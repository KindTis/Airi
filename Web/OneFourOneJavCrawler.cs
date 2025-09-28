using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Airi.Infrastructure;
using Airi.Services;
using OpenQA.Selenium;

namespace Airi.Web
{
    public sealed class OneFourOneJavCrawler
    {
        private readonly ITextTranslationService _translationService;
        private readonly string _targetLanguageCode;

        public OneFourOneJavCrawler()
            : this(NullTranslationService.Instance, null)
        {
        }

        public OneFourOneJavCrawler(ITextTranslationService translationService, string? targetLanguageCode)
        {
            _translationService = translationService ?? throw new ArgumentNullException(nameof(translationService));
            _targetLanguageCode = string.IsNullOrWhiteSpace(targetLanguageCode) ? string.Empty : targetLanguageCode;
        }

        public sealed record CrawlerMetadata(DateTime? ReleaseDate, IReadOnlyList<string> Tags, IReadOnlyList<string> Actors, string Description);

        public async Task<CrawlerMetadata?> TryParseMetadataAsync(IWebDriver driver, CancellationToken cancellationToken = default)
        {
            if (driver is null)
            {
                throw new ArgumentNullException(nameof(driver));
            }

            try
            {
                var metadata = await Task.Run(() => ExtractMetadata(driver), cancellationToken).ConfigureAwait(false);
                if (metadata is null)
                {
                    return null;
                }

                var translatedDescription = await TranslateDescriptionAsync(metadata.Description, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(translatedDescription, metadata.Description, StringComparison.Ordinal))
                {
                    return metadata with { Description = translatedDescription };
                }

                return metadata;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NoSuchElementException)
            {
                return null;
            }
            catch (WebDriverException ex)
            {
                AppLogger.Error("[141Jav] Failed to parse metadata from crawler.", ex);
                return null;
            }
        }

        private static CrawlerMetadata? ExtractMetadata(IWebDriver driver)
        {
            var card = driver.FindElements(By.CssSelector("div.card.mb-3")).FirstOrDefault();
            if (card is null)
            {
                return null;
            }

            var content = card.FindElements(By.CssSelector("div.card-content")).FirstOrDefault();
            if (content is null)
            {
                return null;
            }

            var releaseDate = ExtractReleaseDate(content);
            var tags = ExtractTexts(content.FindElements(By.CssSelector("div.tags a.tag")));
            var actors = ExtractTexts(content.FindElements(By.CssSelector("div.panel a.panel-block")));

            var descriptionElement = content.FindElements(By.CssSelector(".level"))
                .FirstOrDefault(element => !string.IsNullOrWhiteSpace(element.Text));
            var description = descriptionElement?.Text?.Trim() ?? string.Empty;

            return new CrawlerMetadata(releaseDate, tags, actors, description);
        }

        private static DateTime? ExtractReleaseDate(ISearchContext context)
        {
            var dateContainer = context.FindElements(By.CssSelector("p.subtitle.is-6")).FirstOrDefault();
            if (dateContainer is null)
            {
                return null;
            }

            IWebElement? anchor = null;
            try
            {
                anchor = dateContainer.FindElements(By.TagName("a")).FirstOrDefault();
            }
            catch (NoSuchElementException)
            {
            }

            var target = anchor ?? dateContainer;
            var fromHref = ParseDateFromHref(target.GetAttribute("href"));
            if (fromHref is not null)
            {
                return fromHref;
            }

            return ParseDateFromText(target.Text);
        }

        private static DateTime? ParseDateFromHref(string? href)
        {
            if (string.IsNullOrWhiteSpace(href))
            {
                return null;
            }

            var match = Regex.Match(href, @"(\d{4})/(\d{2})/(\d{2})");
            if (!match.Success)
            {
                return null;
            }

            if (int.TryParse(match.Groups[1].Value, out var year)
                && int.TryParse(match.Groups[2].Value, out var month)
                && int.TryParse(match.Groups[3].Value, out var day))
            {
                try
                {
                    return new DateTime(year, month, day);
                }
                catch (ArgumentOutOfRangeException)
                {
                    return null;
                }
            }

            return null;
        }

        private static DateTime? ParseDateFromText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var normalized = text.Trim();

            if (DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var result))
            {
                return result;
            }

            if (DateTime.TryParse(normalized, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out result))
            {
                return result;
            }

            return null;
        }

        private static IReadOnlyList<string> ExtractTexts(IEnumerable<IWebElement> elements)
        {
            if (elements is null)
            {
                return Array.Empty<string>();
            }

            var values = elements
                .Select(element => element.Text?.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values.Count == 0 ? Array.Empty<string>() : values;
        }

        public async Task<string?> TryGetThumbnailUrlAsync(IWebDriver driver, CancellationToken cancellationToken = default)
        {
            if (driver is null)
            {
                throw new ArgumentNullException(nameof(driver));
            }

            try
            {
                return await Task.Run(() => ExtractThumbnailUrl(driver), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (NoSuchElementException)
            {
                return null;
            }
            catch (WebDriverException ex)
            {
                AppLogger.Error("[141Jav] Failed to extract thumbnail URL from crawler.", ex);
                return null;
            }
        }

        private static string? ExtractThumbnailUrl(IWebDriver driver)
        {
            var card = driver.FindElements(By.CssSelector("div.card.mb-3")).FirstOrDefault();
            if (card is null)
            {
                return null;
            }

            var image = card.FindElements(By.CssSelector("img.image")).FirstOrDefault();
            if (image is null)
            {
                return null;
            }

            var source = image.GetAttribute("src");
            if (string.IsNullOrWhiteSpace(source))
            {
                source = image.GetAttribute("data-src");
            }

            if (string.IsNullOrWhiteSpace(source))
            {
                var srcset = image.GetAttribute("srcset");
                if (!string.IsNullOrWhiteSpace(srcset))
                {
                    source = srcset
                        .Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(token => token.StartsWith("http", StringComparison.OrdinalIgnoreCase));
                }
            }

            return string.IsNullOrWhiteSpace(source) ? null : source;
        }

        private async Task<string> TranslateDescriptionAsync(string description, CancellationToken cancellationToken)
        {
            if (!_translationService.IsEnabled || string.IsNullOrWhiteSpace(_targetLanguageCode))
            {
                return description;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                return string.Empty;
            }

            try
            {
                var translated = await _translationService
                    .TranslateAsync(description, null, _targetLanguageCode, cancellationToken)
                    .ConfigureAwait(false);

                return string.IsNullOrWhiteSpace(translated) ? description : translated;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error("[141Jav] Failed to translate description.", ex);
                return description;
            }
        }
    }
}
