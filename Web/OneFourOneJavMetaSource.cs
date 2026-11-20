using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Web
{
    public sealed record OneFourOneJavFetchResult(WebVideoMetaResult? Result, string? Error, bool IsPartial);

    public sealed class OneFourOneJavMetaSource : IWebVideoMetaSource
    {
        private const string SearchBaseUrl = "https://www.141jav.com/search/";
        private readonly IOneFourOneJavCrawlerSessionProvider _sessionProvider;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _fetchGate = new(1, 1);

        public OneFourOneJavMetaSource(IOneFourOneJavCrawlerSessionProvider sessionProvider, HttpClient? httpClient = null)
        {
            _sessionProvider = sessionProvider ?? throw new ArgumentNullException(nameof(sessionProvider));
            _httpClient = httpClient ?? new HttpClient();
        }

        public string Name => "141Jav";

        public bool CanHandle(string query) => !string.IsNullOrWhiteSpace(query);

        public async Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken) =>
            (await FetchDetailedAsync(query, cancellationToken).ConfigureAwait(false)).Result;

        public async Task<OneFourOneJavFetchResult> FetchDetailedAsync(string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new(null, null, false);
            }

            await _fetchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var session = _sessionProvider.CurrentSession;
                if (session is null)
                {
                    AppLogger.Info("[141Jav] Crawler session is not available.");
                    return new(null, "수집기 세션이 없습니다.", false);
                }

                var searchUrl = SearchBaseUrl + Uri.EscapeDataString(query);
                if (!await session.NavigateToAsync(searchUrl, cancellationToken).ConfigureAwait(false))
                {
                    AppLogger.Info($"[141Jav] Navigation failed for '{query}'.");
                    return new(null, "검색 페이지로 이동하지 못했습니다.", false);
                }

                var metadata = await session.TryGetMetadataAsync(cancellationToken).ConfigureAwait(false);
                var thumbnailUrl = await session.TryGetThumbnailUrlAsync(cancellationToken).ConfigureAwait(false);
                var thumbnail = await DownloadThumbnailAsync(thumbnailUrl, cancellationToken).ConfigureAwait(false);

                if (!HasMetadataValue(metadata) && thumbnail.Bytes is null)
                {
                    AppLogger.Info($"[141Jav] No metadata values found for '{query}'.");
                    return new(null, thumbnail.Error, false);
                }

                var meta = metadata is null
                    ? new VideoMeta(string.Empty, null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty)
                    : new VideoMeta(
                        string.Empty,
                        metadata.ReleaseDate is DateTime releaseDate ? DateOnly.FromDateTime(releaseDate.Date) : null,
                        metadata.Actors,
                        string.Empty,
                        metadata.Tags,
                        metadata.Description ?? string.Empty);

                return new(new WebVideoMetaResult(meta, thumbnail.Bytes, thumbnail.Extension), thumbnail.Error, thumbnail.Error is not null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[141Jav] Metadata source failed for '{query}'.", ex);
                return new(null, ex is OperationCanceledException ? "수집 시간이 초과되었습니다." : ex.Message, false);
            }
            finally
            {
                _fetchGate.Release();
            }
        }

        private async Task<(byte[]? Bytes, string? Extension, string? Error)> DownloadThumbnailAsync(string? thumbnailUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                return (null, null, null);
            }

            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(thumbnailUrl, cancellationToken).ConfigureAwait(false);
                return bytes.Length == 0 ? (null, null, "썸네일 응답이 비어 있습니다.") : (bytes, GetThumbnailExtension(thumbnailUrl), null);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                AppLogger.Error($"[141Jav] Failed to download thumbnail from {thumbnailUrl}.", ex);
                return (null, null, "썸네일 다운로드 시간이 초과되었습니다.");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException)
            {
                AppLogger.Error($"[141Jav] Failed to download thumbnail from {thumbnailUrl}.", ex);
                return (null, null, $"썸네일 다운로드 실패: {ex.Message}");
            }
        }

        private static bool HasMetadataValue(OneFourOneJavCrawler.CrawlerMetadata? metadata)
        {
            if (metadata is null)
            {
                return false;
            }

            return metadata.ReleaseDate is not null
                || metadata.Actors.Count > 0
                || metadata.Tags.Count > 0
                || !string.IsNullOrWhiteSpace(metadata.Description);
        }

        private static string GetThumbnailExtension(string thumbnailUrl)
        {
            string? candidate = null;
            if (Uri.TryCreate(thumbnailUrl, UriKind.Absolute, out var uri))
            {
                candidate = Path.GetExtension(uri.AbsolutePath);
            }
            else
            {
                candidate = Path.GetExtension(thumbnailUrl);
            }

            return string.IsNullOrWhiteSpace(candidate) ? ".jpg" : candidate;
        }
    }
}
