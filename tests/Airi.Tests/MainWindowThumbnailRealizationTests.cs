using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Services.VideoPreview;
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
    public void CalculateTooltipDecodeWidth_Uses480DipAndDpiThenClampsTo960()
    {
        WpfTestHost.Run(() =>
        {
            Assert.Equal(480, MainWindow.CalculateTooltipDecodeWidth(1d));
            Assert.Equal(720, MainWindow.CalculateTooltipDecodeWidth(1.5d));
            Assert.Equal(960, MainWindow.CalculateTooltipDecodeWidth(3d));
        });
    }

    [Fact]
    public Task TooltipOpen_LoadsDpiSizedSourceWithoutReplacingCardSource() => Run(async fixture =>
    {
        await fixture.Window.RegisterThumbnailImageForTestsAsync(fixture.CreateImage(fixture.A));
        var cardSource = fixture.A.ThumbnailSource;
        var container = new ListBoxItem { DataContext = fixture.A };
        var tooltip = new ToolTip { Content = fixture.A };

        await fixture.Window.LoadTooltipCoverAsync(container, tooltip, fixture.A, 1.5d);

        Assert.Equal(new[] { 260, 720 }, fixture.Loader.Widths);
        Assert.Same(cardSource, fixture.A.ThumbnailSource);
        var state = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
        Assert.Same(fixture.Loader.Sources[^1], state.CoverSource);
        Assert.Same(fixture.A, tooltip.Content);
        Assert.Same(container, tooltip.PlacementTarget);
    });

    [Fact]
    public Task TooltipOpen_ReusedTooltipReplacesPreviousVideoAndSource() => Run(async fixture =>
    {
        var containerA = new ListBoxItem { DataContext = fixture.A };
        var containerB = new ListBoxItem { DataContext = fixture.B };
        var tooltip = new ToolTip();

        await fixture.Window.LoadTooltipCoverAsync(containerA, tooltip, fixture.A, 1d);
        var previousState = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
        previousState.ShowFrame(8, 8, new VideoPreviewFrame(new byte[8 * 8 * 4], TimeSpan.Zero));
        await fixture.Window.LoadTooltipCoverAsync(containerB, tooltip, fixture.B, 1d);

        Assert.Equal(new[] { fixture.A.ThumbnailPath, fixture.B.ThumbnailPath }, fixture.Loader.Paths);
        Assert.Same(fixture.B, tooltip.Content);
        Assert.Same(containerB, tooltip.PlacementTarget);
        var currentState = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
        Assert.NotSame(previousState, currentState);
        Assert.Null(currentState.PreviewSource);
        Assert.Same(fixture.Loader.Sources[^1], currentState.CoverSource);
    });

    [Fact]
    public Task TooltipOpen_PreviousDelayedRequestCannotOverwriteCurrentVideo() => Run(async fixture =>
    {
        fixture.Loader.DelayNextRequest();
        var containerA = new ListBoxItem { DataContext = fixture.A };
        var containerB = new ListBoxItem { DataContext = fixture.B };
        var tooltip = new ToolTip();

        var loadA = fixture.Window.LoadTooltipCoverAsync(containerA, tooltip, fixture.A, 1d);
        var loadB = fixture.Window.LoadTooltipCoverAsync(containerB, tooltip, fixture.B, 1d);
        await Task.WhenAll(loadA, loadB);

        Assert.True(fixture.Loader.CancellationTokens[0].IsCancellationRequested);
        Assert.Same(fixture.B, tooltip.Content);
        Assert.Same(containerB, tooltip.PlacementTarget);
        var state = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
        Assert.Same(fixture.Loader.Sources[^1], state.CoverSource);
    });

    [Fact]
    public Task TooltipClose_CancelsInFlightLoadAndClearsState() => Run(async fixture =>
    {
        fixture.Loader.DelayNextRequest();
        var container = new ListBoxItem { DataContext = fixture.A };
        var tooltip = new ToolTip { Content = fixture.A };

        var load = fixture.Window.LoadTooltipCoverAsync(container, tooltip, fixture.A, 1d);
        var cancellationToken = Assert.Single(fixture.Loader.CancellationTokens);
        var state = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
        Assert.Same(fixture.A.ThumbnailSource, state.CoverSource);

        await fixture.Window.CloseVideoTooltipAsync(container, tooltip);

        Assert.True(cancellationToken.IsCancellationRequested);
        Assert.Null(tooltip.Tag);
        await load;
    });

    [Fact]
    public Task TooltipClose_FromPreviousContainerDoesNotCloseCurrentVideo() => Run(async fixture =>
    {
        var containerA = new ListBoxItem { DataContext = fixture.A };
        var containerB = new ListBoxItem { DataContext = fixture.B };
        var tooltip = new ToolTip();

        await fixture.Window.LoadTooltipCoverAsync(containerA, tooltip, fixture.A, 1d);
        await fixture.Window.LoadTooltipCoverAsync(containerB, tooltip, fixture.B, 1d);
        tooltip.IsOpen = true;
        var currentSource = tooltip.Tag;

        await fixture.Window.CloseVideoTooltipAsync(containerA, tooltip);

        Assert.True(tooltip.IsOpen);
        Assert.Same(fixture.B, tooltip.Content);
        Assert.Same(currentSource, tooltip.Tag);
    });

    [Fact]
    public Task TooltipClose_CancelsCoverAndPreviewBeforeCompleting() => RunWithPreview(async fixture =>
    {
        fixture.Loader.DelayNextRequest();
        var container = new ListBoxItem { DataContext = fixture.A };
        var tooltip = new ToolTip { Content = fixture.A };
        var cover = fixture.Window.LoadTooltipCoverAsync(container, tooltip, fixture.A, 1d);
        var state = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
        var preview = fixture.PreviewController!.RunAsync(
            new TooltipPreviewSession(
                state.SessionId,
                new VideoPreviewRequest(fixture.A.SourcePath, 480, 350, 15, TimeSpan.FromSeconds(10)),
                true,
                true,
                new AlwaysCurrentPreviewSink()),
            CancellationToken.None);
        await fixture.PreviewService!.WaitUntilCalledAsync();

        var close = fixture.Window.CloseVideoTooltipAsync(container, tooltip);

        Assert.False(tooltip.IsOpen);
        Assert.Null(tooltip.Tag);
        Assert.Null(tooltip.Content);
        Assert.Null(tooltip.PlacementTarget);
        await close;
        await cover;
        await preview;
        Assert.True(Assert.Single(fixture.Loader.CancellationTokens).IsCancellationRequested);
        Assert.True(fixture.PreviewService.PrepareToken.IsCancellationRequested);
    });

    [Fact]
    public Task TooltipPreview_AnimationsDisabledDoesNotCallInjectedService() => RunWithPreview(async fixture =>
    {
        await fixture.PreviewController!.RunAsync(
            new TooltipPreviewSession(
                Guid.NewGuid(),
                new VideoPreviewRequest(fixture.A.SourcePath, 480, 350, 15, TimeSpan.FromSeconds(10)),
                false,
                true,
                new AlwaysCurrentPreviewSink()),
            CancellationToken.None);

        Assert.Equal(0, fixture.PreviewService!.PrepareCallCount);
    });

    [Fact]
    public Task VideoItemMouseEnter_UsesActualItemSourcePath() =>
        RunWithPreview(async fixture =>
        {
            fixture.MakeAvailable(fixture.A);
            var container = await fixture.RealizeAsync(fixture.A);

            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });

            await fixture.PreviewService!.WaitUntilCalledAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(fixture.A.SourcePath, fixture.PreviewService.Request!.SourcePath);
            var tooltip = Assert.IsType<ToolTip>(ToolTipService.GetToolTip(container));
            await fixture.Window.CloseVideoTooltipAsync(container, tooltip);
        }, clientAreaAnimationsEnabledOverride: true);

    [Fact]
    public Task VideoListDoubleClick_OnlyPlaysClickedVideoItem() => Run(async fixture =>
    {
        fixture.A.Title = "selected";
        fixture.B.Title = "clicked";
        await fixture.RealizeAsync(fixture.A);
        fixture.ViewModel.Videos.Add(fixture.B);
        fixture.Window.UpdateLayout();
        WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        var list = fixture.Window.GetVideoListForTests();
        await WaitUntilAsync(
            () => list.ItemContainerGenerator.ContainerFromItem(fixture.B) is ListBoxItem,
            TimeSpan.FromSeconds(5));
        var clickedContainer = Assert.IsType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromItem(fixture.B));
        list.SelectedItem = fixture.A;
        var messages = new List<string>();
        AppLogger.TestObserver = (_, message, _) => messages.Add(message);

        try
        {
            var itemArgs = RaiseDoubleClick(list, clickedContainer);

            Assert.True(itemArgs.Handled);
            Assert.Contains($"Skipping playback for {fixture.B.Title}; no source path set.", messages);
            Assert.DoesNotContain($"Skipping playback for {fixture.A.Title}; no source path set.", messages);

            messages.Clear();
            var scrollBar = Assert.Single(
                EnumerateVisualDescendants<ScrollBar>(list),
                value => value.Orientation == Orientation.Vertical);
            var scrollBarArgs = RaiseDoubleClick(list, scrollBar);

            Assert.False(scrollBarArgs.Handled);
            Assert.Empty(messages);
        }
        finally
        {
            AppLogger.TestObserver = null;
        }
    });

    [Fact]
    public Task WindowClose_AwaitsPreviewShutdownThenCloses() => RunWithPreview(async fixture =>
    {
        var preview = fixture.PreviewController!.RunAsync(
            new TooltipPreviewSession(
                Guid.NewGuid(),
                new VideoPreviewRequest(fixture.A.SourcePath, 480, 350, 15, TimeSpan.FromSeconds(10)),
                true,
                true,
                new AlwaysCurrentPreviewSink()),
            CancellationToken.None);
        await fixture.PreviewService!.WaitUntilCalledAsync();

        fixture.Window.Close();
        WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        await preview;
        WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);

        Assert.True(fixture.PreviewService.PrepareToken.IsCancellationRequested);
        Assert.DoesNotContain(fixture.Window, Application.Current.Windows.Cast<Window>());
    });

    [Fact]
    public Task TooltipTemplate_SeparatesCoverAndPreviewSourcesWhileOpen() => Run(fixture =>
    {
        var initialSource = fixture.A.ThumbnailSource!;
        var upgradedSource = CreateBitmap(29);
        var container = new ListBoxItem { Width = 100, Height = 100 };
        var host = new Window
        {
            Width = 120,
            Height = 120,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow,
            Content = container
        };
        var state = new TooltipPreviewState(Guid.NewGuid(), initialSource);
        var tooltip = new ToolTip
        {
            Content = fixture.A,
            ContentTemplate = Assert.IsType<DataTemplate>(fixture.Window.FindResource("VideoTooltipTemplate")),
            PlacementTarget = container,
            Style = Assert.IsType<Style>(fixture.Window.FindResource(typeof(ToolTip))),
            Tag = state
        };

        try
        {
            host.Show();
            tooltip.IsOpen = true;
            WpfTestHost.DrainDispatcher(host.Dispatcher);
            var images = EnumerateVisualDescendants<Image>(tooltip).ToArray();
            Assert.Equal(2, images.Length);
            Assert.Contains(images, image =>
                ReferenceEquals(image.Source, initialSource) && image.Visibility == Visibility.Visible);

            state.UpdateCover(upgradedSource);
            state.ShowFrame(8, 8, new VideoPreviewFrame(new byte[8 * 8 * 4], TimeSpan.Zero));
            WpfTestHost.DrainDispatcher(host.Dispatcher);

            Assert.Contains(images, image =>
                ReferenceEquals(image.Source, state.PreviewSource) && image.Visibility == Visibility.Visible);
            Assert.Contains(images, image =>
                ReferenceEquals(image.Source, upgradedSource) && image.Visibility == Visibility.Collapsed);
            Assert.Same(initialSource, fixture.A.ThumbnailSource);

            state.SetPhase(TooltipPreviewPhase.Cover);
            WpfTestHost.DrainDispatcher(host.Dispatcher);

            Assert.Contains(images, image =>
                ReferenceEquals(image.Source, upgradedSource) && image.Visibility == Visibility.Visible);
            Assert.Contains(images, image => image.Source is null && image.Visibility == Visibility.Collapsed);
        }
        finally
        {
            tooltip.IsOpen = false;
            host.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task TooltipSink_BackgroundCallbackMarshalsStaleCheckToDispatcher() => Run(async fixture =>
    {
        var container = new ListBoxItem { DataContext = fixture.A };
        var tooltip = new ToolTip { Content = fixture.A, PlacementTarget = container };
        var state = new TooltipPreviewState(Guid.NewGuid(), fixture.A.ThumbnailSource);
        tooltip.Tag = state;
        var sink = new MainWindow.MainWindowTooltipPreviewSink(
            fixture.Window,
            container,
            tooltip,
            fixture.A,
            state,
            TimeProvider.System);

        var callback = await Task.Run(async () =>
            await sink.SetPhaseAsync(
                state.SessionId,
                TooltipPreviewPhase.Preparing,
                CancellationToken.None));

        Assert.True(callback.Applied);
        Assert.Equal(TooltipPreviewPhase.Preparing, state.Phase);
    });

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

    private static MouseButtonEventArgs RaiseDoubleClick(ListBox list, DependencyObject source)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = source
        };
        list.RaiseEvent(args);
        return args;
    }

    private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static ImageSource CreateBitmap(byte value)
    {
        var pixels = Enumerable.Repeat(value, 8 * 8 * 4).ToArray();
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Task Run(Func<Fixture, Task> test) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await test(fixture);
    });

    private static Task RunWithPreview(
        Func<Fixture, Task> test,
        bool? clientAreaAnimationsEnabledOverride = null) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(
            enablePreview: true,
            clientAreaAnimationsEnabledOverride: clientAreaAnimationsEnabledOverride);
        await test(fixture);
    });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
            await Task.Delay(20);
        }
        Assert.True(condition(), "WPF item was not realized before timeout.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(
            bool enablePreview = false,
            bool? clientAreaAnimationsEnabledOverride = null)
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
            if (enablePreview)
            {
                PreviewService = new BlockingPreviewService();
                PreviewController = new TooltipPreviewController(PreviewService, TimeProvider.System);
            }
            Window = new MainWindow(
                ViewModel,
                Probe,
                Loader,
                PreviewController,
                clientAreaAnimationsEnabledOverride);
            A = CreateItem("a.jpg");
            B = CreateItem("b.jpg");
        }

        public RecordingLoader Loader { get; }
        public ThumbnailPerformanceProbe Probe { get; }
        public MainViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public BlockingPreviewService? PreviewService { get; }
        public TooltipPreviewController? PreviewController { get; }
        public VideoItem A { get; }
        public VideoItem B { get; }

        public Image CreateImage(VideoItem item)
        {
            var image = new Image { DataContext = item, Width = 234, Height = 160 };
            image.Measure(new Size(234, 160));
            image.Arrange(new Rect(0, 0, 234, 160));
            return image;
        }

        public void MakeAvailable(VideoItem item)
        {
            var sourcePath = Path.Combine(_root, "actual-item.mp4");
            File.WriteAllBytes(sourcePath, new byte[] { 0 });
            item.UpdateFileState(
                sourcePath,
                1,
                File.GetLastWriteTimeUtc(sourcePath),
                VideoPresenceState.Available,
                File.GetCreationTimeUtc(sourcePath));
        }

        public async Task<ListBoxItem> RealizeAsync(VideoItem item)
        {
            Window.Show();
            await WaitUntilAsync(() => Window.InitializationStarted, TimeSpan.FromSeconds(5));
            await Window.InitializationTask.WaitAsync(TimeSpan.FromSeconds(10));
            ViewModel.Videos.Clear();
            ViewModel.Videos.Add(item);
            Window.UpdateLayout();
            WpfTestHost.DrainDispatcher(Window.Dispatcher);
            var list = Window.GetVideoListForTests();
            await WaitUntilAsync(
                () => list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem,
                TimeSpan.FromSeconds(5));
            return Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(item));
        }

        public void Dispose()
        {
            if (Application.Current.Windows.Cast<Window>().Contains(Window))
            {
                Window.Close();
                WpfTestHost.DrainDispatcher(Window.Dispatcher);
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

    private sealed class BlockingPreviewService : IVideoPreviewService
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IPreparedVideoPreview> _prepare =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PrepareCallCount { get; private set; }
        public CancellationToken PrepareToken { get; private set; }
        public VideoPreviewRequest? Request { get; private set; }

        public Task<IPreparedVideoPreview> PrepareAsync(
            VideoPreviewRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            PrepareToken = cancellationToken;
            Request = request;
            cancellationToken.Register(() => _prepare.TrySetCanceled(cancellationToken));
            _called.TrySetResult();
            return _prepare.Task;
        }

        public Task WaitUntilCalledAsync() => _called.Task;
    }

    private sealed class AlwaysCurrentPreviewSink : ITooltipPreviewSink
    {
        public bool IsCurrent(Guid sessionId) => true;

        public ValueTask<DispatcherCallbackResult> SetPhaseAsync(
            Guid sessionId,
            TooltipPreviewPhase phase,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DispatcherCallbackResult(true, TimeProvider.System.GetTimestamp(), TimeSpan.Zero));

        public ValueTask<FramePresentationResult> ShowFrameAsync(
            Guid sessionId,
            int pixelWidth,
            int pixelHeight,
            VideoPreviewFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FramePresentationResult(true, TimeProvider.System.GetTimestamp(), TimeSpan.Zero));
    }

    private sealed class RecordingLoader : IThumbnailImageLoader
    {
        private bool _delayNextRequest;

        public RecordingLoader()
        {
            FallbackSource = CreateBitmap(0);
        }

        public ImageSource FallbackSource { get; }
        public List<string> Paths { get; } = new();
        public List<int> Widths { get; } = new();
        public List<CancellationToken> CancellationTokens { get; } = new();
        public List<ImageSource> Sources { get; } = new();

        public void DelayNextRequest() => _delayNextRequest = true;

        public Task<ThumbnailImageResult> LoadAsync(string thumbnailPath, int decodePixelWidth, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(thumbnailPath);
            Widths.Add(decodePixelWidth);
            CancellationTokens.Add(cancellationToken);
            var source = CreateBitmap((byte)Math.Clamp(decodePixelWidth % 255, 1, 254));
            Sources.Add(source);
            if (!_delayNextRequest)
            {
                return Task.FromResult(new ThumbnailImageResult(source, false));
            }

            _delayNextRequest = false;
            var completion = new TaskCompletionSource<ThumbnailImageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
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
