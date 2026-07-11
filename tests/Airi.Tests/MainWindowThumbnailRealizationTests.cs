using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainWindowThumbnailRealizationTests
{
    [Fact]
    public void CalculateThumbnailDecodeWidth_UsesActualWidthAndDpiThenClamps64To520()
    {
        WpfTestHost.Run(() =>
        {
            Assert.Equal(64, MeasureWidth(1));
            Assert.Equal(234, MeasureWidth(234));
            Assert.Equal(520, MeasureWidth(600));
        });
    }

    [Fact]
    public Task Loaded_WhenOldRegistrationDiffers_ReleasesOldBeforeRequestingCurrent() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        image.DataContext = fixture.B;
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        Assert.Equal(new[] { fixture.A.ThumbnailPath, fixture.B.ThumbnailPath }, fixture.Loader.Paths);
        Assert.Null(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Outcome);
    });

    [Fact]
    public Task DataContextChanged_WhenLoaded_ReleasesRegistrationThenRequestsNewValue() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(image, fixture.B, isLoaded: true);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).HasInFlight);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Outcome);
    });

    [Fact]
    public Task Unloaded_UsesRegistrationInsteadOfCurrentDataContext() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        image.DataContext = fixture.B;
        fixture.Window.UnregisterThumbnailImageForTests(image);
        Assert.Null(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Exists);
    });

    [Fact]
    public Task DataContextChangedThenUnloaded_DoesNotReleaseNewItemTwice() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(image, fixture.B, isLoaded: true);
        fixture.Window.UnregisterThumbnailImageForTests(image);
        var generation = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Generation;
        fixture.Window.UnregisterThumbnailImageForTests(image);
        Assert.Equal(generation, fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Generation);
    });

    [Fact]
    public Task UnloadedThenDataContextChanged_DoesNotReleaseNewItem() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        fixture.Window.UnregisterThumbnailImageForTests(image);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(image, fixture.B, isLoaded: false);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Exists);
        Assert.Single(fixture.Loader.Paths);
    });

    [Fact]
    public Task Close_ReleasesEveryRegistrationAndLeavesDictionaryEmpty() => Run(async fixture =>
    {
        var first = fixture.CreateImage(fixture.A);
        var second = fixture.CreateImage(fixture.B);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(first);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(second);
        fixture.Window.Close();
        Assert.Equal(0, fixture.Window.GetThumbnailRegistrationCountForTests());
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Exists);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Exists);
    });

    [Fact]
    public Task RequestWithoutImageRegistration_RecordsOutsideRealizationWindow() => Run(async fixture =>
    {
        await fixture.ViewModel.RequestThumbnailAsync(fixture.A, 234);
        Assert.False(fixture.Probe.GetRequestRecords().Single().InRealizationWindow);
    });

    [Fact]
    public Task LoadedRegistrationBeforeRequest_RecordsInsideRealizationWindow() => Run(async fixture =>
    {
        await fixture.Window.RegisterThumbnailImageForTestsAsync(fixture.CreateImage(fixture.A));
        Assert.True(fixture.Probe.GetRequestRecords().Single().InRealizationWindow);
    });

    [Fact]
    public Task RecyclingEventOrders_LeaveOldRegistrationAndKeepOnlyNewItemActive() => Run(async fixture =>
    {
        var firstOrder = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(firstOrder);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(firstOrder, fixture.B, isLoaded: true);
        fixture.Window.UnregisterThumbnailImageForTests(firstOrder);

        var secondOrder = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(secondOrder);
        fixture.Window.UnregisterThumbnailImageForTests(secondOrder);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(secondOrder, fixture.B, isLoaded: false);

        Assert.Equal(0, fixture.Window.GetThumbnailRegistrationCountForTests());
        Assert.Equal(0, fixture.Probe.GetActiveRegistrationCount());
    });

    [Fact]
    public Task SameItemTwoImages_FirstUnloadKeepsThumbnailActive() => Run(async fixture =>
    {
        var first = fixture.CreateImage(fixture.A);
        var second = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(first);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(second);
        fixture.Window.UnregisterThumbnailImageForTests(first);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.Equal(1, fixture.Window.GetThumbnailRegistrationCountForTests());
    });

    [Fact]
    public Task SameItemTwoImages_LastUnloadReleasesExactlyOnce() => Run(async fixture =>
    {
        var first = fixture.CreateImage(fixture.A);
        var second = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(first);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(second);
        fixture.Window.UnregisterThumbnailImageForTests(first);
        fixture.Window.UnregisterThumbnailImageForTests(second);
        var generation = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Generation;
        fixture.Window.UnregisterThumbnailImageForTests(second);
        Assert.Equal(generation, fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Generation);
        Assert.Null(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
    });

    [Fact]
    public Task SameItem_NewRegistrationBeforeOldUnload_DoesNotDropTracking() => Run(async fixture =>
    {
        var oldImage = fixture.CreateImage(fixture.A);
        var newImage = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(oldImage);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(newImage);
        fixture.Window.UnregisterThumbnailImageForTests(oldImage);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.Single(fixture.Loader.Paths);
    });

    private static int MeasureWidth(double width)
    {
        return MainWindow.CalculateThumbnailDecodeWidth(width, 1d);
    }

    private static Task Run(Func<Fixture, Task> test) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await test(fixture);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "AiriMainWindowThumbnailTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Loader = new RecordingLoader();
            Probe = ThumbnailPerformanceProbe.CreateEnabled();
            Probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
            var provider = new CrawlerSessionProvider();
            var source = new OneFourOneJavMetaSource(provider);
            var metadata = new WebMetadataService(
                new IWebVideoMetaSource[] { new NoopMetaSource() },
                new ThumbnailCache(_root),
                NullTranslationService.Instance,
                "KO");
            ViewModel = new MainViewModel(
                new LibraryStore(Path.Combine(_root, "videos.json")),
                new LibraryScanner(new FileSystemScanner()),
                metadata,
                provider,
                source,
                new NoopCrawlerFactory(),
                Probe,
                Loader);
            Window = new MainWindow(ViewModel, Probe);
            A = CreateItem("a.jpg");
            B = CreateItem("b.jpg");
        }

        public RecordingLoader Loader { get; }
        public ThumbnailPerformanceProbe Probe { get; }
        public MainViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public VideoItem A { get; }
        public VideoItem B { get; }

        public Image CreateImage(VideoItem item)
        {
            var image = new Image { DataContext = item, Width = 234, Height = 160 };
            image.Measure(new Size(234, 160));
            image.Arrange(new Rect(0, 0, 234, 160));
            return image;
        }

        public void Dispose()
        {
            if (Application.Current.Windows.Cast<Window>().Contains(Window))
            {
                Window.Close();
            }
            else
            {
                ViewModel.Dispose();
            }

            if (Probe.IsActive)
            {
                Probe.EndMeasurementPhase();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private VideoItem CreateItem(string path)
        {
            var item = new VideoItem
            {
                LibraryPath = $"./Videos/{path}.mp4",
                ThumbnailPath = path,
                ThumbnailUri = string.Empty
            };
            item.ReleaseThumbnail(Loader.FallbackSource);
            return item;
        }
    }

    private sealed class RecordingLoader : IThumbnailImageLoader
    {
        private readonly ImageSource _decoded;

        public RecordingLoader()
        {
            FallbackSource = CreateBitmap(0);
            _decoded = CreateBitmap(0x66);
        }

        public ImageSource FallbackSource { get; }
        public List<string> Paths { get; } = new();

        public Task<ThumbnailImageResult> LoadAsync(string thumbnailPath, int decodePixelWidth, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(thumbnailPath);
            return Task.FromResult(new ThumbnailImageResult(_decoded, false));
        }

        private static ImageSource CreateBitmap(byte value)
        {
            var pixels = Enumerable.Repeat(value, 8 * 8 * 4).ToArray();
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private sealed class NoopMetaSource : IWebVideoMetaSource
    {
        public string Name => "Noop";
        public bool CanHandle(string query) => false;
        public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<WebVideoMetaResult?>(null);
    }

    private sealed class NoopCrawlerFactory : IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<OneFourOneJavCrawlerStartResult>(new InvalidOperationException("Not used."));
    }
}
