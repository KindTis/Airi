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

        public WebMetadataService(IEnumerable<IWebVideoMetaSource> sources, ThumbnailCache thumbnailCache)
        {
            _sources = sources?.ToList() ?? throw new ArgumentNullException(nameof(sources));
            _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
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

            return new VideoMeta(title, date, actors, thumbnail, tags);
        }
    }
}

