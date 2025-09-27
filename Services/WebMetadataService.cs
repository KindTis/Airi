using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Web;

namespace Airi.Services
{
    public sealed class WebMetadataService
    {
        private readonly IReadOnlyList<IWebVideoMetaSource> _sources;
        private readonly ThumbnailCache _thumbnailCache;
        private readonly ITextTranslationService _translationService;
        private readonly string _translationTargetLanguageCode;

        public WebMetadataService(
            IEnumerable<IWebVideoMetaSource> sources,
            ThumbnailCache thumbnailCache,
            ITextTranslationService? translationService = null,
            string translationTargetLanguageCode = "KO")
        {
            _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
            _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
            _translationService = translationService ?? NullTranslationService.Instance;
            _translationTargetLanguageCode = translationTargetLanguageCode ?? string.Empty;
        }

        public async Task<VideoEntry?> EnrichAsync(VideoEntry entry, string query, CancellationToken cancellationToken)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var normalizedQuery = NormalizeQuery(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                AppLogger.Info("Metadata enrichment skipped: query is empty after normalization.");
                return null;
            }

            foreach (var source in _sources)
            {
                if (!source.CanHandle(normalizedQuery))
                {
                    continue;
                }

                try
                {
                    AppLogger.Info($"Requesting metadata from {source.Name} for '{normalizedQuery}'.");
                    var result = await source.FetchAsync(normalizedQuery, cancellationToken).ConfigureAwait(false);
                    if (result is null)
                    {
                        continue;
                    }

                    var meta = MergeMeta(entry.Meta, result.Meta);
                    var thumbnail = entry.Meta.Thumbnail;

                    if (result.ThumbnailBytes is { Length: > 0 })
                    {
                        thumbnail = await _thumbnailCache.SaveAsync(
                            result.ThumbnailBytes,
                            result.ThumbnailExtension ?? ".jpg",
                            query,
                            cancellationToken).ConfigureAwait(false);
                    }

                    meta = meta with { Thumbnail = string.IsNullOrWhiteSpace(thumbnail) ? meta.Thumbnail : thumbnail };
                    meta = await TranslateDescriptionAsync(meta, cancellationToken).ConfigureAwait(false);

                    AppLogger.Info($"Metadata enrichment succeeded via {source.Name} for '{normalizedQuery}'.");
                    return entry with { Meta = meta };
                }
                catch (OperationCanceledException)
                {
                    AppLogger.Info($"Metadata enrichment cancelled for '{query}'.");
                    throw;
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Metadata enrichment failed via {source.Name} for '{query}'.", ex);
                }
            }

            AppLogger.Info($"No metadata providers returned results for '{normalizedQuery}'.");
            return null;
        }

        private async Task<VideoMeta> TranslateDescriptionAsync(VideoMeta meta, CancellationToken cancellationToken)
        {
            if (!_translationService.IsEnabled || string.IsNullOrWhiteSpace(_translationTargetLanguageCode))
            {
                AppLogger.Info("Description translation skipped: translation service disabled or target not set.");
                return meta;
            }

            if (string.IsNullOrWhiteSpace(meta.Description))
            {
                AppLogger.Info("Description translation skipped: description is empty.");
                return meta;
            }

            AppLogger.Info($"Translating description to {_translationTargetLanguageCode} (length {meta.Description.Length}).");

            try
            {
                var translated = await _translationService.TranslateAsync(meta.Description, null, _translationTargetLanguageCode, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(translated) && !string.Equals(translated, meta.Description, StringComparison.Ordinal))
                {
                    AppLogger.Info("Description translation succeeded.");
                    return meta with { Description = translated };
                }

                AppLogger.Info("Description translation returned original content.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Info("Description translation cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Description translation failed for target '{_translationTargetLanguageCode}'.", ex);
            }

            return meta;
        }

        private static string NormalizeQuery(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return LibraryPathHelper.NormalizeCode(value);
        }

        private static VideoMeta MergeMeta(VideoMeta original, VideoMeta incoming)
        {
            var title = string.IsNullOrWhiteSpace(incoming.Title) ? original.Title : incoming.Title;
            var date = incoming.Date ?? original.Date;
            var actors = incoming.Actors.Count > 0 ? incoming.Actors : original.Actors;
            var tags = incoming.Tags.Count > 0 ? incoming.Tags : original.Tags;
            var thumbnail = string.IsNullOrWhiteSpace(incoming.Thumbnail) ? original.Thumbnail : incoming.Thumbnail;
            var description = string.IsNullOrWhiteSpace(incoming.Description) ? original.Description : incoming.Description;

            return new VideoMeta(title, date, actors, thumbnail, tags, description);
        }
    }
}

