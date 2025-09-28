using System;
using System.Linq;
using OpenQA.Selenium;

namespace Airi.Web
{
    public sealed class OneFourOneJavCrawler
    {
        private const string TargetDomain = "141jav.com";

        public string? TryGetThumbnailUrl(IWebDriver driver)
        {
            if (driver is null)
            {
                throw new ArgumentNullException(nameof(driver));
            }

            if (!IsTargetDomain(driver.Url))
            {
                return null;
            }

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

        private static bool IsTargetDomain(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            return uri.Host.EndsWith(TargetDomain, StringComparison.OrdinalIgnoreCase);
        }
    }
}

