using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Web
{
    public sealed class NanoJavMetaSource : IWebVideoMetaSource
    {
        private static readonly Uri BaseUri = new("https://www.nanojav.com/");
        private readonly HttpClient _httpClient;

        public NanoJavMetaSource(HttpClient? httpClient = null)
        {
            _httpClient = httpClient ?? new HttpClient();
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Airi)");
        }

        public string Name => "NanoJav";

        public bool CanHandle(string query) => !string.IsNullOrWhiteSpace(query);

        public async Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            var searchUri = new Uri(BaseUri, $"jav/search/?q={Uri.EscapeDataString(query)}");
            AppLogger.Info($"[{Name}] Requesting {searchUri}");

            using var response = await _httpClient.GetAsync(searchUri, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                AppLogger.Error($"[{Name}] Request failed with status {(int)response.StatusCode}");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var resultNode = doc.DocumentNode.SelectSingleNode("//div[contains(@class,'mb-5')]");
            if (resultNode is null)
            {
                AppLogger.Info($"[{Name}] No search results for '{query}'.");
                return null;
            }

            var titleNode = resultNode.SelectSingleNode(".//a[contains(@class,'has-text-weight-bold')]");
            var title = titleNode?.InnerText?.Trim() ?? query.Trim();

            var imageNode = resultNode.SelectSingleNode(".//img[@class='cover']");
            var imageUrl = imageNode?.GetAttributeValue("src", null);

            var actorNodes = doc.DocumentNode.SelectNodes("//div[@class='mb-2 buttons are-small']//a");
            var actors = new List<string>();
            if (actorNodes is not null)
            {
                foreach (var node in actorNodes)
                {
                    var actor = Regex.Replace(node.InnerText ?? string.Empty, "\\s+", " ").Trim();
                    if (!string.IsNullOrWhiteSpace(actor))
                    {
                        actors.Add(actor);
                    }
                }
            }

            byte[]? thumbnailBytes = null;
            string? thumbnailExtension = null;

            if (!string.IsNullOrWhiteSpace(imageUrl))
            {
                try
                {
                    thumbnailBytes = await _httpClient.GetByteArrayAsync(imageUrl, cancellationToken).ConfigureAwait(false);
                    thumbnailExtension = Path.GetExtension(new Uri(imageUrl).AbsolutePath);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    AppLogger.Error($"[{Name}] Failed to download thumbnail from {imageUrl}", ex);
                }
            }

            var meta = new VideoMeta(
                title,
                null,
                actors,
                string.Empty,
                Array.Empty<string>());

            return new WebVideoMetaResult(meta, thumbnailBytes, thumbnailExtension);
        }
    }
}
