using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainWindowStartupTests
{
    [Fact]
    public Task LoadedTwice_StartsOneInitializationTask() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var load = new TaskCompletionSource<LibraryData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = _ =>
        {
            loadStarted.TrySetResult();
            return load.Task;
        };
        var window = new MainWindow(viewModel);

        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        var firstTask = window.InitializationTask;
        window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        await loadStarted.Task;

        Assert.True(window.InitializationStarted);
        Assert.Same(firstTask, window.InitializationTask);
        Assert.Equal(1, fixture.Store.LoadCount);

        load.TrySetResult(new LibraryData());
        await window.InitializationTask;
        window.Close();
    });

    [Fact]
    public Task CloseDuringLoad_CancellationDoesNotEscapeAsyncVoidHandler() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = async token =>
        {
            loadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new LibraryData();
        };
        var unhandled = 0;
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            unhandled++;
            args.Handled = true;
        };
        Dispatcher.CurrentDispatcher.UnhandledException += handler;
        var window = new MainWindow(viewModel);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await loadStarted.Task;
            window.Close();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => window.InitializationTask);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(0, unhandled);
            Assert.True(viewModel.LifetimeToken.IsCancellationRequested);
        }
        finally
        {
            Dispatcher.CurrentDispatcher.UnhandledException -= handler;
        }
    });

    [Fact]
    public Task InitializationFault_IsObservedAndLoggedOnce() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        fixture.Store.LoadOverride = _ => Task.FromException<LibraryData>(new IOException("load failed"));
        var initializationErrors = 0;
        AppLogger.TestObserver = (level, message, _) =>
        {
            if (level == "ERROR" && message == "Library initialization failed.")
            {
                initializationErrors++;
            }
        };
        var unhandled = 0;
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            unhandled++;
            args.Handled = true;
        };
        Dispatcher.CurrentDispatcher.UnhandledException += handler;
        var window = new MainWindow(viewModel);
        try
        {
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            await Assert.ThrowsAsync<IOException>(() => window.InitializationTask);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
            Assert.Equal(1, initializationErrors);
            Assert.Equal(0, unhandled);
            window.Close();
        }
        finally
        {
            AppLogger.TestObserver = null;
            Dispatcher.CurrentDispatcher.UnhandledException -= handler;
        }
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AiriWindowStartupTests", Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Store = new FakeStore(Path.Combine(_root, "videos.json"));
        }

        public FakeStore Store { get; }

        public MainViewModel CreateViewModel()
        {
            var provider = new CrawlerSessionProvider();
            return new MainViewModel(
                Store,
                new EmptyScanner(),
                new WebMetadataService(
                    Array.Empty<IWebVideoMetaSource>(),
                    new ThumbnailCache(_root),
                    NullTranslationService.Instance,
                    "KO"),
                provider,
                new OneFourOneJavMetaSource(provider),
                new NeverCrawlerSessionFactory(),
                new TestThumbnailImageLoader());
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeStore(string filePath) : ILibraryStore
    {
        public string FilePath { get; } = filePath;
        public int LoadCount { get; private set; }
        public Func<CancellationToken, Task<LibraryData>>? LoadOverride { get; set; }

        public Task<LibraryData> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return LoadOverride?.Invoke(cancellationToken) ?? Task.FromResult(new LibraryData());
        }

        public Task SaveAsync(LibraryData library, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class EmptyScanner : ILibraryScanner
    {
        public Task<LibraryScanResult> ScanAsync(LibraryData library, CancellationToken cancellationToken) =>
            Task.FromResult(new LibraryScanResult(
                Array.Empty<FileSnapshot>(),
                Array.Empty<FileSnapshot>(),
                Array.Empty<VideoEntry>(),
                Array.Empty<UpdatedFile>()));
    }

    private sealed class NeverCrawlerSessionFactory : IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Crawler is not used by startup window tests.");
    }
}
