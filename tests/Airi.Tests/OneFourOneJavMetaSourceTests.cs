using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Airi.Web;
using Xunit;

namespace Airi.Tests
{
    public sealed class OneFourOneJavMetaSourceTests
    {
        [Fact]
        public async Task FetchAsync_WhenSessionMissing_ReturnsNull()
        {
            var provider = new CrawlerSessionProvider();
            var source = new OneFourOneJavMetaSource(provider);

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task FetchAsync_WhenNavigationFails_ReturnsNullAndUsesSearchUrl()
        {
            var session = new FakeSession { NavigateResult = false };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider);

            var result = await source.FetchAsync("ABC 123", CancellationToken.None);

            Assert.Null(result);
            Assert.Equal("https://www.141jav.com/search/ABC%20123", session.NavigatedUrls.Single());
        }

        [Fact]
        public async Task FetchAsync_WhenMetadataAndThumbnailExist_ReturnsBoth()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    new DateTime(2024, 1, 2),
                    new[] { "Tag One" },
                    new[] { "Actor One" },
                    "Description"),
                ThumbnailUrl = "https://example.test/images/sample.webp?size=large"
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider, CreateHttpClient(new byte[] { 1, 2, 3 }));

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(new DateOnly(2024, 1, 2), result!.Meta.Date);
            Assert.Equal(new[] { "Actor One" }, result.Meta.Actors);
            Assert.Equal(new[] { "Tag One" }, result.Meta.Tags);
            Assert.Equal("Description", result.Meta.Description);
            Assert.Equal(new byte[] { 1, 2, 3 }, result.ThumbnailBytes);
            Assert.Equal(".webp", result.ThumbnailExtension);
        }

        [Fact]
        public async Task FetchAsync_WhenMetadataOnly_ReturnsMetadataWithoutThumbnailBytes()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    new DateTime(2024, 1, 2),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty)
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider);

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(new DateOnly(2024, 1, 2), result!.Meta.Date);
            Assert.Null(result.ThumbnailBytes);
        }

        [Fact]
        public async Task FetchAsync_WhenThumbnailOnly_ReturnsEmptyMetaAndThumbnailBytes()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty),
                ThumbnailUrl = "https://example.test/images/sample"
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider, CreateHttpClient(new byte[] { 4, 5, 6 }));

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Null(result!.Meta.Date);
            Assert.Empty(result.Meta.Actors);
            Assert.Empty(result.Meta.Tags);
            Assert.Empty(result.Meta.Description);
            Assert.Equal(new byte[] { 4, 5, 6 }, result.ThumbnailBytes);
            Assert.Equal(".jpg", result.ThumbnailExtension);
        }

        [Fact]
        public async Task FetchAsync_WhenMetadataEmptyAndThumbnailMissing_ReturnsNull()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    null,
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty)
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider);

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.Null(result);
        }

        [Fact]
        public async Task FetchAsync_WhenThumbnailDownloadFails_ReturnsMetadataOnly()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    new DateTime(2024, 1, 2),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty),
                ThumbnailUrl = "https://example.test/images/sample.jpg"
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider, CreateHttpClient(new HttpRequestException("failed")));

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(new DateOnly(2024, 1, 2), result!.Meta.Date);
            Assert.Null(result.ThumbnailBytes);
        }

        [Fact]
        public async Task FetchAsync_WhenThumbnailDownloadTimesOutWithoutCallerCancellation_ReturnsMetadataOnly()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    new DateTime(2024, 1, 2),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty),
                ThumbnailUrl = "https://example.test/images/sample.jpg"
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider, CreateHttpClient(new TaskCanceledException("timeout")));

            var result = await source.FetchAsync("ABC123", CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(new DateOnly(2024, 1, 2), result!.Meta.Date);
            Assert.Null(result.ThumbnailBytes);
        }

        [Fact]
        public async Task FetchAsync_WhenConcurrent_DoesNotOverlapNavigationOrParse()
        {
            var session = new FakeSession
            {
                Metadata = new OneFourOneJavCrawler.CrawlerMetadata(
                    new DateTime(2024, 1, 2),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty),
                OperationDelay = TimeSpan.FromMilliseconds(30)
            };
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider);

            var first = source.FetchAsync("ABC123", CancellationToken.None);
            var second = source.FetchAsync("DEF456", CancellationToken.None);
            await Task.WhenAll(first, second);

            Assert.Equal(2, session.NavigatedUrls.Count);
            Assert.Equal(1, session.MaxActiveOperations);
        }

        [Fact]
        public async Task FetchAsync_WhenCancelledWhileWaitingForLock_ThrowsAndDoesNotNavigateSecondFetch()
        {
            var session = new BlockingSession();
            var provider = new CrawlerSessionProvider();
            provider.SetSession(session);
            var source = new OneFourOneJavMetaSource(provider);

            var first = source.FetchAsync("ABC123", CancellationToken.None);
            await session.NavigationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            using var cts = new CancellationTokenSource();
            var second = source.FetchAsync("DEF456", cts.Token);
            cts.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => second);
            Assert.Single(session.NavigatedUrls);

            session.ReleaseNavigation.SetResult();
            await first;
        }

        private static HttpClient CreateHttpClient(byte[] bytes)
        {
            return new HttpClient(new StubHttpMessageHandler(bytes));
        }

        private static HttpClient CreateHttpClient(Exception exception)
        {
            return new HttpClient(new StubHttpMessageHandler(exception));
        }

        private sealed class FakeSession : IOneFourOneJavCrawlerSession
        {
            private int _activeOperations;

            public List<string> NavigatedUrls { get; } = new();
            public int MaxActiveOperations { get; private set; }
            public bool NavigateResult { get; set; } = true;
            public OneFourOneJavCrawler.CrawlerMetadata? Metadata { get; set; }
            public string? ThumbnailUrl { get; set; }
            public TimeSpan OperationDelay { get; set; }

            public async Task<bool> NavigateToAsync(string url, CancellationToken cancellationToken = default)
            {
                await RunOperationAsync(cancellationToken).ConfigureAwait(false);
                NavigatedUrls.Add(url);
                return NavigateResult;
            }

            public async Task<OneFourOneJavCrawler.CrawlerMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default)
            {
                await RunOperationAsync(cancellationToken).ConfigureAwait(false);
                return Metadata;
            }

            public Task<string?> TryGetThumbnailUrlAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ThumbnailUrl);
            }

            private async Task RunOperationAsync(CancellationToken cancellationToken)
            {
                var active = Interlocked.Increment(ref _activeOperations);
                MaxActiveOperations = Math.Max(MaxActiveOperations, active);
                try
                {
                    if (OperationDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(OperationDelay, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Decrement(ref _activeOperations);
                }
            }
        }

        private sealed class BlockingSession : IOneFourOneJavCrawlerSession
        {
            public TaskCompletionSource NavigationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource ReleaseNavigation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public List<string> NavigatedUrls { get; } = new();

            public async Task<bool> NavigateToAsync(string url, CancellationToken cancellationToken = default)
            {
                NavigatedUrls.Add(url);
                NavigationStarted.TrySetResult();
                await ReleaseNavigation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return true;
            }

            public Task<OneFourOneJavCrawler.CrawlerMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<OneFourOneJavCrawler.CrawlerMetadata?>(null);
            }

            public Task<string?> TryGetThumbnailUrlAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<string?>(null);
            }
        }

        private sealed class StubHttpMessageHandler : HttpMessageHandler
        {
            private readonly byte[]? _bytes;
            private readonly Exception? _exception;

            public StubHttpMessageHandler(byte[] bytes)
            {
                _bytes = bytes;
            }

            public StubHttpMessageHandler(Exception exception)
            {
                _exception = exception;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (_exception is not null)
                {
                    throw _exception;
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(_bytes ?? Array.Empty<byte>())
                };

                return Task.FromResult(response);
            }
        }
    }
}
