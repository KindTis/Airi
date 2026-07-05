using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;
using Xunit;

namespace Airi.Tests
{
    public sealed class MainViewModelCrawlerTests
    {
        [Fact]
        public void TryFetchOneFourOneJavMetadataAsync_WhenSessionMissing_StartsFactoryAndSetsProvider()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();

                var result = await viewModel.TryFetchOneFourOneJavMetadataAsync("ABC123");

                Assert.NotNull(result);
                Assert.Equal(1, fixture.Factory.StartCount);
                Assert.Same(fixture.Session, fixture.Provider.CurrentSession);
                Assert.True(viewModel.IsCrawlerRunning);
                Assert.False(viewModel.StartCrawlerCommand.CanExecute(null));
            });
        }

        [Fact]
        public void TryFetchOneFourOneJavMetadataAsync_WhenFactoryFails_ClearsProviderAndStatus()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                fixture.Factory.StartOverride = _ => Task.FromException<OneFourOneJavCrawlerStartResult>(new InvalidOperationException("boom"));
                var viewModel = fixture.CreateViewModel();

                var result = await viewModel.TryFetchOneFourOneJavMetadataAsync("ABC123");

                Assert.Null(result);
                Assert.Null(fixture.Provider.CurrentSession);
                Assert.False(viewModel.IsCrawlerRunning);
                Assert.Contains("Crawler failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
            });
        }

        [Fact]
        public void TryFetchOneFourOneJavMetadataAsync_WhenConcurrent_StartsFactoryOnce()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                fixture.Factory.StartOverride = async cancellationToken =>
                {
                    await Task.Delay(50, cancellationToken);
                    return fixture.Factory.CreateResult();
                };
                var viewModel = fixture.CreateViewModel();

                var first = viewModel.TryFetchOneFourOneJavMetadataAsync("ABC123");
                var second = viewModel.TryFetchOneFourOneJavMetadataAsync("DEF456");
                await Task.WhenAll(first, second);

                Assert.Equal(1, fixture.Factory.StartCount);
                Assert.Same(fixture.Session, fixture.Provider.CurrentSession);
            });
        }

        [Fact]
        public void DisposeCrawler_ClearsProviderAndDisposesHandle()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();
                await viewModel.TryFetchOneFourOneJavMetadataAsync("ABC123");

                InvokePrivate(viewModel, "DisposeCrawler");

                Assert.Null(fixture.Provider.CurrentSession);
                Assert.True(fixture.Handle.Disposed);
            });
        }

        [Fact]
        public void DisposeCrawlerIfCurrent_WhenHandleChanged_DoesNotClearNewSession()
        {
            RunInStaAsync(() =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();
                var staleSession = new FakeSession();
                var staleHandle = new FakeHandle(staleSession);
                var currentSession = new FakeSession();
                var currentHandle = new FakeHandle(currentSession);
                fixture.Provider.SetSession(currentSession);
                SetPrivateField(viewModel, "_crawlerSession", currentSession);
                SetPrivateField(viewModel, "_crawlerHandle", currentHandle);

                var disposed = InvokePrivate<bool>(viewModel, "DisposeCrawlerIfCurrent", staleHandle);

                Assert.False(disposed);
                Assert.Same(currentSession, fixture.Provider.CurrentSession);
                Assert.False(currentHandle.Disposed);
                return Task.CompletedTask;
            });
        }

        [Fact]
        public void DisposeCrawlerIfCurrent_WhenCleanupWaitsBehindStateChange_DoesNotClearNewSession()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();
                var staleSession = new FakeSession();
                var staleHandle = new FakeHandle(staleSession);
                var currentSession = new FakeSession();
                var currentHandle = new FakeHandle(currentSession);
                var crawlerStateLock = GetPrivateField<object>(viewModel, "_crawlerStateLock");
                fixture.Provider.SetSession(staleSession);
                SetPrivateField(viewModel, "_crawlerSession", staleSession);
                SetPrivateField(viewModel, "_crawlerHandle", staleHandle);

                Monitor.Enter(crawlerStateLock);
                try
                {
                    var cleanup = Task.Run(() => InvokePrivate<bool>(viewModel, "DisposeCrawlerIfCurrent", staleHandle));
                    SetPrivateField(viewModel, "_crawlerSession", currentSession);
                    SetPrivateField(viewModel, "_crawlerHandle", currentHandle);
                    fixture.Provider.SetSession(currentSession);

                    Monitor.Exit(crawlerStateLock);
                    crawlerStateLock = null!;

                    var disposed = await cleanup.WaitAsync(TimeSpan.FromSeconds(5));

                    Assert.False(disposed);
                    Assert.Same(currentSession, fixture.Provider.CurrentSession);
                    Assert.False(currentHandle.Disposed);
                }
                finally
                {
                    if (crawlerStateLock is not null)
                    {
                        Monitor.Exit(crawlerStateLock);
                    }
                }
            });
        }

        [Fact]
        public void StartCrawlerCommand_CanExecuteFollowsRunningStateAndMonitorCleanup()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();

                Assert.True(viewModel.StartCrawlerCommand.CanExecute(null));

                await viewModel.TryFetchOneFourOneJavMetadataAsync("ABC123");
                Assert.False(viewModel.StartCrawlerCommand.CanExecute(null));

                fixture.Handle.IsOpen = false;
                await WaitUntilAsync(() => fixture.Provider.CurrentSession is null && !viewModel.IsCrawlerRunning);

                Assert.True(fixture.Handle.Disposed);
                Assert.True(viewModel.StartCrawlerCommand.CanExecute(null));
            });
        }

        [Fact]
        public void TryFetchOneFourOneJavMetadataAsync_WhenFactoryCancelled_PropagatesAndLeavesProviderEmpty()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                using var cts = new CancellationTokenSource();
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Factory.StartOverride = async cancellationToken =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return fixture.Factory.CreateResult();
                };
                var viewModel = fixture.CreateViewModel();

                var fetch = viewModel.TryFetchOneFourOneJavMetadataAsync("ABC123", cts.Token);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cts.Cancel();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
                Assert.Null(fixture.Provider.CurrentSession);
                Assert.False(viewModel.IsCrawlerRunning);
            });
        }

        [Fact]
        public void FetchMissingMetadataWithCrawlerAsync_WhenAlreadyFetching_OnlyUpdatesStatus()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();
                SetPrivateField(viewModel, "_isFetchingMetadata", true);

                await InvokePrivateTask(viewModel, "FetchMissingMetadataWithCrawlerAsync");

                Assert.Equal(0, fixture.Factory.StartCount);
                Assert.Equal("Metadata fetch already in progress.", viewModel.StatusMessage);
            });
        }

        [Fact]
        public void FetchMissingMetadataWithCrawlerAsync_WhenNoMissingThumbnail_OnlyUpdatesStatus()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var metadataSource = new CountingMetaSource(CreateMetaResult("Updated Title"));
                var viewModel = fixture.CreateViewModel(metadataSource);
                SeedLibrary(
                    viewModel,
                    new VideoEntry(
                        "./Videos/abc123.mp4",
                        new VideoMeta("ABC-123", null, Array.Empty<string>(), "cache/thumb.jpg", Array.Empty<string>(), string.Empty),
                        1,
                        DateTime.UtcNow),
                    "file:///cache/thumb.jpg");

                await InvokePrivateTask(viewModel, "FetchMissingMetadataWithCrawlerAsync");

                Assert.Equal(0, fixture.Factory.StartCount);
                Assert.Equal(0, metadataSource.CallCount);
                Assert.Equal("No videos require crawler metadata.", viewModel.StatusMessage);
            });
        }

        [Fact]
        public void FetchMissingMetadataWithCrawlerAsync_WhenCrawlerStartFails_PreservesFailureStatus()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                fixture.Factory.StartOverride = _ => Task.FromException<OneFourOneJavCrawlerStartResult>(new InvalidOperationException("boom"));
                var metadataSource = new CountingMetaSource(CreateMetaResult("Updated Title"));
                var viewModel = fixture.CreateViewModel(metadataSource);
                SeedLibrary(
                    viewModel,
                    new VideoEntry(
                        "./Videos/abc123.mp4",
                        new VideoMeta("ABC-123", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                        1,
                        DateTime.UtcNow),
                    string.Empty);

                await InvokePrivateTask(viewModel, "FetchMissingMetadataWithCrawlerAsync");

                Assert.Equal(1, fixture.Factory.StartCount);
                Assert.Equal(0, metadataSource.CallCount);
                Assert.Contains("Crawler failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
                Assert.False(viewModel.IsFetchingMetadata);
            });
        }

        [Fact]
        public void FetchMissingMetadataWithCrawlerAsync_WhenCrawlerStartInProgress_SecondCallOnlyUpdatesStatus()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var metadataSource = new CountingMetaSource(CreateMetaResult("Updated Title"));
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                fixture.Factory.StartOverride = async cancellationToken =>
                {
                    started.SetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return fixture.Factory.CreateResult();
                };
                var viewModel = fixture.CreateViewModel(metadataSource);
                SeedLibrary(
                    viewModel,
                    new VideoEntry(
                        "./Videos/abc123.mp4",
                        new VideoMeta("ABC-123", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                        1,
                        DateTime.UtcNow),
                    string.Empty);

                var first = InvokePrivateTask(viewModel, "FetchMissingMetadataWithCrawlerAsync");
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

                await InvokePrivateTask(viewModel, "FetchMissingMetadataWithCrawlerAsync");

                Assert.Equal("Metadata fetch already in progress.", viewModel.StatusMessage);
                Assert.Equal(1, fixture.Factory.StartCount);

                release.SetResult();
                await first;
            });
        }

        [Fact]
        public void FetchMissingMetadataWithCrawlerAsync_UsesWebMetadataServiceResultAndSaves()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var metadataSource = new CountingMetaSource(CreateMetaResult("Updated Title"));
                var viewModel = fixture.CreateViewModel(metadataSource);
                SeedLibrary(
                    viewModel,
                    new VideoEntry(
                        "./Videos/abc123.mp4",
                        new VideoMeta("ABC-123", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                        1,
                        DateTime.UtcNow),
                    string.Empty);

                await InvokePrivateTask(viewModel, "FetchMissingMetadataWithCrawlerAsync");

                Assert.Equal(1, fixture.Factory.StartCount);
                Assert.Equal(1, metadataSource.CallCount);
                var video = Assert.Single(viewModel.Videos);
                Assert.Equal("Updated Title", video.Title);
                Assert.Contains("Updated Title", await File.ReadAllTextAsync(fixture.LibraryPath));
            });
        }

        [Fact]
        public void ProcessMetadataForPathAsync_WhenSessionMissing_DoesNotStartCrawlerAndAllowsFallback()
        {
            RunInStaAsync(async () =>
            {
                using var fixture = new ViewModelFixture();
                var fallback = new CountingMetaSource(CreateMetaResult("Fallback Title"));
                var oneFourOneJavSource = new OneFourOneJavMetaSource(fixture.Provider);
                var viewModel = fixture.CreateViewModel(fallback, new IWebVideoMetaSource[] { oneFourOneJavSource, fallback });
                SeedLibrary(
                    viewModel,
                    new VideoEntry(
                        "./Videos/abc123.mp4",
                        new VideoMeta("ABC-123", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                        1,
                        DateTime.UtcNow),
                    string.Empty);

                await InvokePrivateTask(viewModel, "ProcessMetadataForPathAsync", "./Videos/abc123.mp4");

                Assert.Equal(0, fixture.Factory.StartCount);
                Assert.Equal(1, fallback.CallCount);
                var video = Assert.Single(viewModel.Videos);
                Assert.Equal("Fallback Title", video.Title);
            });
        }

        private static void InvokePrivate(MainViewModel viewModel, string methodName)
        {
            var method = typeof(MainViewModel).GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(viewModel, Array.Empty<object>());
        }

        private static void InvokePrivate(MainViewModel viewModel, string methodName, params object[] args)
        {
            var method = typeof(MainViewModel).GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            method!.Invoke(viewModel, args);
        }

        private static T InvokePrivate<T>(MainViewModel viewModel, string methodName, params object[] args)
        {
            var method = typeof(MainViewModel).GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            return Assert.IsType<T>(method!.Invoke(viewModel, args));
        }

        private static async Task InvokePrivateTask(MainViewModel viewModel, string methodName, params object[] args)
        {
            var method = typeof(MainViewModel).GetMethod(methodName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(method);
            var task = Assert.IsAssignableFrom<Task>(method!.Invoke(viewModel, args));
            await task.ConfigureAwait(false);
        }

        private static void SetPrivateField<T>(MainViewModel viewModel, string fieldName, T value)
        {
            var field = typeof(MainViewModel).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(viewModel, value);
        }

        private static T GetPrivateField<T>(MainViewModel viewModel, string fieldName)
        {
            var field = typeof(MainViewModel).GetField(fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<T>(field!.GetValue(viewModel));
        }

        private static void SeedLibrary(MainViewModel viewModel, VideoEntry entry, string thumbnailUri)
        {
            SetPrivateField(viewModel, "_library", new LibraryData { Videos = { entry } });
            var item = new VideoItem
            {
                LibraryPath = LibraryPathHelper.NormalizeLibraryPath(entry.Path),
                Title = entry.Meta.Title,
                ReleaseDate = entry.Meta.Date,
                Actors = entry.Meta.Actors,
                Tags = entry.Meta.Tags,
                Description = entry.Meta.Description,
                ThumbnailPath = entry.Meta.Thumbnail,
                ThumbnailUri = thumbnailUri
            };

            viewModel.Videos.Add(item);
            InvokePrivate(viewModel, "RegisterVideo", item);
        }

        private static WebVideoMetaResult CreateMetaResult(string title)
        {
            return new WebVideoMetaResult(
                new VideoMeta(
                    title,
                    new DateOnly(2024, 1, 2),
                    new[] { "Actor One" },
                    string.Empty,
                    new[] { "Tag One" },
                    "Description"),
                null,
                null);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.True(predicate(), "Condition was not satisfied before timeout.");
        }

        private static void RunInStaAsync(Func<Task> action)
        {
            Exception? captured = null;
            using var completed = new ManualResetEventSlim(false);

            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));

                try
                {
                    var task = action();
                    task.ContinueWith(t =>
                    {
                        if (t.Exception is not null)
                        {
                            captured = t.Exception.InnerException ?? t.Exception;
                        }
                        else if (t.IsCanceled)
                        {
                            captured = new TaskCanceledException(t);
                        }

                        dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    }, TaskScheduler.Default);
                }
                catch (Exception ex)
                {
                    captured = ex;
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }

                Dispatcher.Run();
                completed.Set();
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            completed.Wait();
            thread.Join();

            if (captured is not null)
            {
                throw new Xunit.Sdk.XunitException(captured.ToString());
            }
        }

        private sealed class ViewModelFixture : IDisposable
        {
            private readonly string _root;

            public ViewModelFixture()
            {
                _root = Path.Combine(Path.GetTempPath(), "AiriMainViewModelCrawlerTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
                Provider = new CrawlerSessionProvider();
                Session = new FakeSession();
                Handle = new FakeHandle(Session);
                Factory = new FakeCrawlerSessionFactory(Handle, Session);
            }

            public CrawlerSessionProvider Provider { get; }
            public FakeSession Session { get; }
            public FakeHandle Handle { get; }
            public FakeCrawlerSessionFactory Factory { get; }
            public string LibraryPath => Path.Combine(_root, "videos.json");

            public MainViewModel CreateViewModel(IWebVideoMetaSource? metadataSource = null, IEnumerable<IWebVideoMetaSource>? metadataSources = null)
            {
                var libraryStore = new LibraryStore(LibraryPath);
                var libraryScanner = new LibraryScanner(new FileSystemScanner());
                var thumbnailCache = new ThumbnailCache(_root);
                var oneFourOneJavSource = new OneFourOneJavMetaSource(Provider);
                var sources = metadataSources ?? new[] { metadataSource ?? new CountingMetaSource(null) };
                var metadataService = new WebMetadataService(
                    sources,
                    thumbnailCache,
                    NullTranslationService.Instance,
                    "KO");

                return new MainViewModel(
                    libraryStore,
                    libraryScanner,
                    metadataService,
                    Provider,
                    oneFourOneJavSource,
                    Factory);
            }

            public void Dispose()
            {
                if (Directory.Exists(_root))
                {
                    Directory.Delete(_root, recursive: true);
                }
            }
        }

        private sealed class FakeCrawlerSessionFactory : IOneFourOneJavCrawlerSessionFactory
        {
            private readonly FakeHandle _handle;
            private readonly FakeSession _session;

            public FakeCrawlerSessionFactory(FakeHandle handle, FakeSession session)
            {
                _handle = handle;
                _session = session;
            }

            public int StartCount { get; private set; }
            public Func<CancellationToken, Task<OneFourOneJavCrawlerStartResult>>? StartOverride { get; set; }

            public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default)
            {
                StartCount++;
                return StartOverride is null
                    ? Task.FromResult(CreateResult())
                    : StartOverride(cancellationToken);
            }

            public OneFourOneJavCrawlerStartResult CreateResult()
            {
                return new OneFourOneJavCrawlerStartResult(_handle, _session, "Crawler started.");
            }
        }

        private sealed class FakeHandle : IOneFourOneJavCrawlerSessionHandle
        {
            public FakeHandle(IOneFourOneJavCrawlerSession session)
            {
                Session = session;
            }

            public bool IsOpen { get; set; } = true;
            public bool Disposed { get; private set; }
            public IOneFourOneJavCrawlerSession Session { get; }

            public bool IsBrowserOpen() => IsOpen;

            public void Dispose()
            {
                Disposed = true;
            }
        }

        private sealed class FakeSession : IOneFourOneJavCrawlerSession
        {
            public Task<bool> NavigateToAsync(string url, CancellationToken cancellationToken = default)
            {
                return Task.FromResult(true);
            }

            public Task<OneFourOneJavCrawler.CrawlerMetadata?> TryGetMetadataAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<OneFourOneJavCrawler.CrawlerMetadata?>(new OneFourOneJavCrawler.CrawlerMetadata(
                    new DateTime(2024, 1, 2),
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    string.Empty));
            }

            public Task<string?> TryGetThumbnailUrlAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult<string?>(null);
            }
        }

        private sealed class CountingMetaSource : IWebVideoMetaSource
        {
            private readonly WebVideoMetaResult? _result;

            public CountingMetaSource(WebVideoMetaResult? result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public string Name => "Counting";

            public bool CanHandle(string query) => true;

            public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }
    }
}
