using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Globalization;
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

            var titleNode = resultNode.SelectSingleNode(".//div[contains(@class,'card-content')]//h3[contains(@class,'title')]/a");
            var title = titleNode?.InnerText?.Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                title = query.Trim();
            }

            var imageNode = resultNode.SelectSingleNode(".//img[@class='cover']");
            var imageUrl = imageNode?.GetAttributeValue("src", null);

            var actorNodes = doc.DocumentNode.SelectNodes("//div[@class='mb-2 buttons are-small']//a");
            var tagNodes = resultNode.SelectNodes(".//div[contains(@class,'card-content')]//div[contains(@class,'tags')]//a");
            var actors = new List<string>();
            var tags = new List<string>();
            if (actorNodes is not null)
            {
                foreach (var node in actorNodes)
                {
                    var actor = Regex.Replace(node.InnerText ?? string.Empty, @"\s+", " " ).Trim();
                    if (!string.IsNullOrWhiteSpace(actor))
                    {
                        actors.Add(actor);
                    }
                }
            }

            if (tagNodes is not null)
            {
                foreach (var node in tagNodes)
                {
                    var tag = Regex.Replace(node.InnerText ?? string.Empty, @"\s+", " " ).Trim();
                    if (!string.IsNullOrWhiteSpace(tag))
                    {
                        tags.Add(tag);
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

            DateOnly? releaseDate = null;
            var releaseNodes = resultNode.SelectNodes(".//div[contains(@class,'card-content')]//p[contains(@class,'subtitle')]");
            if (releaseNodes is not null)
            {
                foreach (var paragraph in releaseNodes)
                {
                    var labelSpan = paragraph.SelectSingleNode(".//span[contains(@class,'has-text-info') and contains(translate(text(),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'release date')]");
                    if (labelSpan is null)
                    {
                        continue;
                    }

                    var rawText = Regex.Replace(paragraph.InnerText ?? string.Empty, @"\s+", " " ).Trim();
                    var labelText = Regex.Replace(labelSpan.InnerText ?? string.Empty, @"\s+", " " ).Trim();
                    if (!string.IsNullOrEmpty(labelText) && rawText.StartsWith(labelText, StringComparison.OrdinalIgnoreCase))
                    {
                        rawText = rawText[labelText.Length..].Trim();
                    }
                    rawText = rawText.TrimStart(':').Trim();

                    if (DateTime.TryParse(rawText, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out var parsed) ||
                        DateTime.TryParse(rawText, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
                    {
                        releaseDate = DateOnly.FromDateTime(parsed.Date);
                        break;
                    }
                }
            }

            var meta = new VideoMeta(
                title,
                releaseDate,
                actors,
                string.Empty,
                tags);

            return new WebVideoMetaResult(meta, thumbnailBytes, thumbnailExtension);
        }
    }
}
