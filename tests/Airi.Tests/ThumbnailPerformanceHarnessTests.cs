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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
    public void PerformanceProbe_RecordsRequestMembershipMissesAndDispatcherDurations()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.EnterImageRegistration(10, 20);
        probe.RecordThumbnailRequest(20, 1, "thumbnail.jpg", 320);
        probe.RecordDispatcherBatch("StoredPublish", 40, 123);

        var snapshot = probe.EndMeasurementPhase();

        var request = Assert.Single(snapshot.Requests);
        Assert.True(request.InRealizationWindow);
        var batch = Assert.Single(snapshot.DispatcherBatches);
        Assert.Equal("StoredPublish", batch.Kind);
        Assert.Equal(40, batch.ItemCount);
        Assert.Equal(123, batch.ElapsedTicks);
        Assert.Equal(1, snapshot.ActiveRegistrationCount);
        Assert.Equal(1, snapshot.MaxRegistrationCount);
    }

    [Fact]
    public void BeginWarm_RequiresZeroRegistrationsAfterColdCleanup()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.EnterImageRegistration(1, 2);
        _ = probe.EndMeasurementPhase();

        Assert.Throws<InvalidOperationException>(() =>
            probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Warm));

        probe.LeaveImageRegistration(1, 2);
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Warm);
        Assert.Equal(ThumbnailMeasurementPhase.Warm, probe.EndMeasurementPhase().Phase);
    }

    [Fact]
    public void BeginMeasurementPhase_WithActiveDecode_RejectsBoundary()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();

        Assert.Throws<InvalidOperationException>(() => probe.BeginMeasurementPhase(
            ThumbnailMeasurementPhase.Cold,
            new ThumbnailPhaseBoundaryState(
                ActiveDecodeCount: 1,
                ViewModelInFlightCount: 0,
                RegistrationCount: 0,
                PreviousRuntimeStateCount: 0,
                DispatcherDrained: true)));
    }

    [Fact]
    public void EndMeasurementPhase_RejectsUnstableOrInFlightSeal()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.TryMark(StartupTimingMarker.StartupTerminal);

        Assert.Throws<InvalidOperationException>(() => probe.EndMeasurementPhase(
            new ThumbnailPhaseSealState(
                ActiveDecodeCount: 0,
                ViewModelInFlightCount: 1,
                StableFor500Milliseconds: false,
                AllActiveRegistrationsTerminal: true)));

        var snapshot = probe.EndMeasurementPhase(new ThumbnailPhaseSealState(0, 0, true, true));
        Assert.Contains(snapshot.ResourceCheckpoints, checkpoint =>
            checkpoint.Kind == ThumbnailResourceCheckpointKind.PhaseEnd);
    }

    [Fact]
    public void Snapshot_MemoryMetricsUseAbsoluteGaugesAndGcPhaseDeltas()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.TryMark(StartupTimingMarker.VisualFirstMeaningfulCard);
        probe.TryMark(StartupTimingMarker.VisualFirstThumbnail);
        probe.RecordFirstSteadyCheckpoint();
        probe.TryMark(StartupTimingMarker.StartupTerminal);

        var snapshot = probe.EndMeasurementPhase(new ThumbnailPhaseSealState(0, 0, true, true));

        Assert.Contains(snapshot.ResourceCheckpoints, checkpoint => checkpoint.Kind == ThumbnailResourceCheckpointKind.PhaseStart);
        Assert.Contains(snapshot.ResourceCheckpoints, checkpoint => checkpoint.Kind == ThumbnailResourceCheckpointKind.VisualFirstMeaningfulCard);
        Assert.Contains(snapshot.ResourceCheckpoints, checkpoint => checkpoint.Kind == ThumbnailResourceCheckpointKind.VisualFirstThumbnail);
        Assert.Contains(snapshot.ResourceCheckpoints, checkpoint => checkpoint.Kind == ThumbnailResourceCheckpointKind.FirstSteady);
        Assert.Contains(snapshot.ResourceCheckpoints, checkpoint => checkpoint.Kind == ThumbnailResourceCheckpointKind.StartupTerminal);
        Assert.All(snapshot.ResourceCheckpoints, checkpoint =>
        {
            Assert.True(checkpoint.WorkingSetBytes > 0);
            Assert.True(checkpoint.ManagedHeapBytes > 0);
            Assert.True(checkpoint.Gc0CollectionCount >= 0);
            Assert.True(checkpoint.Gc1CollectionCount >= 0);
            Assert.True(checkpoint.Gc2CollectionCount >= 0);
        });
        Assert.Equal(snapshot.ResourceCheckpoints.Max(x => x.WorkingSetBytes), snapshot.CheckpointMaximum.WorkingSetBytes);
        Assert.Equal(snapshot.ResourceCheckpoints.Max(x => x.ManagedHeapBytes), snapshot.CheckpointMaximum.ManagedHeapBytes);
        Assert.True(snapshot.GcPhaseDelta.Gen0 >= 0);
        Assert.True(snapshot.GcPhaseDelta.Gen1 >= 0);
        Assert.True(snapshot.GcPhaseDelta.Gen2 >= 0);
    }

    [Fact]
    public void PerformanceProbe_DecodedOwnerGaugeCanExposeOwnerOutsideBound()
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.EnterDecodedStrongReferenceOwner(ThumbnailDecodedOwnerKind.LoaderCache, 10);
        probe.EnterDecodedStrongReferenceOwner(ThumbnailDecodedOwnerKind.RealizedItemSource, 20);
        probe.EnterDecodedStrongReferenceOwner(ThumbnailDecodedOwnerKind.Unexpected, 30);
        probe.RecordFirstSteadyCheckpoint();
        probe.TryMark(StartupTimingMarker.StartupTerminal);

        var snapshot = probe.EndMeasurementPhase(new ThumbnailPhaseSealState(0, 0, true, true));
        var checkpoint = Assert.Single(snapshot.ResourceCheckpoints,
            value => value.Kind == ThumbnailResourceCheckpointKind.FirstSteady);
        Assert.Equal(3, checkpoint.DecodedStrongReferenceCount);
        Assert.Equal(2, checkpoint.OwnerSlotBound);
        Assert.True(checkpoint.DecodedStrongReferenceCount > checkpoint.OwnerSlotBound);
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
            probe.BeginMeasurementPhase(
                ThumbnailMeasurementPhase.Cold,
                new ThumbnailPhaseBoundaryState(0, 0, 0, 0, true));
            probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);
            var sharedLoader = await ThumbnailImageLoader.CreateAsync(probe, CancellationToken.None);
            var cold = await MeasurePhaseAsync(
                iterationRoot,
                coldStorePath,
                probe,
                sharedLoader,
                ThumbnailMeasurementPhase.Cold,
                performWarmPreparation: true);
            WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
            Assert.Equal(0, probe.GetActiveRegistrationCount());
            Assert.Equal(0, sharedLoader.GetDiagnostics().ActiveDecodeCount);
            var warm = await MeasurePhaseAsync(
                iterationRoot,
                warmStorePath,
                probe,
                sharedLoader,
                ThumbnailMeasurementPhase.Warm,
                performWarmPreparation: false);

            document = new ThumbnailPerformanceRawDocument(
                ThumbnailPerformanceProbe.SchemaVersion,
                RequireEnvironment("AIRI_PERF_DATASET"),
                int.Parse(RequireEnvironment("AIRI_PERF_ITERATION"), System.Globalization.CultureInfo.InvariantCulture),
                Environment.GetEnvironmentVariable("AIRI_PERF_MODE") ?? "After",
                RequireEnvironment("AIRI_PERF_MANIFEST_SHA256"),
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
        ThumbnailImageLoader sharedLoader,
        ThumbnailMeasurementPhase phase,
        bool performWarmPreparation)
    {
        var inputHash = ComputeSha256(storePath);
        var loaderStart = sharedLoader.GetDiagnostics();
        if (!probe.IsActive)
        {
            WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
            probe.BeginMeasurementPhase(
                phase,
                new ThumbnailPhaseBoundaryState(
                    loaderStart.ActiveDecodeCount,
                    ViewModelInFlightCount: 0,
                    probe.GetActiveRegistrationCount(),
                    PreviousRuntimeStateCount: 0,
                    DispatcherDrained: true));
            probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);
        }

        var store = new LibraryStore(storePath);
        var viewModel = CreateViewModel(iterationRoot, store, probe, sharedLoader);
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
        await WaitForPhaseSteadyAsync(window, viewModel, sharedLoader);
        probe.RecordFirstSteadyCheckpoint();

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
        var snapshot = probe.EndMeasurementPhase(new ThumbnailPhaseSealState(
            sharedLoader.GetDiagnostics().ActiveDecodeCount,
            viewModel.GetThumbnailInFlightCount(),
            StableFor500Milliseconds: true,
            window.AreActiveThumbnailRegistrationsTerminalForTests()));
        var loaderEnd = sharedLoader.GetDiagnostics();
        var nonFallbackSources = window.GetRealizedNonFallbackThumbnailSourceCount(sharedLoader.FallbackSource);
        var gates = BuildPhaseGates(
            RequireEnvironment("AIRI_PERF_DATASET"),
            snapshot,
            loaderStart,
            loaderEnd,
            nonFallbackSources);
        var structural = await CaptureStructuralValidationAsync(
            window,
            viewModel,
            sharedLoader,
            includeReturnTop: performWarmPreparation);

        WarmPreparationSnapshot? warmPreparation = null;
        if (performWarmPreparation)
        {
            var before = snapshot.Markers.Count;
            warmPreparation = new WarmPreparationSnapshot(
                viewModel.Videos.Count,
                window.GetRealizedVideoContainerCount(),
                before == snapshot.Markers.Count);
        }

        window.Close();
        WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
        Assert.Equal(0, viewModel.GetThumbnailInFlightCount());
        Assert.Equal(0, viewModel.GetThumbnailRuntimeStateCount());
        Assert.Equal(sharedLoader.GetDiagnostics().CacheEntryCount, probe.GetDecodedStrongReferenceOwnerCount());
        var outputHash = ComputeSha256(storePath);

        return new ThumbnailPerformancePhaseResult(
            compatible,
            invalidReason,
            compatible ? snapshot : null,
            viewport,
            inputHash,
            outputHash,
            warmPreparation,
            structural,
            gates,
            new PhaseLoaderDiagnostics(
                loaderEnd.CacheMissRequestCount - loaderStart.CacheMissRequestCount,
                loaderEnd.FileOpenCount - loaderStart.FileOpenCount,
                loaderEnd.FileChangeRetryAttemptCount - loaderStart.FileChangeRetryAttemptCount,
                loaderEnd.ActiveDecodeCount,
                loaderEnd.MaxConcurrentDecodeCount,
                loaderStart.CacheEntryCount,
                loaderEnd.CacheEntryCount,
                loaderEnd.RecencyNodeCount,
                loaderEnd.FallbackInitializationThreadId,
                loaderEnd.FallbackInitializationElapsedTicks));
    }

    private static PhaseGateResults BuildPhaseGates(
        string dataset,
        ThumbnailPerformanceSnapshot snapshot,
        ThumbnailImageLoaderDiagnostics loaderStart,
        ThumbnailImageLoaderDiagnostics loaderEnd,
        int nonFallbackSources)
    {
        var firstCardGate = dataset is "current" or "stress"
            ? snapshot.Markers[StartupTimingMarker.VisualFirstMeaningfulCard].ElapsedTicks <
              snapshot.Markers[StartupTimingMarker.AllItemsPublished].ElapsedTicks
                ? "Pass"
                : "Fail"
            : "NotApplicable";
        var phaseMisses = loaderEnd.CacheMissRequestCount - loaderStart.CacheMissRequestCount;
        var phaseOpens = loaderEnd.FileOpenCount - loaderStart.FileOpenCount;
        var phaseRetries = loaderEnd.FileChangeRetryAttemptCount - loaderStart.FileChangeRetryAttemptCount;
        return new PhaseGateResults(
            firstCardGate,
            snapshot.Requests.All(request => request.InRealizationWindow),
            phaseOpens <= phaseMisses + phaseRetries,
            loaderEnd.MaxConcurrentDecodeCount <= 4,
            loaderEnd.CacheEntryCount == loaderEnd.RecencyNodeCount && loaderEnd.CacheEntryCount <= 96,
            snapshot.DispatcherBatches.All(batch => batch.ElapsedTicks <= Stopwatch.Frequency / 10),
            nonFallbackSources <= snapshot.ActiveRegistrationCount,
            snapshot.ResourceCheckpoints.All(checkpoint =>
                checkpoint.DecodedStrongReferenceCount <= checkpoint.OwnerSlotBound));
    }

    private static async Task<StructuralValidationSnapshot> CaptureStructuralValidationAsync(
        MainWindow window,
        MainViewModel viewModel,
        ThumbnailImageLoader loader,
        bool includeReturnTop)
    {
        var sections = new List<StructuralPositionSnapshot>();
        var positions = new List<(string Name, int Index)>
        {
            ("top", 0),
            ("middle", Math.Max(0, viewModel.Videos.Count / 2)),
            ("last", Math.Max(0, viewModel.Videos.Count - 1))
        };
        if (includeReturnTop)
        {
            positions.Add(("topReturn", 0));
        }

        foreach (var (name, index) in positions)
        {
            var traversalMaximum = window.GetRealizedVideoContainerCount();
            ItemsChangedEventHandler observe = (_, _) =>
                traversalMaximum = Math.Max(traversalMaximum, window.GetRealizedVideoContainerCount());
            window.GetVideoListForTests().ItemContainerGenerator.ItemsChanged += observe;
            window.ScrollVideoToIndex(index);
            var deadline = DateTime.UtcNow.AddSeconds(30);
            var stableSince = DateTime.UtcNow;
            var previous = (-1, -1, -1);
            var steadyCount = 0;
            var steadyRegistrations = 0;
            var steadySources = 0;
            try
            {
                while (DateTime.UtcNow < deadline)
                {
                    WpfTestHost.DrainDispatcher(window.Dispatcher);
                    steadyCount = window.GetRealizedVideoContainerCount();
                    steadyRegistrations = window.GetThumbnailRegistrationCountForTests();
                    steadySources = window.GetRealizedNonFallbackThumbnailSourceCount(loader.FallbackSource);
                    traversalMaximum = Math.Max(traversalMaximum, steadyCount);
                    var current = (steadyCount, steadyRegistrations, steadySources);
                    var quiescent = loader.GetDiagnostics().ActiveDecodeCount == 0 &&
                                    viewModel.GetThumbnailInFlightCount() == 0 &&
                                    window.AreActiveThumbnailRegistrationsTerminalForTests();
                    if (quiescent && current == previous)
                    {
                        if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500))
                        {
                            break;
                        }
                    }
                    else
                    {
                        previous = current;
                        stableSince = DateTime.UtcNow;
                    }
                    await Task.Delay(20);
                }
            }
            finally
            {
                window.GetVideoListForTests().ItemContainerGenerator.ItemsChanged -= observe;
            }
            Assert.True(DateTime.UtcNow < deadline, $"Structural position '{name}' did not become stable.");
            var scroll = Assert.IsType<ScrollViewer>(window.GetVideoScrollViewer());
            var extent = window.GetFirstVideoContainerExtent();
            var columns = Math.Max(1, (int)Math.Floor(scroll.ViewportWidth / Math.Max(1, extent.Width)));
            var visibleRows = Math.Max(1, (int)Math.Ceiling(scroll.ViewportHeight / Math.Max(1, extent.Height)));
            var hardLimit = ((3 * visibleRows) + 1) * columns;
            Assert.InRange(steadyCount, 1, hardLimit);
            Assert.InRange(traversalMaximum, 1, hardLimit);
            sections.Add(new StructuralPositionSnapshot(
                name,
                index,
                scroll.VerticalOffset,
                steadyCount,
                traversalMaximum,
                steadyRegistrations,
                steadySources,
                extent.Width,
                extent.Height,
                columns,
                hardLimit));
        }

        return new StructuralValidationSnapshot(
            sections,
            sections.All(section =>
                section.RealizedContainerCount <= section.HardLimit &&
                section.TraversalMaximumContainerCount <= section.HardLimit));
    }

    private static async Task WaitForPhaseSteadyAsync(
        MainWindow window,
        MainViewModel viewModel,
        ThumbnailImageLoader loader)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var stableSince = DateTime.UtcNow;
        var previous = (-1, -1, -1);
        while (DateTime.UtcNow < deadline)
        {
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var current = (
                window.GetRealizedVideoContainerCount(),
                window.GetThumbnailRegistrationCountForTests(),
                window.GetRealizedNonFallbackThumbnailSourceCount(loader.FallbackSource));
            var quiescent = loader.GetDiagnostics().ActiveDecodeCount == 0 &&
                            viewModel.GetThumbnailInFlightCount() == 0 &&
                            window.AreActiveThumbnailRegistrationsTerminalForTests();
            if (quiescent && current == previous)
            {
                if (DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(500))
                {
                    return;
                }
            }
            else
            {
                previous = current;
                stableSince = DateTime.UtcNow;
            }
            await Task.Delay(20);
        }

        Assert.Fail("The performance phase did not reach a 500ms stable terminal state.");
    }

    private static MainViewModel CreateViewModel(
        string root,
        LibraryStore store,
        ThumbnailPerformanceProbe probe,
        IThumbnailImageLoader? thumbnailLoader = null)
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
            thumbnailLoader ?? CreateHarnessThumbnailLoader(probe));
    }

    private static ThumbnailImageLoader CreateHarnessThumbnailLoader(ThumbnailPerformanceProbe probe)
    {
        var fallback = new TestThumbnailImageLoader().FallbackSource;
        return new ThumbnailImageLoader(
            fallback,
            decoder: null,
            probe,
            failureSink: null,
            LibraryPathHelper.ResolveToAbsolute("resources/noimage.jpg"),
            capacity: 96,
            maxConcurrency: 4);
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
        string FixtureManifestSha256,
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
        WarmPreparationSnapshot? WarmPreparation,
        StructuralValidationSnapshot StructuralValidation,
        PhaseGateResults Gates,
        PhaseLoaderDiagnostics Loader);

    private sealed record PhaseLoaderDiagnostics(
        int CacheMissRequestCount,
        int FileOpenCount,
        int FileChangeRetryAttemptCount,
        int ActiveDecodeCount,
        int MaxConcurrentDecodeCount,
        int StartCacheEntryCount,
        int CacheEntryCount,
        int RecencyNodeCount,
        int FallbackInitializationThreadId,
        long FallbackInitializationElapsedTicks);

    private sealed record PhaseGateResults(
        string FirstCardBeforeAllItems,
        bool RequestMembership,
        bool FileOpenBound,
        bool DecodeConcurrency,
        bool LruInvariant,
        bool DispatcherUnder100Milliseconds,
        bool NonFallbackSourceBound,
        bool OwnerSlotBound);

    private sealed record StructuralValidationSnapshot(
        IReadOnlyList<StructuralPositionSnapshot> Positions,
        bool AllPassed);

    private sealed record StructuralPositionSnapshot(
        string Name,
        int Index,
        double VerticalOffset,
        int RealizedContainerCount,
        int TraversalMaximumContainerCount,
        int RegistrationCount,
        int RealizedNonFallbackSourceCount,
        double ItemWidth,
        double ItemHeight,
        int Columns,
        int HardLimit);

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
