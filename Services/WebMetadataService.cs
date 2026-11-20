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
    public sealed record MetadataEnrichmentResult(VideoEntry? Entry, string? Source, string? Error, bool NoResult)
    {
        public bool IsPartial { get; init; }
    }

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

        public bool RequiresCrawlerSession => _sources.Any(source => source is OneFourOneJavMetaSource);

        public async Task<VideoEntry?> EnrichAsync(VideoEntry entry, string query, CancellationToken cancellationToken) =>
            (await FetchEnrichmentAsync(entry, query, cancellationToken).ConfigureAwait(false)).Entry;

        public async Task<MetadataEnrichmentResult> FetchEnrichmentAsync(VideoEntry entry, string query, CancellationToken cancellationToken)
        {
            if (entry is null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            var normalizedQuery = NormalizeQuery(query);
            if (string.IsNullOrWhiteSpace(normalizedQuery))
            {
                AppLogger.Info("Metadata enrichment skipped: query is empty after normalization.");
                return new(null, null, "검색어가 없습니다.", true);
            }
            var failures = new List<string>();
            var handledSources = new List<string>();
            foreach (var source in _sources)
            {
                if (!source.CanHandle(normalizedQuery))
                {
                    continue;
                }

                handledSources.Add(source.Name);
                string? ownedThumbnail = null;
                try
                {
                    AppLogger.Info($"Requesting metadata from {source.Name} for '{normalizedQuery}'.");
                    string? sourceWarning = null;
                    var sourcePartial = false;
                    WebVideoMetaResult? result;
                    if (source is OneFourOneJavMetaSource crawler)
                    {
                        var detailed = await crawler.FetchDetailedAsync(normalizedQuery, cancellationToken).ConfigureAwait(false);
                        result = detailed.Result; sourceWarning = detailed.Error; sourcePartial = detailed.IsPartial;
                    }
                    else result = await source.FetchAsync(normalizedQuery, cancellationToken).ConfigureAwait(false);
                    if (result is null)
                    {
                        if (sourceWarning is not null) failures.Add($"{source.Name}: {sourceWarning}");
                        continue;
                    }

                    var meta = MergeMeta(entry.Meta, result.Meta);
                    var thumbnail = entry.Meta.Thumbnail;

                    if (CanFill(entry.Meta, MetadataField.Thumbnail) && result.ThumbnailBytes is { Length: > 0 })
                    {
                        thumbnail = ownedThumbnail = await _thumbnailCache.SaveAsync(
                            result.ThumbnailBytes,
                            result.ThumbnailExtension ?? ".jpg",
                            query,
                            cancellationToken).ConfigureAwait(false);
                    }

                    meta = meta with { Thumbnail = string.IsNullOrWhiteSpace(thumbnail) ? meta.Thumbnail : thumbnail };
                    if (CanFill(entry.Meta, MetadataField.Description) && !string.IsNullOrWhiteSpace(meta.Description))
                        meta = await TranslateDescriptionAsync(meta, cancellationToken).ConfigureAwait(false);

                    cancellationToken.ThrowIfCancellationRequested();
                    AppLogger.Info($"Metadata enrichment succeeded via {source.Name} for '{normalizedQuery}'.");
                    return new(entry with { Meta = meta }, source.Name, sourceWarning, false)
                    { IsPartial = sourcePartial || GalleryQueryEngine.IsIncomplete(meta) };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    if (ownedThumbnail is not null) _thumbnailCache.DiscardOwned(ownedThumbnail);
                    AppLogger.Info($"Metadata enrichment cancelled for '{query}'.");
                    throw;
                }
                catch (Exception ex)
                {
                    if (ownedThumbnail is not null) _thumbnailCache.DiscardOwned(ownedThumbnail);
                    failures.Add($"{source.Name}: {(ex is OperationCanceledException ? "시간 초과" : ex.Message)}");
                    AppLogger.Error($"Metadata enrichment failed via {source.Name} for '{query}'.", ex);
                }
            }

            AppLogger.Info($"No metadata providers returned results for '{normalizedQuery}'.");
            return new(null, handledSources.Count > 0 ? string.Join(", ", handledSources) : null,
                failures.Count > 0 ? string.Join(" / ", failures) : null, failures.Count == 0);
        }

        public async Task<WebVideoMetaResult> TranslateResultDescriptionAsync(WebVideoMetaResult result, CancellationToken cancellationToken)
        {
            var meta = await TranslateDescriptionAsync(result.Meta, cancellationToken).ConfigureAwait(false);
            return result with { Meta = meta };
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

        public void AcceptCandidate(VideoEntry candidate) => _thumbnailCache.AcceptOwned(candidate.Meta.Thumbnail);
        public void DiscardCandidate(VideoEntry candidate) => _thumbnailCache.DiscardOwned(candidate.Meta.Thumbnail);

        public static bool CanFill(VideoMeta original, MetadataField field)
        {
            if (original.ManualFieldLocks.Contains(field)) return false;
            return field switch
            {
                MetadataField.Title => string.IsNullOrWhiteSpace(original.Title),
                MetadataField.Date => original.Date is null && string.IsNullOrWhiteSpace(original.RawDate),
                MetadataField.Actors => original.Actors.Count == 0,
                MetadataField.Tags => original.Tags.Count == 0,
                MetadataField.Thumbnail => string.IsNullOrWhiteSpace(original.Thumbnail),
                MetadataField.Description => string.IsNullOrWhiteSpace(original.Description),
                MetadataField.Studio => string.IsNullOrWhiteSpace(original.Studio),
                MetadataField.Genres => original.Genres.Count == 0,
                _ => false
            };
        }

        public static VideoMeta MergeMeta(VideoMeta original, VideoMeta incoming) => original with
        {
            Title = CanFill(original, MetadataField.Title) && !string.IsNullOrWhiteSpace(incoming.Title) ? incoming.Title : original.Title,
            Date = CanFill(original, MetadataField.Date) ? incoming.Date : original.Date,
            RawDate = CanFill(original, MetadataField.Date) && incoming.Date.HasValue ? null : original.RawDate,
            Actors = CanFill(original, MetadataField.Actors) && incoming.Actors.Count > 0 ? incoming.Actors : original.Actors,
            Tags = CanFill(original, MetadataField.Tags) && incoming.Tags.Count > 0 ? incoming.Tags : original.Tags,
            Thumbnail = CanFill(original, MetadataField.Thumbnail) && !string.IsNullOrWhiteSpace(incoming.Thumbnail) ? incoming.Thumbnail : original.Thumbnail,
            Description = CanFill(original, MetadataField.Description) && !string.IsNullOrWhiteSpace(incoming.Description) ? incoming.Description : original.Description,
            Studio = CanFill(original, MetadataField.Studio) && !string.IsNullOrWhiteSpace(incoming.Studio) ? incoming.Studio : original.Studio,
            Genres = CanFill(original, MetadataField.Genres) && incoming.Genres.Count > 0 ? incoming.Genres : original.Genres
        };
    }
}
