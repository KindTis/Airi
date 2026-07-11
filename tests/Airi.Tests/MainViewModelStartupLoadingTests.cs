using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainViewModelStartupLoadingTests
{
    [Fact]
    public Task Constructor_StartsInLoadingAndCannotMutate() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();

        Assert.Equal(StartupLibraryState.Loading, viewModel.StartupState);
        Assert.False(viewModel.CanMutateLibrary);
        Assert.False(viewModel.IsScanning);
        Assert.False(viewModel.IsFetchingMetadata);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupState_ReadyWithoutOwner_CanMutate() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();

        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        Assert.True(viewModel.CanMutateLibrary);
        Assert.True(viewModel.FetchMetadataCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TryBeginLibraryMutation_AllowsExactlyOneDispatcherOwner() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        var first = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);
        var second = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.EditorSave);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(first, viewModel.CurrentMutationLease);
        Assert.True(viewModel.IsFetchingMetadata);
        Assert.False(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReleaseLibraryMutation_WrongIdentityCannotReleaseOwner() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        var lease = Assert.IsType<LibraryMutationLease>(
            viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));

        viewModel.ReleaseLibraryMutation(lease with { Identity = Guid.NewGuid() });
        Assert.Equal(lease, viewModel.CurrentMutationLease);

        viewModel.ReleaseLibraryMutation(lease);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.True(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupStateTransitions_DeriveIsScanningAcrossScanApplySave() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Scanning);
        Assert.True(viewModel.IsScanning);
        viewModel.SetStartupState(StartupLibraryState.ApplyingScan);
        Assert.True(viewModel.IsScanning);
        viewModel.SetStartupState(StartupLibraryState.SavingScan);
        Assert.True(viewModel.IsScanning);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        Assert.False(viewModel.IsScanning);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dispose_CancelsLifetimeAndAllLeases() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        Assert.NotNull(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.EditorSave));

        viewModel.Dispose();

        Assert.True(viewModel.LifetimeToken.IsCancellationRequested);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.False(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task InitializeAsync_OwnsOneStartupLeaseFromLoadThroughTerminalState() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var states = new List<(StartupLibraryState State, LibraryMutationLease? Lease)>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StartupState))
            {
                states.Add((viewModel.StartupState, viewModel.CurrentMutationLease));
            }
        };

        await viewModel.InitializeAsync();

        Assert.Equal(new[]
        {
            StartupLibraryState.Publishing,
            StartupLibraryState.Scanning,
            StartupLibraryState.ApplyingScan,
            StartupLibraryState.SavingScan,
            StartupLibraryState.Ready
        }, states.ConvertAll(entry => entry.State));
        var startupStates = states
            .Where(entry => entry.State != StartupLibraryState.Ready)
            .ToArray();
        var identities = startupStates.Select(entry => entry.Lease?.Identity).Distinct().ToArray();
        Assert.Single(identities);
        Assert.NotNull(identities[0]);
        Assert.All(startupStates, entry => Assert.Equal(LibraryMutationOwner.StartupScan, entry.Lease?.Owner));
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.True(viewModel.CanMutateLibrary);
        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Equal(1, fixture.Scanner.ScanCount);
        Assert.Equal(1, fixture.Store.SaveCount);
    });

    [Fact]
    public Task InitializeAsync_OperationCancelDuringLoad_PreservesOriginalLibraryBytesAndAllowsRetry() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var originalBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(fixture.Store.FilePath, originalBytes);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = async token =>
        {
            loadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new LibraryData();
        };
        using var cancellation = new CancellationTokenSource();

        var initialization = viewModel.InitializeAsync(cancellation.Token);
        await loadStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(fixture.Store.FilePath));
        Assert.Equal(StartupLibraryState.Loading, viewModel.StartupState);
        Assert.Empty(viewModel.Videos);
        Assert.Null(viewModel.CurrentMutationLease);

        fixture.Store.LoadOverride = _ => Task.FromResult(new LibraryData());
        await viewModel.InitializeAsync();
        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(2, fixture.Store.LoadCount);
    });

    [Fact]
    public Task InitializeAsync_ConcurrentCalls_DoNotCreateSecondStartupLease() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var releaseLoad = new TaskCompletionSource<LibraryData>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = _ => releaseLoad.Task;

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        releaseLoad.TrySetResult(new LibraryData());
        await Task.WhenAll(first, second);

        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Equal(1, fixture.Scanner.ScanCount);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
    });

    [Fact]
    public Task StartupStateChange_RaisesIsScanningAndSkeletonNotifications() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var changes = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changes.Add(args.PropertyName);
            }
        };

        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Scanning);
        viewModel.SetStartupState(StartupLibraryState.ApplyingScan);
        viewModel.SetStartupState(StartupLibraryState.SavingScan);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        Assert.Equal(2, changes.Count(name => name == nameof(MainViewModel.IsScanning)));
        Assert.Equal(2, changes.Count(name => name == nameof(MainViewModel.ShowVideoSkeleton)));
        Assert.Equal(2, changes.Count(name => name == nameof(MainViewModel.ShowActorSkeleton)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task EditorLease_BlocksAutoAndManualUntilSaveCancelOrException() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        var editor = Assert.IsType<LibraryMutationLease>(viewModel.TryBeginMetadataEditorMutation());
        Assert.Equal(LibraryMutationOwner.EditorSave, editor.Owner);
        Assert.Null(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata));
        Assert.Null(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));
        viewModel.EndMetadataEditorMutation(editor);
        Assert.True(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReadOnlyCommands_RemainEnabledWhileMutationIsBlocked() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SearchQuery = "sample";

        Assert.True(viewModel.ClearSearchCommand.CanExecute(null));
        Assert.True(viewModel.CanUseCommandBar);
        viewModel.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, viewModel.SearchQuery);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Faulted_ReleasesLeaseButKeepsMutationDisabled() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        fixture.Store.LoadOverride = _ => Task.FromException<LibraryData>(new IOException("load failed"));

        await Assert.ThrowsAsync<IOException>(() => viewModel.InitializeAsync());

        Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.False(viewModel.CanMutateLibrary);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
    });

    [Fact]
    public Task FetchCommand_RapidDoubleExecute_StartsOneManualLease() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        var first = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);
        var second = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
        Assert.Equal(LibraryMutationOwner.ManualFetch, viewModel.CurrentMutationLease?.Owner);
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupLifecycle_TransitionsOnlyAtLoadPublishScanApplySaveBoundaries() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();

        Assert.Throws<InvalidOperationException>(() =>
            viewModel.SetStartupState(StartupLibraryState.Scanning));
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        Assert.Throws<InvalidOperationException>(() =>
            viewModel.SetStartupState(StartupLibraryState.SavingScan));
        viewModel.SetStartupState(StartupLibraryState.Scanning);
        viewModel.SetStartupState(StartupLibraryState.ApplyingScan);
        viewModel.SetStartupState(StartupLibraryState.SavingScan);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        return Task.CompletedTask;
    });

    [Fact]
    public Task InitializeAsync_HundredEntries_FirstFortyEndsVideoSkeleton() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(100));
        using var viewModel = fixture.CreateViewModel();
        Task? initialization = null;
        var observedFirstBatch = false;
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 40)
            {
                observedFirstBatch = true;
                Assert.False(viewModel.ShowVideoSkeleton);
                Assert.True(viewModel.ShowActorSkeleton);
                Assert.NotNull(initialization);
                Assert.False(initialization!.IsCompleted);
            }
        };

        initialization = viewModel.InitializeAsync();
        await initialization;

        Assert.True(observedFirstBatch);
        Assert.Equal(100, viewModel.Videos.Count);
        Assert.False(viewModel.ShowActorSkeleton);
    });

    [Fact]
    public Task InitializeAsync_FirstBatchKeepsActorSkeletonUntilFinalRefresh() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(100));
        using var viewModel = fixture.CreateViewModel();
        var actorSkeletonAtFirstBatch = false;
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 40)
            {
                actorSkeletonAtFirstBatch = viewModel.ShowActorSkeleton;
            }
        };

        await viewModel.InitializeAsync();

        Assert.True(actorSkeletonAtFirstBatch);
        Assert.False(viewModel.IsActorListLoading);
        Assert.True(viewModel.Actors.Count > 1);
    });

    [Fact]
    public Task InitializeAsync_FirstCollectionEventPrecedesInitializationCompletion() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(2));
        using var viewModel = fixture.CreateViewModel();
        Task? initialization = null;
        var incompleteAtFirstEvent = false;
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 1)
            {
                incompleteAtFirstEvent = initialization is { IsCompleted: false };
            }
        };

        initialization = viewModel.InitializeAsync();
        await initialization;

        Assert.True(incompleteAtFirstEvent);
    });

    [Fact]
    public Task InitializeAsync_EachCollectionBatchIsAtMostConfiguredSize() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(100));
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        using var viewModel = fixture.CreateViewModel(probe);

        await viewModel.InitializeAsync();

        var records = probe.GetDispatcherBatchRecords();
        Assert.Equal(new[] { 40, 40, 20 }, records
            .Where(record => record.Kind == "StoredPublish")
            .Select(record => record.ItemCount));
        Assert.Single(records, record => record.Kind == "StoredFinalize");
        _ = probe.EndMeasurementPhase();
    });

    [Fact]
    public Task InitializeAsync_OperationCancelDuringPublishing_RecoversWholePersistedLibraryOrFaults() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var persisted = CreateLibrary(100);
        fixture.Store.LoadOverride = _ => Task.FromResult(persisted);
        using var viewModel = fixture.CreateViewModel();
        using var cancellation = new CancellationTokenSource();
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 40)
            {
                cancellation.Cancel();
            }
        };

        await viewModel.InitializeAsync(cancellation.Token);

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(100, viewModel.Videos.Count);
        Assert.Equal(2, fixture.Store.LoadCount);
        Assert.Equal(0, fixture.Scanner.ScanCount);
        Assert.Contains("persisted library restored", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task InitializeAsync_EmptyLibrary_KeepsSkeletonUntilScanFirstBatch() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishScan = new TaskCompletionSource<LibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scanner.ScanOverride = (_, _) =>
        {
            scanStarted.TrySetResult();
            return finishScan.Task;
        };
        using var viewModel = fixture.CreateViewModel();

        var initialization = viewModel.InitializeAsync();
        await scanStarted.Task;
        Assert.Equal(StartupLibraryState.Scanning, viewModel.StartupState);
        Assert.True(viewModel.ShowVideoSkeleton);
        Assert.True(viewModel.ShowActorSkeleton);
        finishScan.TrySetResult(EmptyScanResult());
        await initialization;
        Assert.False(viewModel.ShowVideoSkeleton);
        Assert.False(viewModel.ShowActorSkeleton);
    });

    [Fact]
    public Task InitializeAsync_StoredPublishingFailure_RecoversThenReadyWithoutScan() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(5));
        var failed = false;
        using var viewModel = fixture.CreateViewModel(
            storedMapper: (entry, _) =>
            {
                if (!failed)
                {
                    failed = true;
                    throw new InvalidOperationException("mapping failed");
                }
                return MapEntry(entry);
            });

        await viewModel.InitializeAsync();

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(5, viewModel.Videos.Count);
        Assert.Equal(0, fixture.Scanner.ScanCount);
        Assert.Equal(2, fixture.Store.LoadCount);
    });

    [Fact]
    public Task InitializeAsync_StoredPublishingRecoveryFailure_EndsSkeletonAndFaults() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => fixture.Store.LoadCount == 1
            ? Task.FromResult(CreateLibrary(5))
            : Task.FromException<LibraryData>(new IOException("recovery failed"));
        using var viewModel = fixture.CreateViewModel(
            storedMapper: (_, _) => throw new InvalidOperationException("mapping failed"));

        await viewModel.InitializeAsync();

        Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
        Assert.False(viewModel.ShowVideoSkeleton);
        Assert.False(viewModel.ShowActorSkeleton);
        Assert.False(viewModel.CanMutateLibrary);
        Assert.Equal(0, fixture.Scanner.ScanCount);
    });

    [Fact]
    public Task InitializeAsync_LoadAndDefaultResetFailure_FaultsWithoutScanOrAutoMetadata() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromException<LibraryData>(
            new InvalidDataException("reset failed"));
        using var viewModel = fixture.CreateViewModel();

        await Assert.ThrowsAsync<InvalidDataException>(() => viewModel.InitializeAsync());

        Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
        Assert.Equal(0, fixture.Scanner.ScanCount);
        Assert.False(viewModel.IsFetchingMetadata);
    });

    [Fact]
    public Task InitializeAsync_NonResettableLoadFailure_TransitionsFaultedWithoutScan() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromException<LibraryData>(
            new UnauthorizedAccessException("denied"));
        using var viewModel = fixture.CreateViewModel();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => viewModel.InitializeAsync());

        Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
        Assert.Equal(0, fixture.Scanner.ScanCount);
        Assert.Equal(0, fixture.Store.SaveCount);
    });

    [Fact]
    public Task InitializeAsync_LifetimeCancellation_DoesNotStartRecoveryOrScan() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = async token =>
        {
            loadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new LibraryData();
        };
        var viewModel = fixture.CreateViewModel();
        var initialization = viewModel.InitializeAsync();
        await loadStarted.Task;

        viewModel.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Equal(0, fixture.Scanner.ScanCount);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(StartupLibraryState.Loading, viewModel.StartupState);
    });

    [Fact]
    public Task InitializeAsync_LifetimeEndsDuringBatchMapping_DoesNotScheduleOrRunPublishCallback() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(1));
        var mappingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMapping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        using var viewModel = fixture.CreateViewModel(
            probe,
            storedMapper: (entry, token) =>
            {
                mappingStarted.TrySetResult();
                releaseMapping.Task.Wait(token);
                return MapEntry(entry);
            });

        var initialization = viewModel.InitializeAsync();
        await mappingStarted.Task;
        viewModel.Dispose();
        releaseMapping.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Empty(viewModel.Videos);
        Assert.Equal(0, fixture.Scanner.ScanCount);
        Assert.DoesNotContain(probe.GetDispatcherBatchRecords(), record => record.Kind == "StoredPublish");
        _ = probe.EndMeasurementPhase();
    });

    [Fact]
    public Task StoredFinalize_RunsOnDispatcherAndIsMeasuredExactlyOnce() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(5));
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        using var viewModel = fixture.CreateViewModel(probe);

        await viewModel.InitializeAsync();

        var finalize = Assert.Single(
            probe.GetDispatcherBatchRecords(),
            record => record.Kind == "StoredFinalize");
        Assert.Equal(dispatcherThread, finalize.ThreadId);
        Assert.True(finalize.ElapsedTicks >= 0);
        Assert.True(finalize.ElapsedTicks <= Stopwatch.Frequency / 10);
        _ = probe.EndMeasurementPhase();
    });

    [Fact]
    public Task InitializeAsync_StoredMappingRunsOffDispatcher() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(45));
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var mappingThreads = new ConcurrentBag<int>();
        using var viewModel = fixture.CreateViewModel(
            storedMapper: (entry, _) =>
            {
                mappingThreads.Add(Environment.CurrentManagedThreadId);
                return MapEntry(entry);
            });

        await viewModel.InitializeAsync();

        Assert.NotEmpty(mappingThreads);
        Assert.DoesNotContain(dispatcherThread, mappingThreads);
    });

    [Fact]
    public Task StartupScan_FirstNewBatchEndsVideoSkeleton() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(1));
        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.SaveOverride = async (_, token) =>
        {
            saveStarted.TrySetResult();
            await finishSave.Task.WaitAsync(token);
        };
        using var viewModel = fixture.CreateViewModel();

        var initialization = viewModel.InitializeAsync();
        await saveStarted.Task;
        Assert.Equal(StartupLibraryState.SavingScan, viewModel.StartupState);
        Assert.Single(viewModel.Videos);
        Assert.False(viewModel.ShowVideoSkeleton);
        finishSave.TrySetResult();
        await initialization;
    });

    [Fact]
    public Task StartupScan_ZeroResults_KeepsSkeletonsUntilSaveCompletion() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(0));
        var saveStarted = new TaskCompletionSource<LibraryData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.SaveOverride = async (library, token) =>
        {
            saveStarted.TrySetResult(library);
            await finishSave.Task.WaitAsync(token);
        };
        using var viewModel = fixture.CreateViewModel();

        var initialization = viewModel.InitializeAsync();
        var savingLibrary = await saveStarted.Task;
        Assert.Equal(StartupLibraryState.SavingScan, viewModel.StartupState);
        Assert.True(viewModel.ShowVideoSkeleton);
        Assert.True(viewModel.ShowActorSkeleton);
        Assert.NotNull(savingLibrary.Targets.Single().LastScanUtc);
        finishSave.TrySetResult();
        await initialization;
        Assert.False(viewModel.ShowVideoSkeleton);
        Assert.False(viewModel.ShowActorSkeleton);
    });

    [Fact]
    public Task StartupScan_HundredNewItemsPublishesMultipleDispatcherBatches() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(100));
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        using var viewModel = fixture.CreateViewModel(probe);

        await viewModel.InitializeAsync();

        Assert.Equal(100, viewModel.Videos.Count);
        Assert.Equal(new[] { 40, 40, 20 }, probe.GetDispatcherBatchRecords()
            .Where(record => record.Kind == "ScanApply")
            .Select(record => record.ItemCount));
        Assert.Single(probe.GetDispatcherBatchRecords(), record => record.Kind == "ScanCommitStart");
        Assert.Single(probe.GetDispatcherBatchRecords(), record => record.Kind == "ScanFinalize");
        _ = probe.EndMeasurementPhase();
    });

    [Fact]
    public Task StartupScan_EnumerationApplyingAndSavingBlockManualAndEditorMutation() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) =>
        {
            Assert.Equal(StartupLibraryState.Scanning, fixture.ViewModel!.StartupState);
            Assert.False(fixture.ViewModel.CanMutateLibrary);
            return Task.FromResult(CreateNewFileScanResult(1));
        };
        fixture.Store.SaveOverride = (_, _) =>
        {
            Assert.Equal(StartupLibraryState.SavingScan, fixture.ViewModel!.StartupState);
            Assert.Equal(LibraryMutationOwner.StartupScan, fixture.ViewModel.CurrentMutationLease?.Owner);
            Assert.Null(fixture.ViewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));
            Assert.Null(fixture.ViewModel.TryBeginMetadataEditorMutation());
            return Task.CompletedTask;
        };
        using var viewModel = fixture.CreateViewModel();
        fixture.ViewModel = viewModel;

        await viewModel.InitializeAsync();

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
    });

    [Fact]
    public Task ScanPlan_CancelBeforeFirstBatchChangesNothing() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var persisted = CreateLibrary(2);
        fixture.Store.LoadOverride = _ => Task.FromResult(persisted);
        using var cancellation = new CancellationTokenSource();
        fixture.Scanner.ScanOverride = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(CreateScanResultForLibrary(persisted));
        };
        using var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync(cancellation.Token);

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(2, viewModel.Videos.Count);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Contains("unchanged", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task ScanPlan_CancelAfterFirstBatchFinishesPlanAndAtomicSave() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(100));
        using var cancellation = new CancellationTokenSource();
        using var viewModel = fixture.CreateViewModel();
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 1)
            {
                cancellation.Cancel();
            }
        };

        await viewModel.InitializeAsync(cancellation.Token);

        Assert.Equal(100, viewModel.Videos.Count);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
    });

    [Fact]
    public Task ScanPlan_MiddleBatchFailureSkipsMetadataAndRecoversPersistedState() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var persisted = CreateLibrary(2);
        fixture.Store.LoadOverride = _ => Task.FromResult(persisted);
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(100));
        using var viewModel = fixture.CreateViewModel();
        var failed = false;
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (!failed && viewModel.Videos.Count == 50)
            {
                failed = true;
                throw new InvalidOperationException("apply failed");
            }
        };

        await viewModel.InitializeAsync();

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(2, viewModel.Videos.Count);
        Assert.Equal(2, fixture.Store.LoadCount);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.False(viewModel.IsFetchingMetadata);
    });

    [Fact]
    public Task ScanPlan_RecoveryFailureFaultsAndKeepsMutationDisabled() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var persisted = CreateLibrary(1);
        fixture.Store.LoadOverride = _ => fixture.Store.LoadCount == 1
            ? Task.FromResult(persisted)
            : Task.FromException<LibraryData>(new IOException("recovery failed"));
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(2));
        using var viewModel = fixture.CreateViewModel();
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 2)
            {
                throw new InvalidOperationException("apply failed");
            }
        };

        await viewModel.InitializeAsync();

        Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
        Assert.False(viewModel.CanMutateLibrary);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
    });

    [Fact]
    public Task ScanPlan_LifetimeCancelStopsSchedulingWithoutRecoveryOrReady() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(100));
        var viewModel = fixture.CreateViewModel();
        viewModel.Videos.CollectionChanged += (_, _) =>
        {
            if (viewModel.Videos.Count == 1)
            {
                viewModel.Dispose();
            }
        };

        await viewModel.InitializeAsync();

        Assert.NotEqual(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(0, fixture.Store.SaveCount);
        Assert.Equal(1, fixture.Store.LoadCount);
    });

    [Fact]
    public Task StartupScan_PendingAutoQueueWinsLeaseBeforeManualFetch() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(1));
        using var viewModel = fixture.CreateViewModel();
        LibraryMutationOwner? ownerAtReady = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StartupState) &&
                viewModel.StartupState == StartupLibraryState.Ready)
            {
                ownerAtReady = viewModel.CurrentMutationLease?.Owner;
                Assert.Null(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));
            }
        };

        await viewModel.InitializeAsync();

        Assert.Equal(LibraryMutationOwner.AutoMetadata, ownerAtReady);
    });

    [Fact]
    public Task PrepareScanApplyPlan_DeepCloneRunsOffDispatcherThread() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var lease = Assert.IsType<LibraryMutationLease>(
            viewModel.TryBeginLibraryMutation(LibraryMutationOwner.StartupScan));
        var seed = viewModel.CaptureScanPreparationSeed(lease);
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var preparationThread = 0;

        var plan = await Task.Run(() =>
        {
            preparationThread = Environment.CurrentManagedThreadId;
            return viewModel.PrepareScanApplyPlan(EmptyScanResult(), seed, CancellationToken.None);
        });

        Assert.NotEqual(dispatcherThread, preparationThread);
        Assert.NotSame(seed.TargetReferences, plan.FinalLibrary.Targets);
        viewModel.ReleaseLibraryMutation(lease);
    });

    [Fact]
    public Task ScanApplyPlan_CollectionsCannotBeMutatedAfterConstruction() => WpfTestHost.RunAsync(() =>
    {
        var operations = new List<ScanApplyOperation>();
        var metadata = new List<string> { "one" };
        var actors = new List<string> { "actor" };
        var plan = new ScanApplyPlan(
            new LibraryData(),
            operations,
            metadata,
            actors,
            DateTime.UtcNow,
            0,
            0,
            0);
        metadata[0] = "changed";
        actors[0] = "changed";

        Assert.Equal("one", plan.MetadataPaths[0]);
        Assert.Equal("actor", plan.ActorSnapshot[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)plan.MetadataPaths).Add("two"));
        return Task.CompletedTask;
    });

    [Fact]
    public Task ScanSnapshotCapture_StressFixture_CompletesWithinHardGate() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Store.LoadOverride = _ => Task.FromResult(CreateLibrary(1000));
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishScan = new TaskCompletionSource<LibraryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scanner.ScanOverride = (_, _) =>
        {
            scanStarted.TrySetResult();
            return finishScan.Task;
        };
        using var viewModel = fixture.CreateViewModel();
        var initialization = viewModel.InitializeAsync();
        await scanStarted.Task;
        var lease = Assert.IsType<LibraryMutationLease>(viewModel.CurrentMutationLease);
        var stopwatch = Stopwatch.StartNew();
        _ = viewModel.CaptureScanPreparationSeed(lease);
        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed <= TimeSpan.FromMilliseconds(100));
        finishScan.TrySetResult(CreateScanResultForLibrary(CreateLibrary(1000)));
        await initialization;
    });

    [Fact]
    public Task StartupScan_SuccessFailureAndOperationCancelReturnReadyWithOriginalStatus() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var persisted = CreateLibrary(2);
        fixture.Store.LoadOverride = _ => Task.FromResult(persisted);
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateScanResultForLibrary(persisted));
        fixture.Store.SaveOverride = (_, _) => Task.FromException(new IOException("save failed"));
        using var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync();

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(2, fixture.Store.LoadCount);
        Assert.Equal(2, viewModel.Videos.Count);
        Assert.Contains("save failed", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public Task ScanFinalize_RefreshesActorsFilterSelectionBeforeTerminal() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var persisted = CreateLibrary(3);
        fixture.Store.LoadOverride = _ => Task.FromResult(persisted);
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateScanResultForLibrary(persisted));
        using var viewModel = fixture.CreateViewModel();
        var finalizedBeforeReady = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StartupState) &&
                viewModel.StartupState == StartupLibraryState.SavingScan)
            {
                finalizedBeforeReady =
                    viewModel.Actors.Contains("Actor 0") &&
                    viewModel.SelectedVideo is not null &&
                    !viewModel.IsActorListLoading;
            }
        };

        await viewModel.InitializeAsync();

        Assert.True(finalizedBeforeReady);
        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
    });

    [Fact]
    public Task CancelBeforeCommit_OriginalDomainGraphRemainsByteForByteEquivalent() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var original = CreateLibrary(3);
        var before = JsonSerializer.SerializeToUtf8Bytes(original);
        fixture.Store.LoadOverride = _ => Task.FromResult(original);
        using var cancellation = new CancellationTokenSource();
        fixture.Scanner.ScanOverride = (_, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(CreateScanResultForLibrary(original));
        };
        using var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync(cancellation.Token);

        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(original));
        Assert.Equal(0, fixture.Store.SaveCount);
    });

    [Fact]
    public Task MutationOwnerChange_RaisesCanExecuteAndProjectionNotifications() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        var changes = new List<string>();
        var canExecuteChanges = 0;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changes.Add(args.PropertyName);
            }
        };
        viewModel.FetchMetadataCommand.CanExecuteChanged += (_, _) => canExecuteChanges++;

        var lease = Assert.IsType<LibraryMutationLease>(
            viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));
        viewModel.ReleaseLibraryMutation(lease);

        Assert.True(changes.Count(name => name == nameof(MainViewModel.CanMutateLibrary)) >= 2);
        Assert.True(changes.Count(name => name == nameof(MainViewModel.IsFetchingMetadata)) >= 2);
        Assert.True(canExecuteChanges >= 2);
        return Task.CompletedTask;
    });

    [Fact]
    public Task EditorLease_SaveCancelAndException_ReleaseSameIdentity() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        Guid? identityAtSave = null;
        fixture.Store.SaveOverride = (_, _) =>
        {
            Assert.Equal(LibraryMutationOwner.EditorSave, viewModel.CurrentMutationLease?.Owner);
            identityAtSave = viewModel.CurrentMutationLease?.Identity;
            return Task.FromException(new IOException("save failed"));
        };
        var item = new VideoItem
        {
            LibraryPath = "./Videos/editor.mp4",
            Title = "Before",
            Actors = Array.Empty<string>(),
            Tags = Array.Empty<string>()
        };

        await Assert.ThrowsAsync<IOException>(() => viewModel.ApplyMetadataEditAsync(
            item,
            new MetadataEditResult(
                "After",
                null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                string.Empty,
                "resources/noimage.jpg")));

        Assert.NotNull(identityAtSave);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.True(viewModel.CanMutateLibrary);
    });

    [Fact]
    public Task AutoQueue_AfterScanSave_PreemptsManualFetch() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromResult(CreateNewFileScanResult(1));
        using var viewModel = fixture.CreateViewModel();
        LibraryMutationOwner? readyOwner = null;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StartupState) &&
                viewModel.StartupState == StartupLibraryState.Ready)
            {
                readyOwner = viewModel.CurrentMutationLease?.Owner;
            }
        };

        await viewModel.InitializeAsync();

        Assert.Equal(LibraryMutationOwner.AutoMetadata, readyOwner);
    });

    [Fact]
    public Task ScanFailureStatus_IsNotOverwrittenDuringReadyCleanup() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Scanner.ScanOverride = (_, _) => Task.FromException<LibraryScanResult>(
            new IOException("scan unavailable"));
        using var viewModel = fixture.CreateViewModel();

        await viewModel.InitializeAsync();
        var status = viewModel.StatusMessage;
        await Task.Delay(25);

        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(status, viewModel.StatusMessage);
        Assert.Contains("scan unavailable", status, StringComparison.OrdinalIgnoreCase);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AiriStartupTests", Guid.NewGuid().ToString("N"));

        public FakeLibraryStore Store { get; }
        public FakeLibraryScanner Scanner { get; } = new();
        public MainViewModel? ViewModel { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Store = new FakeLibraryStore(Path.Combine(_root, "videos.json"));
        }

        public MainViewModel CreateViewModel(
            ThumbnailPerformanceProbe? probe = null,
            Func<VideoEntry, CancellationToken, VideoItem>? storedMapper = null)
        {
            var provider = new CrawlerSessionProvider();
            var source = new OneFourOneJavMetaSource(provider);
            return new MainViewModel(
                Store,
                Scanner,
                new WebMetadataService(
                    Array.Empty<IWebVideoMetaSource>(),
                    new ThumbnailCache(_root),
                    NullTranslationService.Instance,
                    "KO"),
                provider,
                source,
                new NeverCrawlerSessionFactory(),
                probe ?? ThumbnailPerformanceProbe.Disabled,
                new TestThumbnailImageLoader(),
                storedMapper);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    internal sealed class FakeLibraryStore(string filePath) : ILibraryStore
    {
        public string FilePath { get; } = filePath;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public Func<CancellationToken, Task<LibraryData>>? LoadOverride { get; set; }
        public Func<LibraryData, CancellationToken, Task>? SaveOverride { get; set; }

        public Task<LibraryData> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return LoadOverride?.Invoke(cancellationToken) ?? Task.FromResult(new LibraryData());
        }

        public Task SaveAsync(LibraryData library, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return SaveOverride?.Invoke(library, cancellationToken) ?? Task.CompletedTask;
        }
    }

    internal sealed class FakeLibraryScanner : ILibraryScanner
    {
        public int ScanCount { get; private set; }
        public Func<LibraryData, CancellationToken, Task<LibraryScanResult>>? ScanOverride { get; set; }

        public Task<LibraryScanResult> ScanAsync(LibraryData library, CancellationToken cancellationToken)
        {
            ScanCount++;
            return ScanOverride?.Invoke(library, cancellationToken) ?? Task.FromResult(new LibraryScanResult(
                Array.Empty<FileSnapshot>(),
                Array.Empty<FileSnapshot>(),
                Array.Empty<VideoEntry>(),
                Array.Empty<UpdatedFile>()));
        }
    }

    private sealed class NeverCrawlerSessionFactory : IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Crawler is not used by startup tests.");
    }

    private static LibraryData CreateLibrary(int count) => new()
    {
        Targets = new List<TargetFolder>
        {
            new("./Videos", new[] { "*.mp4" }, Array.Empty<string>(), null)
        },
        Videos = Enumerable.Range(0, count)
            .Select(index => new VideoEntry(
                $"./Videos/video-{index:D4}.mp4",
                new VideoMeta(
                    $"Video {index:D4}",
                    null,
                    new[] { $"Actor {index % 7}" },
                    "resources/noimage.jpg",
                    Array.Empty<string>(),
                    string.Empty),
                index,
                DateTime.SpecifyKind(new DateTime(2026, 1, 1).AddMinutes(index), DateTimeKind.Utc),
                DateTime.SpecifyKind(new DateTime(2026, 1, 1).AddMinutes(index), DateTimeKind.Utc)))
            .ToList()
    };

    private static VideoItem MapEntry(VideoEntry entry) => new()
    {
        LibraryPath = entry.Path,
        Title = entry.Meta.Title,
        Actors = entry.Meta.Actors,
        Tags = entry.Meta.Tags,
        Description = entry.Meta.Description,
        ThumbnailPath = entry.Meta.Thumbnail,
        ThumbnailUri = entry.Meta.Thumbnail
    };

    private static LibraryScanResult EmptyScanResult() => new(
        Array.Empty<FileSnapshot>(),
        Array.Empty<FileSnapshot>(),
        Array.Empty<VideoEntry>(),
        Array.Empty<UpdatedFile>());

    private static LibraryScanResult CreateNewFileScanResult(int count)
    {
        var snapshots = Enumerable.Range(0, count)
            .Select(index => new FileSnapshot(
                $"./Videos/new-{index:D4}.mp4",
                Path.Combine(Path.GetTempPath(), $"new-{index:D4}.mp4"),
                1000 + index,
                DateTime.SpecifyKind(new DateTime(2026, 2, 1).AddMinutes(index), DateTimeKind.Utc),
                DateTime.SpecifyKind(new DateTime(2026, 2, 1).AddMinutes(index), DateTimeKind.Utc)))
            .ToArray();
        return new LibraryScanResult(
            snapshots,
            snapshots,
            Array.Empty<VideoEntry>(),
            Array.Empty<UpdatedFile>());
    }

    private static LibraryScanResult CreateScanResultForLibrary(LibraryData library)
    {
        var snapshots = library.Videos
            .Select(entry => new FileSnapshot(
                entry.Path,
                LibraryPathHelper.ResolveToAbsolute(entry.Path),
                entry.SizeBytes,
                entry.LastModifiedUtc,
                entry.CreatedUtc))
            .ToArray();
        return new LibraryScanResult(
            snapshots,
            Array.Empty<FileSnapshot>(),
            Array.Empty<VideoEntry>(),
            Array.Empty<UpdatedFile>());
    }
}
