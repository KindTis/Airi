using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class ThumbnailPerformanceHarnessTests
{
    [Fact]
    public void PerformanceProbe_MarkersShareOriginAndCannotBeOverwritten()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();

        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        Assert.True(probe.TryMark(StartupTimingMarker.StartupMeasurementBegin));
        Assert.True(probe.TryMark(StartupTimingMarker.MainWindowLoaded));
        Assert.False(probe.TryMark(StartupTimingMarker.MainWindowLoaded));
        var snapshot = probe.EndMeasurementPhase();

        Assert.Equal(ThumbnailPerformanceProbe.SchemaVersion, snapshot.SchemaVersion);
        Assert.Equal(ThumbnailMeasurementPhase.Cold, snapshot.Phase);
        Assert.Equal(0d, snapshot.Markers[StartupTimingMarker.StartupMeasurementBegin].ElapsedMilliseconds);
        Assert.True(snapshot.Markers[StartupTimingMarker.MainWindowLoaded].ElapsedTicks >= 0);
        Assert.Equal(2, snapshot.Markers.Count);
    }

    [Fact]
    public void PerformanceProbe_DisabledIsNoOp()
    {
        var probe = ThumbnailPerformanceProbe.Disabled;

        Assert.False(probe.TryMark(StartupTimingMarker.MainWindowLoaded));
        Assert.False(probe.IsActive);
    }

    [Fact]
    public void PerformanceProbe_EndMeasurementPhaseSealsStableSnapshot()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Warm);
        probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);
        var snapshot = probe.EndMeasurementPhase();

        Assert.False(probe.TryMark(StartupTimingMarker.MainWindowLoaded));
        Assert.Single(snapshot.Markers);
        Assert.False(probe.IsActive);
    }

    [Fact]
    public void WpfTestHost_CloseLastWindow_KeepsDispatcherAlive()
    {
        WpfTestHost.Run(() =>
        {
            var dispatcher = Application.Current.Dispatcher;
            var window = new Window();
            window.Show();
            window.Close();
            WpfTestHost.DrainDispatcher(dispatcher);
            Assert.False(dispatcher.HasShutdownStarted);
        });
    }

    [Fact]
    public async Task WpfTestHost_ColdWindowClose_AllowsWarmWindowCreation()
    {
        var threadIds = new int[2];
        await WpfTestHost.RunAsync(() =>
        {
            threadIds[0] = Environment.CurrentManagedThreadId;
            var cold = new Window();
            cold.Show();
            cold.Close();
            return Task.CompletedTask;
        });

        await WpfTestHost.RunAsync(() =>
        {
            threadIds[1] = Environment.CurrentManagedThreadId;
            var warm = new Window();
            warm.Show();
            warm.Close();
            return Task.CompletedTask;
        });

        Assert.Equal(threadIds[0], threadIds[1]);
    }

    [Fact]
    public void PerformanceSnapshot_CommonSchemaContainsVisualMarkers()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);
        probe.TryMark(StartupTimingMarker.MainWindowLoaded);
        probe.TryMark(StartupTimingMarker.LibraryLoaded);
        probe.TryMark(StartupTimingMarker.FirstBatchPublished);
        probe.TryMark(StartupTimingMarker.VisualFirstMeaningfulCard);
        probe.TryMark(StartupTimingMarker.VisualFirstThumbnail);
        probe.TryMark(StartupTimingMarker.AllItemsPublished);
        var snapshot = probe.EndMeasurementPhase();

        var expected = new[]
        {
            StartupTimingMarker.StartupMeasurementBegin,
            StartupTimingMarker.MainWindowLoaded,
            StartupTimingMarker.LibraryLoaded,
            StartupTimingMarker.FirstBatchPublished,
            StartupTimingMarker.VisualFirstMeaningfulCard,
            StartupTimingMarker.VisualFirstThumbnail,
            StartupTimingMarker.AllItemsPublished
        };
        Assert.True(expected.All(snapshot.Markers.ContainsKey));
    }

    [Fact]
    public async Task ProductionWindow_RecordsCommonVisualMarkersFromRealCard()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriThumbnailHarnessTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            await WpfTestHost.RunAsync(async () =>
            {
                var mediaRoot = Path.Combine(root, "media");
                Directory.CreateDirectory(mediaRoot);
                var videoPath = Path.Combine(mediaRoot, "sample.mp4");
                await File.WriteAllBytesAsync(videoPath, new byte[] { 0 });
                var thumbnailPath = Path.Combine(root, "sample.jpg");
                WriteJpeg(thumbnailPath);

                var store = new LibraryStore(Path.Combine(root, "videos.json"));
                var library = new LibraryData
                {
                    Targets = new List<TargetFolder>
                    {
                        new(mediaRoot, new[] { "*.mp4" }, Array.Empty<string>(), null)
                    },
                    Videos = new List<VideoEntry>
                    {
                        new(videoPath,
                            new VideoMeta("Sample", null, Array.Empty<string>(), thumbnailPath, Array.Empty<string>(), string.Empty),
                            1,
                            File.GetLastWriteTimeUtc(videoPath),
                            File.GetCreationTimeUtc(videoPath))
                    }
                };
                await store.SaveAsync(library);

                var probe = ThumbnailPerformanceProbe.CreateEnabled();
                probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
                probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);
                var viewModel = CreateViewModel(root, store, probe);
                var window = new MainWindow(viewModel, probe)
                {
                    Width = 1500,
                    Height = 1000,
                    WindowState = WindowState.Normal,
                    SizeToContent = SizeToContent.Manual
                };

                window.Show();
                await WaitUntilAsync(() =>
                    probe.HasMarker(StartupTimingMarker.VisualFirstMeaningfulCard) &&
                    probe.HasMarker(StartupTimingMarker.VisualFirstThumbnail) &&
                    probe.HasMarker(StartupTimingMarker.AllItemsPublished),
                    () => $"Recorded markers: {string.Join(", ", probe.GetRecordedMarkers())}. {window.GetLegacyThumbnailDiagnostic()}");
                window.Close();

                var snapshot = probe.EndMeasurementPhase();
                Assert.True(snapshot.Markers[StartupTimingMarker.FirstBatchPublished].ElapsedTicks <=
                            snapshot.Markers[StartupTimingMarker.VisualFirstMeaningfulCard].ElapsedTicks);
                Assert.True(snapshot.Markers[StartupTimingMarker.FirstBatchPublished].ElapsedTicks <=
                            snapshot.Markers[StartupTimingMarker.VisualFirstThumbnail].ElapsedTicks);
            });
        }
        finally
        {
            if (Directory.Exists(root))
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                try
                {
                    Directory.Delete(root, recursive: true);
                }
                catch (IOException)
                {
                    // Legacy URI binding can retain an image handle in WPF's process cache.
                }
            }
        }
    }

    [Fact]
    public async Task Measure()
    {
        var outputPath = Environment.GetEnvironmentVariable("AIRI_PERF_OUTPUT");
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            return;
        }

        var iterationRoot = RequireEnvironment("AIRI_PERF_ITERATION_ROOT");
        var coldStorePath = RequireEnvironment("AIRI_PERF_COLD_STORE");
        var warmStorePath = RequireEnvironment("AIRI_PERF_WARM_STORE");
        AssertPathUnder(iterationRoot, AppContext.BaseDirectory);
        AssertPathUnder(iterationRoot, coldStorePath);
        AssertPathUnder(iterationRoot, warmStorePath);
        AssertPathUnder(iterationRoot, outputPath);
        Assert.NotEqual(Path.GetFullPath(coldStorePath), Path.GetFullPath(warmStorePath));

        ThumbnailPerformanceRawDocument? document = null;
        await WpfTestHost.RunAsync(async () =>
        {
            var probe = ThumbnailPerformanceProbe.CreateEnabled();
            var cold = await MeasurePhaseAsync(
                iterationRoot,
                coldStorePath,
                probe,
                ThumbnailMeasurementPhase.Cold,
                performWarmPreparation: true);
            WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
            var warm = await MeasurePhaseAsync(
                iterationRoot,
                warmStorePath,
                probe,
                ThumbnailMeasurementPhase.Warm,
                performWarmPreparation: false);

            document = new ThumbnailPerformanceRawDocument(
                ThumbnailPerformanceProbe.SchemaVersion,
                RequireEnvironment("AIRI_PERF_DATASET"),
                int.Parse(RequireEnvironment("AIRI_PERF_ITERATION"), System.Globalization.CultureInfo.InvariantCulture),
                Environment.GetEnvironmentVariable("AIRI_PERF_MODE") ?? "After",
                cold,
                warm,
                new EnvironmentCompatibility(
                    Environment.GetEnvironmentVariable("AIRI_PERF_MACHINE_LABEL") ?? "local-windows",
                    Environment.OSVersion.VersionString,
                    RuntimeInformation.FrameworkDescription,
                    RuntimeInformation.ProcessArchitecture.ToString(),
                    "Release",
                    Environment.GetEnvironmentVariable("AIRI_PERF_POWER_SCHEME") ?? "unknown"),
                new BuildProvenance(
                    Environment.GetEnvironmentVariable("AIRI_PERF_COMMIT_SHA") ?? "unknown",
                    ComputeSha256(Path.Combine(AppContext.BaseDirectory, "Airi.dll")),
                    string.Equals(Environment.GetEnvironmentVariable("AIRI_PERF_DIRTY"), "true", StringComparison.OrdinalIgnoreCase)));
        });

        Assert.NotNull(document);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        options.Converters.Add(new JsonStringEnumConverter());
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(document, options));
    }

    private static async Task<ThumbnailPerformancePhaseResult> MeasurePhaseAsync(
        string iterationRoot,
        string storePath,
        ThumbnailPerformanceProbe probe,
        ThumbnailMeasurementPhase phase,
        bool performWarmPreparation)
    {
        var inputHash = ComputeSha256(storePath);
        probe.BeginMeasurementPhase(phase);
        probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);

        var store = new LibraryStore(storePath);
        var viewModel = CreateViewModel(iterationRoot, store, probe);
        var window = new MainWindow(viewModel, probe)
        {
            WindowState = WindowState.Normal,
            SizeToContent = SizeToContent.Manual,
            Width = 1500,
            Height = 1000
        };

        window.Show();
        await WaitUntilAsync(() =>
            probe.HasMarker(StartupTimingMarker.MainWindowLoaded) &&
            probe.HasMarker(StartupTimingMarker.LibraryLoaded) &&
            probe.HasMarker(StartupTimingMarker.FirstBatchPublished) &&
            probe.HasMarker(StartupTimingMarker.VisualFirstMeaningfulCard) &&
            probe.HasMarker(StartupTimingMarker.VisualFirstThumbnail) &&
            probe.HasMarker(StartupTimingMarker.AllItemsPublished) &&
            probe.HasMarker(StartupTimingMarker.StartupTerminal),
            () => $"Phase {phase}; markers: {string.Join(", ", probe.GetRecordedMarkers())}; {window.GetLegacyThumbnailDiagnostic()}",
            timeout: TimeSpan.FromMinutes(2));
        await Task.Delay(500);
        WpfTestHost.DrainDispatcher(window.Dispatcher);

        var dpi = VisualTreeHelper.GetDpi(window);
        var workArea = SystemParameters.WorkArea;
        var scrollViewer = window.GetVideoScrollViewer();
        var extent = window.GetFirstVideoContainerExtent();
        var viewport = new ViewportSnapshot(
            window.ActualWidth,
            window.ActualHeight,
            scrollViewer?.ViewportWidth ?? 0,
            scrollViewer?.ViewportHeight ?? 0,
            extent.Width,
            extent.Height,
            dpi.DpiScaleX,
            dpi.DpiScaleY,
            scrollViewer?.HorizontalOffset ?? 0,
            scrollViewer?.VerticalOffset ?? 0,
            window.GetRealizedVideoContainerCount(),
            viewModel.SearchQuery,
            viewModel.SelectedActor,
            viewModel.ShowMissingMetadataOnly,
            viewModel.SelectedSortOption.Label,
            workArea.Width,
            workArea.Height);
        var compatible = Math.Abs(dpi.DpiScaleX - 1d) < 0.001 &&
                         Math.Abs(dpi.DpiScaleY - 1d) < 0.001 &&
                         workArea.Width >= 1500 &&
                         workArea.Height >= 1000;
        var invalidReason = compatible
            ? null
            : $"Requires 100% DPI and at least 1500x1000 DIP working area; observed {dpi.DpiScaleX:F2}x{dpi.DpiScaleY:F2}, {workArea.Width:F0}x{workArea.Height:F0}.";
        var snapshot = probe.EndMeasurementPhase();

        WarmPreparationSnapshot? warmPreparation = null;
        if (performWarmPreparation)
        {
            var before = snapshot.Markers.Count;
            window.ScrollVideoToIndex(Math.Max(0, viewModel.Videos.Count - 1));
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            window.ScrollVideoToIndex(0);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            warmPreparation = new WarmPreparationSnapshot(
                viewModel.Videos.Count,
                window.GetRealizedVideoContainerCount(),
                before == snapshot.Markers.Count);
        }

        window.Close();
        WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
        var outputHash = ComputeSha256(storePath);

        return new ThumbnailPerformancePhaseResult(
            compatible,
            invalidReason,
            compatible ? snapshot : null,
            viewport,
            inputHash,
            outputHash,
            warmPreparation);
    }

    private static MainViewModel CreateViewModel(
        string root,
        LibraryStore store,
        ThumbnailPerformanceProbe probe)
    {
        var provider = new CrawlerSessionProvider();
        var source = new OneFourOneJavMetaSource(provider);
        var metadata = new WebMetadataService(
            new IWebVideoMetaSource[] { new NoopMetaSource() },
            new ThumbnailCache(root),
            NullTranslationService.Instance,
            "KO");
        return new MainViewModel(
            store,
            new LibraryScanner(new FileSystemScanner()),
            metadata,
            provider,
            source,
            new NoopCrawlerFactory(),
            probe,
            new TestThumbnailImageLoader());
    }

    private static void WriteJpeg(string path)
    {
        var pixels = Enumerable.Repeat((byte)0x7f, 32 * 20 * 4).ToArray();
        var bitmap = BitmapSource.Create(32, 20, 96, 96, PixelFormats.Bgra32, null, pixels, 32 * 4);
        var encoder = new JpegBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        Func<string>? failureDetail = null,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow.Add(timeout ?? TimeSpan.FromSeconds(10));
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(predicate(),
            $"The production window did not reach the expected visual marker before timeout. {failureDetail?.Invoke()}");
    }

    private static string RequireEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Required environment variable '{name}' is missing.");

    private static void AssertPathUnder(string root, string candidate)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        Assert.StartsWith(normalizedRoot, normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
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
            Task.FromException<OneFourOneJavCrawlerStartResult>(new InvalidOperationException("Crawler is disabled in performance tests."));
    }

    private sealed record ThumbnailPerformanceRawDocument(
        int SchemaVersion,
        string Dataset,
        int Iteration,
        string Mode,
        ThumbnailPerformancePhaseResult Cold,
        ThumbnailPerformancePhaseResult Warm,
        EnvironmentCompatibility EnvironmentCompatibility,
        BuildProvenance BuildProvenance);

    private sealed record ThumbnailPerformancePhaseResult(
        bool Valid,
        string? InvalidReason,
        ThumbnailPerformanceSnapshot? Timing,
        ViewportSnapshot Viewport,
        string InputLibrarySha256,
        string OutputLibrarySha256,
        WarmPreparationSnapshot? WarmPreparation);

    private sealed record ViewportSnapshot(
        double WindowWidth,
        double WindowHeight,
        double ViewportWidth,
        double ViewportHeight,
        double FirstItemWidth,
        double FirstItemHeight,
        double DpiScaleX,
        double DpiScaleY,
        double HorizontalOffset,
        double VerticalOffset,
        int RealizedContainerCount,
        string SearchQuery,
        string SelectedActor,
        bool MissingOnly,
        string SortLabel,
        double WorkingAreaWidth,
        double WorkingAreaHeight);

    private sealed record WarmPreparationSnapshot(
        int ItemCount,
        int RealizedContainerCount,
        bool TimingSnapshotStayedSealed);

    private sealed record EnvironmentCompatibility(
        string MachineLabel,
        string OsBuild,
        string Runtime,
        string ProcessArchitecture,
        string Configuration,
        string PowerSchemeLabel);

    private sealed record BuildProvenance(
        string CommitSha,
        string BinarySha256,
        bool DirtyWorktree);
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfTestCollection
{
    public const string Name = "Airi WPF tests";
}
