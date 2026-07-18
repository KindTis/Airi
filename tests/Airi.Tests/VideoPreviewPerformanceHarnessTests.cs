using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Services.VideoPreview;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VideoPreviewPerformanceHarnessTests
{
    [Fact]
    public void Evaluate_FailsAnyDispatcherOver100MsOrFrameGapOver250Ms()
    {
        var probe = VideoPreviewPerformanceProbe.CreateEnabled();
        probe.RecordDispatcherCallback(TimeSpan.FromMilliseconds(101));
        probe.RecordDisplayedFrame(TimeSpan.Zero);
        probe.RecordDisplayedFrame(TimeSpan.FromMilliseconds(251));
        var result = probe.Complete(TimeSpan.FromSeconds(1));

        Assert.False(result.Passed);
        Assert.Equal(TimeSpan.FromMilliseconds(101), result.MaximumDispatcherDuration);
        Assert.Equal(TimeSpan.FromMilliseconds(251), result.MaximumFrameGap);
    }

    [Fact]
    public void Evaluate_PassesAtLeast12FpsWithInclusiveLimits()
    {
        var probe = VideoPreviewPerformanceProbe.CreateEnabled();
        for (var index = 0; index < 24; index++)
        {
            probe.RecordDispatcherCallback(TimeSpan.FromMilliseconds(4));
            probe.RecordDisplayedFrame(TimeSpan.FromSeconds(index / 12d));
        }
        var result = probe.Complete(TimeSpan.FromSeconds(2));

        Assert.True(result.Passed);
        Assert.True(result.AverageFramesPerSecond >= 12d);
        Assert.True(result.MaximumFrameGap <= TimeSpan.FromMilliseconds(250));
        Assert.True(result.MaximumDispatcherDuration <= TimeSpan.FromMilliseconds(100));
    }

    [Fact]
    public void RecorderAndObserver_UseOneClockAndAcceptInclusivePlaybackLimit()
    {
        var time = new ManualTimeProvider();
        var probe = VideoPreviewPerformanceProbe.CreateEnabled(time);
        var sessionId = Guid.NewGuid();
        probe.RecordSessionOpened(sessionId, 0);
        probe.RecordPrepared(sessionId, TimeSpan.FromSeconds(1).Ticks);
        probe.RecordPlaybackStarted(
            sessionId,
            TimeSpan.FromSeconds(2).Ticks,
            TimeSpan.FromSeconds(10));
        for (var index = 0; index < 120; index++)
        {
            probe.RecordPresentation(
                sessionId,
                new FramePresentationResult(
                    true,
                    TimeSpan.FromSeconds(2 + (index / 12d)).Ticks,
                    TimeSpan.FromMilliseconds(100)));
        }
        probe.RecordPhaseChanged(
            sessionId,
            TooltipPreviewPhase.Cover,
            new DispatcherCallbackResult(
                true,
                TimeSpan.FromSeconds(12).Ticks,
                TimeSpan.FromMilliseconds(100)));
        var identity = new MediaProcessIdentity("ffmpeg.exe", 42, DateTime.UnixEpoch);
        probe.Started(identity);
        probe.Exited(identity, 0);

        var result = probe.Complete(TimeSpan.FromSeconds(10));

        Assert.True(result.Passed, string.Join(", ", result.FailureReasons));
        Assert.Equal(TimeSpan.FromSeconds(1), result.PrepareDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), result.PlaybackDuration);
        Assert.Equal(1, result.MaximumLiveProcessCount);
        Assert.Equal(0, result.RemainingLiveProcessCount);
    }

    [Fact]
    public void Evaluate_FailsIncompleteSessionAndLiveProcess()
    {
        var time = new ManualTimeProvider();
        var probe = VideoPreviewPerformanceProbe.CreateEnabled(time);
        var sessionId = Guid.NewGuid();
        probe.RecordSessionOpened(sessionId, 0);
        probe.RecordDisplayedFrame(TimeSpan.Zero);
        probe.Started(new MediaProcessIdentity("ffprobe.exe", 7, DateTime.UnixEpoch));

        var result = probe.Complete(TimeSpan.FromSeconds(1));

        Assert.False(result.Passed);
        Assert.Contains(result.FailureReasons, reason => reason.Contains("incomplete", StringComparison.Ordinal));
        Assert.Contains(result.FailureReasons, reason => reason.Contains("remained", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolvePerformanceInputs_SingleVideoReturnsOnlySinglePath()
    {
        var paths = ResolvePerformanceInputs("single.mp4", "h264.mp4", "hevc.mkv");

        Assert.Equal(new[] { Path.GetFullPath("single.mp4") }, paths);
    }

    [Fact]
    public void ResolvePerformanceInputs_DualModePreservesBothCodecPaths()
    {
        var paths = ResolvePerformanceInputs(null, "h264.mp4", "hevc.mkv");

        Assert.Equal(
            new[] { Path.GetFullPath("h264.mp4"), Path.GetFullPath("hevc.mkv") },
            paths);
    }

    [Fact]
    public async Task Measure()
    {
        var outputPath = Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        var inputPaths = ResolvePerformanceInputs(
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_VIDEO"),
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_H264"),
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_HEVC"));
        Assert.All(inputPaths, path =>
            Assert.True(File.Exists(path), $"Video preview input not found: {path}"));
        var measurements = new List<MeasuredPreview>();

        await WpfTestHost.RunAsync(async () =>
        {
            foreach (var inputPath in inputPaths)
            {
                measurements.Add(await MeasurePreviewAsync(inputPath));
            }
        });

        var first = measurements[0];
        using var process = Process.GetCurrentProcess();
        var document = new VideoPreviewPerformanceDocument(
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_BUILD_CONFIGURATION") ?? BuildConfiguration,
            first.DpiX,
            first.DpiY,
            $"{SystemParameters.WorkArea.Width:0.###}x{SystemParameters.WorkArea.Height:0.###}",
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_CPU") ?? "unknown",
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_GPU") ?? "unknown",
            process.WorkingSet64,
            measurements.Select(measurement => measurement.Entry).ToArray());
        var fullOutputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutputPath)!);
        await File.WriteAllTextAsync(
            fullOutputPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            }));

        Assert.All(document.Results, result =>
            Assert.True(result.Passed, $"{result.Codec}: {string.Join(", ", result.FailureReasons)}"));
    }

    [Theory]
    [InlineData("ffprobe.exe")]
    [InlineData("ffmpeg.exe")]
    public async Task ActualVideoItem_WindowCloseStopsActiveMediaProcess(string processName)
    {
        var outputPath = Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath)) return;

        var sourcePath = ResolvePerformanceInputs(
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_VIDEO"),
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_H264"),
            Environment.GetEnvironmentVariable("AIRI_VIDEO_PREVIEW_HEVC"))[0];
        Assert.True(File.Exists(sourcePath), $"Video preview input not found: {sourcePath}");

        await WpfTestHost.RunAsync(() =>
            VerifyWindowCloseStopsActiveMediaProcessAsync(sourcePath, processName));
    }

    private static async Task<MeasuredPreview> MeasurePreviewAsync(string sourcePath)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AiriVideoPreviewPerformance",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var loader = new HarnessThumbnailLoader();
        var viewModel = CreateViewModel(root, loader);
        var probe = VideoPreviewPerformanceProbe.CreateEnabled(TimeProvider.System);
        var binaryFolder = Path.Combine(AppContext.BaseDirectory, "resources", "ffmpeg", "win-x64");
        var mediaProbe = await new FfmpegCorePreviewBackend(binaryFolder)
            .ProbeAsync(sourcePath, CancellationToken.None);
        var runner = new MediaProcessRunner(probe);
        var controller = new TooltipPreviewController(
            new FfmpegVideoPreviewService(new FfmpegCorePreviewBackend(binaryFolder, runner)),
            TimeProvider.System,
            probe);
        var window = new MainWindow(
            viewModel,
            thumbnailImageLoader: loader,
            tooltipPreviewController: controller,
            clientAreaAnimationsEnabledOverride: true)
        {
            Width = 1500,
            Height = 1000,
            SizeToContent = SizeToContent.Manual,
            WindowState = WindowState.Normal,
            ShowInTaskbar = false
        };

        try
        {
            window.Show();
            await WaitUntilAsync(() => window.InitializationStarted, TimeSpan.FromSeconds(5));
            await window.InitializationTask.WaitAsync(TimeSpan.FromSeconds(10));
            viewModel.Videos.Clear();
            for (var index = 0; index < 120; index++)
            {
                var item = new VideoItem
                {
                    LibraryPath = $"./Videos/performance-{index:D3}.mp4",
                    Title = $"Preview performance {index:D3}",
                    ThumbnailPath = string.Empty,
                    ThumbnailUri = string.Empty
                };
                item.ReleaseThumbnail(loader.FallbackSource);
                item.UpdateFileState(
                    sourcePath,
                    new FileInfo(sourcePath).Length,
                    File.GetLastWriteTimeUtc(sourcePath),
                    VideoPresenceState.Available,
                    File.GetCreationTimeUtc(sourcePath));
                viewModel.Videos.Add(item);
            }
            window.UpdateLayout();
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            window.ScrollVideoToIndex(0);
            var list = window.GetVideoListForTests();
            await WaitUntilAsync(
                () => list.ItemContainerGenerator.ContainerFromIndex(0) is ListBoxItem,
                TimeSpan.FromSeconds(5));
            var container = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromIndex(0));
            var video = Assert.IsType<VideoItem>(container.DataContext);
            var tooltip = Assert.IsType<ToolTip>(ToolTipService.GetToolTip(container));
            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });
            var state = Assert.IsType<TooltipPreviewState>(tooltip.Tag);
            await PumpUntilAsync(
                () => probe.HasStartedPlayback,
                window,
                viewModel,
                stressUi: false,
                TimeSpan.FromSeconds(8),
                () => DescribeProbe(probe) +
                      $", tooltipOpen={tooltip.IsOpen}, data={ReferenceEquals(container.DataContext, video)}, " +
                      $"placement={ReferenceEquals(tooltip.PlacementTarget, container)}, " +
                      $"content={ReferenceEquals(tooltip.Content, video)}, tag={tooltip.Tag is TooltipPreviewState}");
            await PumpUntilAsync(
                () => probe.HasCompletedSession && probe.LiveProcessCount == 0,
                window,
                viewModel,
                stressUi: true,
                TimeSpan.FromSeconds(15),
                () => DescribeProbe(probe) +
                      $", data={ReferenceEquals(container.DataContext, video)}, " +
                      $"placement={ReferenceEquals(tooltip.PlacementTarget, container)}, " +
                      $"content={ReferenceEquals(tooltip.Content, video)}, tag={tooltip.Tag is TooltipPreviewState}",
                () => container.DataContext = video);

            Assert.Equal(TooltipPreviewPhase.Cover, state.Phase);
            Assert.Null(state.PreviewSource);
            Assert.Same(video.ThumbnailSource, state.CoverSource);

            var result = probe.Complete(TimeSpan.FromSeconds(10));
            var dpi = VisualTreeHelper.GetDpi(window);
            await window.CloseVideoTooltipAsync(container, tooltip);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var entry = new VideoPreviewPerformanceEntry(
                mediaProbe.Container,
                mediaProbe.Codec,
                $"{mediaProbe.PixelWidth}x{mediaProbe.PixelHeight}",
                mediaProbe.Duration.TotalSeconds,
                result.PrepareDuration?.TotalMilliseconds,
                result.PlaybackDuration?.TotalMilliseconds,
                result.MaximumDispatcherDuration.TotalMilliseconds,
                result.AverageFramesPerSecond,
                result.MaximumFrameGap.TotalMilliseconds,
                result.MaximumLiveProcessCount,
                result.Passed,
                result.FailureReasons);
            return new MeasuredPreview(dpi.DpiScaleX, dpi.DpiScaleY, entry);
        }
        finally
        {
            if (Application.Current.Windows.Cast<Window>().Contains(window))
            {
                window.Close();
                WpfTestHost.DrainDispatcher(window.Dispatcher);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyWindowCloseStopsActiveMediaProcessAsync(
        string sourcePath,
        string processName)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AiriVideoPreviewWindowClose",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var loader = new HarnessThumbnailLoader();
        var viewModel = CreateViewModel(root, loader);
        var probe = VideoPreviewPerformanceProbe.CreateEnabled(TimeProvider.System);
        var observer = new SignalingProcessObserver(probe);
        var binaryFolder = Path.Combine(AppContext.BaseDirectory, "resources", "ffmpeg", "win-x64");
        var controller = new TooltipPreviewController(
            new FfmpegVideoPreviewService(
                new FfmpegCorePreviewBackend(binaryFolder, new MediaProcessRunner(observer))),
            TimeProvider.System,
            probe);
        var window = new MainWindow(
            viewModel,
            thumbnailImageLoader: loader,
            tooltipPreviewController: controller,
            clientAreaAnimationsEnabledOverride: true)
        {
            Width = 900,
            Height = 700,
            SizeToContent = SizeToContent.Manual,
            WindowState = WindowState.Normal,
            ShowInTaskbar = false
        };

        try
        {
            window.Show();
            await WaitUntilAsync(() => window.InitializationStarted, TimeSpan.FromSeconds(5));
            await window.InitializationTask.WaitAsync(TimeSpan.FromSeconds(10));
            var video = new VideoItem
            {
                LibraryPath = "./Videos/window-close.mp4",
                Title = "Window close preview",
                ThumbnailPath = string.Empty,
                ThumbnailUri = string.Empty
            };
            video.ReleaseThumbnail(loader.FallbackSource);
            video.UpdateFileState(
                sourcePath,
                new FileInfo(sourcePath).Length,
                File.GetLastWriteTimeUtc(sourcePath),
                VideoPresenceState.Available,
                File.GetCreationTimeUtc(sourcePath));
            viewModel.Videos.Clear();
            viewModel.Videos.Add(video);
            window.UpdateLayout();
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var list = window.GetVideoListForTests();
            await WaitUntilAsync(
                () => list.ItemContainerGenerator.ContainerFromItem(video) is ListBoxItem,
                TimeSpan.FromSeconds(5));
            var container = Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(video));

            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });
            var identity = await observer.WaitUntilStartedAsync(processName)
                .WaitAsync(TimeSpan.FromSeconds(8));
            if (processName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                Assert.False(probe.HasStartedPlayback);
            }
            else
            {
                await PumpUntilAsync(
                    () => probe.HasStartedPlayback,
                    window,
                    viewModel,
                    stressUi: false,
                    TimeSpan.FromSeconds(8));
            }

            Assert.True(IsProcessRunning(identity));
            window.Close();
            await observer.WaitUntilExitedAsync(identity).WaitAsync(TimeSpan.FromSeconds(8));
            await PumpDispatcherUntilAsync(
                () => !Application.Current.Windows.Cast<Window>().Contains(window),
                window.Dispatcher,
                TimeSpan.FromSeconds(8));

            Assert.Equal(0, probe.LiveProcessCount);
            Assert.False(IsProcessRunning(identity));
        }
        finally
        {
            if (Application.Current.Windows.Cast<Window>().Contains(window))
            {
                window.Close();
                WpfTestHost.DrainDispatcher(window.Dispatcher);
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task PumpDispatcherUntilAsync(
        Func<bool> condition,
        Dispatcher dispatcher,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            WpfTestHost.DrainDispatcher(dispatcher);
            await Task.Delay(20);
        }
        Assert.True(condition(), "WPF window did not close before timeout.");
    }

    private static bool IsProcessRunning(MediaProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() == identity.StartTimeUtc;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task PumpUntilAsync(
        Func<bool> condition,
        MainWindow window,
        MainViewModel viewModel,
        bool stressUi,
        TimeSpan timeout,
        Func<string>? failureDetail = null,
        Action? maintainSession = null)
    {
        var deadline = DateTime.UtcNow + timeout;
        var iteration = 0;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            if (stressUi)
            {
                var scroll = window.GetVideoScrollViewer();
                scroll?.ScrollToVerticalOffset(Math.Min(scroll.ScrollableHeight, (iteration % 4) * 12d));
                if (iteration % 8 == 0)
                {
                    viewModel.SearchQuery = iteration % 16 == 0 ? "Preview" : "performance";
                }
                maintainSession?.Invoke();
            }
            window.UpdateLayout();
            maintainSession?.Invoke();
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            await Task.Delay(20);
            iteration++;
        }
        Assert.True(
            condition(),
            $"Video preview performance session did not reach the expected state before timeout. {failureDetail?.Invoke()}");
    }

    private static string DescribeProbe(VideoPreviewPerformanceProbe probe)
    {
        var result = probe.Complete(TimeSpan.FromSeconds(10));
        return $"session={result.SessionId}, prepared={result.PrepareDuration}, " +
               $"playback={result.PlaybackDuration}, live={result.RemainingLiveProcessCount}, " +
               $"maxLive={result.MaximumLiveProcessCount}, " +
               $"failures={string.Join('|', result.FailureReasons)}";
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
        Assert.True(condition(), "WPF performance fixture did not become ready before timeout.");
    }

    private static MainViewModel CreateViewModel(string root, IThumbnailImageLoader loader)
    {
        var provider = new CrawlerSessionProvider();
        var source = new OneFourOneJavMetaSource(provider);
        var metadata = new WebMetadataService(
            new IWebVideoMetaSource[] { new NoopMetaSource() },
            new ThumbnailCache(root),
            NullTranslationService.Instance,
            "KO");
        return new MainViewModel(
            new LibraryStore(Path.Combine(root, "videos.json")),
            new LibraryScanner(new FileSystemScanner()),
            metadata,
            provider,
            source,
            new NoopCrawlerFactory(),
            ThumbnailPerformanceProbe.Disabled,
            loader);
    }

    private static IReadOnlyList<string> ResolvePerformanceInputs(
        string? videoPath,
        string? h264Path,
        string? hevcPath)
    {
        if (!string.IsNullOrWhiteSpace(videoPath))
        {
            return new[] { Path.GetFullPath(videoPath) };
        }

        return new[]
        {
            RequirePath(h264Path, "AIRI_VIDEO_PREVIEW_H264"),
            RequirePath(hevcPath, "AIRI_VIDEO_PREVIEW_HEVC")
        };
    }

    private static string RequirePath(string? value, string environmentName) =>
        !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value)
            : throw new InvalidOperationException(
                $"Required environment variable '{environmentName}' is missing.");

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    private sealed record MeasuredPreview(
        double DpiX,
        double DpiY,
        VideoPreviewPerformanceEntry Entry);

    private sealed record VideoPreviewPerformanceDocument(
        string BuildConfiguration,
        double DpiX,
        double DpiY,
        string WorkArea,
        string Cpu,
        string Gpu,
        long Memory,
        IReadOnlyList<VideoPreviewPerformanceEntry> Results);

    private sealed record VideoPreviewPerformanceEntry(
        string Container,
        string Codec,
        string Resolution,
        double Duration,
        double? PrepareMs,
        double? PlaybackMs,
        double MaxDispatcherMs,
        double AverageDisplayedFps,
        double MaxFrameGapMs,
        int TotalProcessMaximum,
        bool Passed,
        IReadOnlyList<string> FailureReasons);

    private sealed class SignalingProcessObserver : IMediaProcessObserver
    {
        private readonly IMediaProcessObserver _inner;
        private readonly TaskCompletionSource<MediaProcessIdentity> _ffprobeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<MediaProcessIdentity> _ffmpegStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<(int ProcessId, DateTime StartTimeUtc), TaskCompletionSource> _exits = new();
        private readonly object _sync = new();

        public SignalingProcessObserver(IMediaProcessObserver inner) => _inner = inner;

        public void Started(MediaProcessIdentity identity)
        {
            lock (_sync)
            {
                _exits[(identity.ProcessId, identity.StartTimeUtc)] =
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            _inner.Started(identity);
            var fileName = Path.GetFileName(identity.ExecutablePath);
            if (fileName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                _ffprobeStarted.TrySetResult(identity);
            }
            else if (fileName.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                _ffmpegStarted.TrySetResult(identity);
            }
        }

        public void Exited(MediaProcessIdentity identity, int? exitCode)
        {
            _inner.Exited(identity, exitCode);
            lock (_sync)
            {
                if (_exits.TryGetValue((identity.ProcessId, identity.StartTimeUtc), out var exit))
                {
                    exit.TrySetResult();
                }
            }
        }

        public Task<MediaProcessIdentity> WaitUntilStartedAsync(string processName) =>
            processName.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)
                ? _ffprobeStarted.Task
                : _ffmpegStarted.Task;

        public Task WaitUntilExitedAsync(MediaProcessIdentity identity)
        {
            lock (_sync)
            {
                return _exits[(identity.ProcessId, identity.StartTimeUtc)].Task;
            }
        }
    }

    private sealed class HarnessThumbnailLoader : IThumbnailImageLoader
    {
        public HarnessThumbnailLoader()
        {
            var pixels = new byte[8 * 8 * 4];
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            bitmap.Freeze();
            FallbackSource = bitmap;
        }

        public ImageSource FallbackSource { get; }

        public Task<ThumbnailImageResult> LoadAsync(
            string thumbnailPath,
            int decodePixelWidth,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ThumbnailImageResult(FallbackSource, true));
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
