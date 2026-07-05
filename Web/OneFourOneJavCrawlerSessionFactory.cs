using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Infrastructure;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace Airi.Web
{
    public sealed class OneFourOneJavCrawlerSessionFactory : IOneFourOneJavCrawlerSessionFactory
    {
        private const string CrawlerSeedUrl = "https://example.com/";
        private readonly OneFourOneJavCrawler _crawler;

        public OneFourOneJavCrawlerSessionFactory(OneFourOneJavCrawler crawler)
        {
            _crawler = crawler ?? throw new ArgumentNullException(nameof(crawler));
        }

        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default)
        {
            return Task.Run(() => Start(cancellationToken), cancellationToken);
        }

        private OneFourOneJavCrawlerStartResult Start(CancellationToken cancellationToken)
        {
            ChromeDriverService? service = null;
            ChromeDriver? driver = null;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                service = ChromeDriverService.CreateDefaultService();
                service.HideCommandPromptWindow = true;

                var options = new ChromeOptions();
                options.AddArgument("--disable-gpu");
                options.AddArgument("--no-sandbox");
                options.AddArgument("--disable-dev-shm-usage");
                options.AddArgument("--log-level=3");

                driver = new ChromeDriver(service, options);
                driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
                driver.Navigate().GoToUrl(CrawlerSeedUrl);

                var title = driver.Title ?? string.Empty;
                var heading = string.Empty;

                try
                {
                    heading = driver.FindElements(By.TagName("h1"))
                        .Select(element => element.Text)
                        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
                }
                catch (NoSuchElementException)
                {
                    // Not all pages include a heading.
                }

                var highlight = string.IsNullOrWhiteSpace(heading) ? title : heading;
                var summary = string.IsNullOrWhiteSpace(highlight)
                    ? "Crawler opened the page. Close the browser window when you are finished."
                    : $"Crawler opened \"{highlight}\". Close the browser window when you are finished.";

                AppLogger.Info($"Crawler visited {CrawlerSeedUrl} (title: {title}).");

                var session = _crawler.CreateSession(driver);
                var handle = new SeleniumCrawlerSessionHandle(service, driver, session);
                service = null;
                driver = null;

                return new OneFourOneJavCrawlerStartResult(handle, session, summary);
            }
            catch
            {
                Cleanup(driver, service);
                throw;
            }
        }

        private static void Cleanup(ChromeDriver? driver, ChromeDriverService? service)
        {
            if (driver is not null)
            {
                try
                {
                    driver.Quit();
                }
                catch (WebDriverException)
                {
                }

                try
                {
                    driver.Dispose();
                }
                catch (WebDriverException)
                {
                }
            }

            service?.Dispose();
        }

        private sealed class SeleniumCrawlerSessionHandle : IOneFourOneJavCrawlerSessionHandle
        {
            private readonly ChromeDriverService _service;
            private readonly ChromeDriver _driver;

            public SeleniumCrawlerSessionHandle(
                ChromeDriverService service,
                ChromeDriver driver,
                IOneFourOneJavCrawlerSession session)
            {
                _service = service ?? throw new ArgumentNullException(nameof(service));
                _driver = driver ?? throw new ArgumentNullException(nameof(driver));
                Session = session ?? throw new ArgumentNullException(nameof(session));
            }

            public IOneFourOneJavCrawlerSession Session { get; }

            public bool IsBrowserOpen()
            {
                try
                {
                    return _driver.WindowHandles.Count > 0;
                }
                catch (WebDriverException)
                {
                    return false;
                }
            }

            public void Dispose()
            {
                Cleanup(_driver, _service);
            }
        }
    }
}
