using System.IO;
using System.Text.Json;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VideoDeletionTests
{
    [Fact]
    public Task DeleteCommand_MixedSelectionRecyclesOnlyExistingFilesAndRemovesBothEntries() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var existing = vm.Videos.Single(item => item.Title == "Title 0");
        var missing = vm.Videos.Single(item => item.Title == "Title 1");
        var untouched = vm.Videos.Single(item => item.Title == "Title 2");
        File.Delete(missing.SourcePath);
        missing.Presence = VideoPresenceState.Missing;
        existing.IsChecked = true;
        missing.IsChecked = true;

        var confirmations = new List<GalleryOperationConfirmation>();
        vm.ConfirmOperationAsync = confirmation =>
        {
            confirmations.Add(confirmation);
            return Task.FromResult(true);
        };
        f.Recycler.Override = async path =>
        {
            var pending = await f.Store.LoadAsync();
            Assert.Contains(pending.Videos, entry => entry.Id == existing.Id);
            Assert.Equal(LibraryOperationState.Pending, pending.LatestOperation(existing.Id)?.State);
            File.Move(path, path + ".test-recycled");
            return new VideoRecycleResult(VideoRecycleOutcome.Succeeded) { Receipt = "fake:" + path, MovementConfirmed = true };
        };

        vm.DeleteCommand.Execute(null);
        await vm.GalleryActionTask;
        await VideoDeletionFixture.WaitForQueryAsync(vm);

        var confirmed = Assert.Single(confirmations);
        Assert.Equal(LibraryOperationIntent.Delete, confirmed.Intent);
        Assert.Equal(new[] { existing.Title, missing.Title }, confirmed.Titles);
        Assert.Equal(existing.SourcePath, Assert.Single(f.Recycler.Paths));
        Assert.False(File.Exists(existing.SourcePath));
        Assert.True(File.Exists(existing.SourcePath + ".test-recycled"));
        Assert.True(File.Exists(untouched.SourcePath));
        Assert.Same(untouched, Assert.Single(vm.Videos));
        Assert.Same(untouched, Assert.Single(vm.FilteredVideos.Cast<VideoItem>()));
        var saved = await f.Store.LoadAsync();
        Assert.Equal(untouched.Id, Assert.Single(saved.Videos).Id);
        Assert.Empty(saved.Journal);
        Assert.False(Assert.Single(vm.ChangeHistory).CanUndo);
    });

    [Fact]
    public Task DeleteInspectedCommand_MissingFileKeepsEntryOnSaveFailureAndCanRetry() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        File.Delete(item.SourcePath);
        item.Presence = VideoPresenceState.Missing;
        vm.InspectCommand.Execute(item);
        vm.ConfirmOperationAsync = _ => Task.FromResult(true);

        f.FailSave = true;
        vm.DeleteInspectedCommand.Execute(null);
        await vm.GalleryActionTask;
        Assert.Contains(item, vm.Videos);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Contains("실패", vm.StatusMessage);

        f.FailSave = false;
        vm.DeleteInspectedCommand.Execute(null);
        await vm.GalleryActionTask;
        Assert.DoesNotContain(item, vm.Videos);
        Assert.DoesNotContain((await f.Store.LoadAsync()).Videos, entry => entry.Id == item.Id);
        Assert.Empty(f.Recycler.Paths);
    });

    [Fact]
    public Task DeleteCommand_SharedFileIsRecycledOnceAndBothSelectedEntriesAreRemoved() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(duplicatePath: true);
        var sharedPath = vm.SelectedVideo!.SourcePath;
        foreach (var item in vm.Videos.Where(item => item.SourcePath == sharedPath)) item.IsChecked = true;
        vm.ConfirmOperationAsync = _ => Task.FromResult(true);

        vm.DeleteCommand.Execute(null);
        await vm.GalleryActionTask;

        Assert.Equal(sharedPath, Assert.Single(f.Recycler.Paths));
        Assert.Equal("Title 2", Assert.Single(vm.Videos).Title);
        var saved = await f.Store.LoadAsync();
        Assert.Equal("Title 2", Assert.Single(saved.Videos).Meta.Title);
        Assert.Empty(saved.Journal);
    });

    [Fact]
    public Task CancelMissingAndBusy_DoNotRecycleOrSave() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(false), _ => throw new Exception("stop"));
        Assert.Same(item, vm.SelectedVideo);
        File.Delete(item.SourcePath);
        item.Presence = VideoPresenceState.Missing;
        var confirmed = false;
        await vm.DeleteSelectedVideoAsync(_ => { confirmed = true; return Task.FromResult(false); }, _ => Task.CompletedTask);
        Assert.True(confirmed);
        var lease = vm.TryBeginLibraryMutation(LibraryMutationOwner.EditorSave)!;
        await vm.DeleteSelectedVideoAsync(_ => throw new Exception("confirm"), _ => Task.CompletedTask);
        vm.ReleaseLibraryMutation(lease);
        Assert.Empty(f.Recycler.Paths);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.True(vm.CanMutateLibrary);
    });

    [Theory]
    [InlineData(VideoRecycleOutcome.Cancelled)]
    [InlineData(VideoRecycleOutcome.Failed)]
    [InlineData(VideoRecycleOutcome.Unknown)]
    public Task RecycleNotSuccessful_PreservesCanonicalMetadataAndDurableUncertainJournal(VideoRecycleOutcome outcome) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        f.Recycler.Result = new(outcome, "injected result");
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.Contains(item, vm.Videos);
        var persisted = await f.Store.LoadAsync();
        Assert.Equal(3, persisted.Videos.Count);
        Assert.Equal("preserved description", persisted.Videos.Single(entry => entry.Id == item.Id).Meta.Description);
        Assert.Equal(LibraryOperationState.Uncertain, Assert.Single(persisted.Journal).State);
        Assert.Equal(outcome.ToString(), persisted.Journal[0].Outcome);
        Assert.False(persisted.Journal[0].RestoreAvailable);
        Assert.True(vm.CanMutateLibrary);
    });

    [Fact]
    public Task CapturesTarget_WaitsForPreview_RejectsReentry_ThenPersistsCandidate() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var target = vm.SelectedVideo!;
        var visible = vm.FilteredVideos.Cast<VideoItem>().ToList();
        var index = visible.IndexOf(target);
        var next = visible[index + 1];
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = vm.DeleteSelectedVideoAsync(captured =>
        {
            Assert.Same(target, captured.Item);
            Assert.Equal(Path.GetFileName(target.SourcePath), captured.FileName);
            Assert.False(vm.CanMutateLibrary);
            vm.SelectedVideo = next;
            return Task.FromResult(true);
        }, item =>
        {
            Assert.Same(target, item);
            stopped.SetResult();
            return stop.Task;
        });
        await stopped.Task;
        Assert.Same(task, vm.VideoDeletionTask);
        Assert.True(vm.IsVideoDeletionTarget(target));
        await vm.DeleteSelectedVideoAsync(_ => throw new Exception("reentry"), _ => Task.CompletedTask);
        Assert.Empty(f.Recycler.Paths);
        stop.SetResult();
        await task;
        Assert.Equal(new[] { target.SourcePath }, f.Recycler.Paths);
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        Assert.DoesNotContain(target, vm.Videos);
        Assert.DoesNotContain(target, vm.FilteredVideos.Cast<VideoItem>());
        Assert.Same(next, vm.SelectedVideo);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(f.Store.FilePath));
        var entries = json.RootElement.GetProperty("Videos").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.GetProperty("Id").GetGuid() == target.Id);
        Assert.Empty(json.RootElement.GetProperty("Journal").EnumerateArray());
        Assert.All(entries, entry => Assert.Equal("preserved description", entry.GetProperty("Meta").GetProperty("Description").GetString()));
        Assert.True(File.Exists(f.SidecarPath));
        Assert.True(File.Exists(f.ThumbnailPath));
        var reloaded = await f.Store.LoadAsync();
        Assert.Equal(2, reloaded.Videos.Count);
        Assert.True(vm.CanMutateLibrary);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChangedPathOrDisappearedFile_StopsBeforeRecycle(bool disappear) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        await vm.DeleteSelectedVideoAsync(_ =>
        {
            if (disappear) File.Move(item.SourcePath, item.SourcePath + ".gone");
            else item.UpdateFileState(vm.Videos.First(v => !ReferenceEquals(v, item)).SourcePath,
                item.SizeBytes, item.LastModifiedUtc, item.Presence, item.CreatedUtc);
            return Task.FromResult(true);
        }, _ => Task.CompletedTask);
        Assert.Empty(f.Recycler.Paths);
        Assert.Contains(item, vm.Videos);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Contains("실패", vm.StatusMessage);
    });

    [Fact]
    public Task PendingSaveFailure_ProtectsFileAndMetadataAndMakesZeroOsCalls() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var probeThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var vm = await f.CreateAsync(path =>
        { probeThreads.Add(Environment.CurrentManagedThreadId); return File.Exists(path) && !Directory.Exists(path); });
        var item = vm.SelectedVideo!;
        f.FailSave = true;
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.True(File.Exists(item.SourcePath)); Assert.Empty(f.Recycler.Paths);
        Assert.Contains(item, vm.Videos); Assert.Equal("preserved description", item.Description);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.True(vm.CanMutateLibrary); Assert.True(probeThreads.Count >= 3);
        Assert.DoesNotContain(dispatcherThread, probeThreads);
    });

    [Fact]
    public Task FinalSaveFailure_RetainsDurablePendingAndBlocksDuplicateOsCall() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!; f.FailFinalSave = true;
        f.Recycler.Result = f.Recycler.Result with { Diagnostics = "session diagnostics" };
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.False(File.Exists(item.SourcePath)); Assert.Single(f.Recycler.Paths);
        var persisted = await f.Store.LoadAsync();
        var pending = Assert.Single(persisted.Journal);
        Assert.Equal(LibraryOperationIntent.Delete, pending.Intent);
        Assert.Equal(LibraryOperationState.Pending, pending.State); Assert.Null(pending.ReceiptJson); Assert.Null(pending.NativeDiagnostics);
        Assert.Equal(3, persisted.Videos.Count); Assert.Empty(vm.ChangeHistory);
        Assert.Contains("저장 실패", vm.StatusMessage);
        vm.SelectedVideo = item;
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.Single(f.Recycler.Paths);
        f.FailFinalSave = false;
        vm.CurrentGalleryView = GalleryView.Trash; await VideoDeletionFixture.WaitForQueryAsync(vm);
        vm.SelectedVideo = item; vm.ReinspectCommand.Execute(null); await vm.GalleryActionTask;
        var recovered = await f.Store.LoadAsync();
        Assert.DoesNotContain(recovered.Videos, entry => entry.Id == item.Id);
        Assert.Empty(recovered.Journal);
        Assert.DoesNotContain(item, vm.Videos);
        Assert.False(Assert.Single(vm.ChangeHistory).CanUndo);
        Assert.Single(f.Recycler.Paths);
    });

    [Fact]
    public Task FileProbe_DoesNotBlockDispatcher_AndRechecksIdentityAfterReturning() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var releaseProbe = new ManualResetEventSlim();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var vm = await f.CreateAsync(path =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                probeStarted.TrySetResult();
                if (!releaseProbe.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Probe was not released by dispatcher.");
            }
            return File.Exists(path) && !Directory.Exists(path);
        });
        var item = vm.SelectedVideo!;
        var deletion = vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        try
        {
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deletion.IsCompleted);
            var other = vm.Videos.First(v => !ReferenceEquals(v, item));
            item.UpdateFileState(other.SourcePath, item.SizeBytes, item.LastModifiedUtc, item.Presence, item.CreatedUtc);
        }
        finally { releaseProbe.Set(); }
        await deletion;
        Assert.Empty(f.Recycler.Paths);
        Assert.Contains(item, vm.Videos);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Contains("실패", vm.StatusMessage);
        Assert.True(vm.CanMutateLibrary);
    });

    [Fact]
    public Task RemovingActive_UsesFirstRemainingAndPreservesFacetAtZeroResults() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var visible = vm.FilteredVideos.Cast<VideoItem>().ToList();
        vm.SelectedVideo = visible[^1];
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        Assert.Same(visible[0], vm.SelectedVideo);
        var actor = vm.SelectedVideo!.Actors[0]; vm.SelectedActor = actor;
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        Assert.Equal(actor, vm.SelectedActor); Assert.Null(vm.SelectedVideo);
        Assert.Empty(vm.FilteredVideos.Cast<VideoItem>());
        vm.ClearFiltersCommand.Execute(null); await VideoDeletionFixture.WaitForQueryAsync(vm);
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        Assert.Null(vm.SelectedVideo); Assert.Equal(0, vm.NormalCount); Assert.Equal(0, vm.TrashCount);
        Assert.Empty(vm.Videos);
    });

    [Fact]
    public Task LegacyTrash_RestoreAndSessionUndoPreserveMetadataAndSidecars() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(legacyTrashCount: 1);
        vm.ConfirmOperationAsync = _ => Task.FromResult(true);
        vm.CurrentGalleryView = GalleryView.Trash;
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        var item = Assert.Single(vm.FilteredVideos.Cast<VideoItem>());
        var bytes = await File.ReadAllBytesAsync(item.SourcePath + ".test-recycled");
        vm.SelectedVideo = item;
        vm.RestoreCommand.Execute(null); await vm.GalleryActionTask;
        Assert.Single(vm.ChangeHistory); Assert.True(vm.ChangeHistory[0].CanUndo);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(item.SourcePath));
        Assert.Equal(LibraryOperationState.Restored, (await f.Store.LoadAsync()).LatestOperation(item.Id)!.State);
        vm.UndoCommand.Execute(null); await vm.GalleryActionTask; await VideoDeletionFixture.WaitForQueryAsync(vm);
        Assert.False(File.Exists(item.SourcePath));
        Assert.Equal(LibraryOperationState.Trashed, (await f.Store.LoadAsync()).LatestOperation(item.Id)!.State);
        Assert.True(File.Exists(f.SidecarPath)); Assert.True(File.Exists(f.ThumbnailPath));
        Assert.Equal("preserved description", item.Description); Assert.False(vm.ChangeHistory[0].CanUndo);
    });

    [Fact]
    public Task AsyncQuery_SameCountDifferentIdsAndDelayedOldWorkerCannotOverwriteLatest() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.GalleryQueryEvaluator = async (snapshot, query, _, token) =>
        {
            if (query.Search == "Title 0") { started.TrySetResult(); await release.Task; }
            return GalleryQueryEngine.Evaluate(snapshot, query, CancellationToken.None);
        };
        vm.SearchQuery = "Title 0"; await started.Task;
        vm.SearchQuery = "Title 1"; await VideoDeletionFixture.WaitForQueryAsync(vm);
        var generation = vm.PublishedQueryGeneration;
        Assert.Equal("Title 1", Assert.Single(vm.FilteredVideos.Cast<VideoItem>()).Title);
        release.TrySetResult(); await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ContextIdle);
        Assert.Equal(generation, vm.PublishedQueryGeneration);
        Assert.Equal("Title 1", Assert.Single(vm.FilteredVideos.Cast<VideoItem>()).Title);
    });

    [Fact]
    public Task SingleEdit_SaveFailureKeepsDraftAndConfirmedValuesThenRetryAndUndoOnlyChangedField() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!; var actors = item.Actors.ToArray();
        await vm.BeginGalleryEditAsync();
        var draft = vm.EditorDraft!; var studio = draft.Fields.Single(field => field.Field == MetadataField.Studio);
        studio.Value = "new studio"; vm.PreviewChangesCommand.Execute(null);
        Assert.Equal(new[] { MetadataField.Studio }, draft.Changes.Select(change => change.Field));
        f.FailSave = true; await vm.ApplyGalleryChangesAsync();
        Assert.Same(draft, vm.EditorDraft); Assert.True(vm.IsChangePreview); Assert.Null(item.Studio);
        Assert.Empty(vm.ChangeHistory); Assert.NotNull(draft.Error);
        vm.SearchQuery = "blocked"; Assert.Equal("", vm.SearchQuery);
        item.IsChecked = true; Assert.False(item.IsChecked);
        f.FailSave = false; await vm.ApplyGalleryChangesAsync();
        Assert.Null(vm.EditorDraft); Assert.Equal("new studio", item.Studio); Assert.Equal(actors, item.Actors);
        vm.UndoCommand.Execute(null); await vm.GalleryActionTask;
        Assert.Null(item.Studio); Assert.Equal(actors, item.Actors);
    });

    [Theory]
    [InlineData(VideoRecycleOutcome.Succeeded, true, false, false, LibraryOperationState.RepairRequired)]
    [InlineData(VideoRecycleOutcome.Unknown, false, true, false, LibraryOperationState.Uncertain)]
    [InlineData(VideoRecycleOutcome.Cancelled, false, false, true, LibraryOperationState.Failed)]
    public Task NativeOutcome_RequiresConfirmedReceiptAndDistinguishesKnownAbsent(VideoRecycleOutcome outcome,
        bool movement, bool receipt, bool absent, LibraryOperationState expected) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        f.Recycler.Result = new(outcome, "injected") { MovementConfirmed = movement, Receipt = receipt ? "fake" : null, SideEffectsKnownAbsent = absent };
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        var stored = await f.Store.LoadAsync();
        Assert.Equal(expected, Assert.Single(stored.Journal).State);
        Assert.Equal(3, stored.Videos.Count); Assert.Empty(vm.ChangeHistory);
        Assert.False(stored.Journal[0].RestoreAvailable);
        Assert.Equal("preserved description", stored.Videos.Single(entry => entry.Id == item.Id).Meta.Description);
    });

    [Fact]
    public Task RestartAfterUnsavedNativeResult_DoesNotInventReceiptOrTreatSourceAbsenceAsSuccess() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var vm = await f.CreateAsync();
        var id = vm.SelectedVideo!.Id; f.FailFinalSave = true;
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        vm.Dispose(); f.FailFinalSave = false;
        using var reopened = await f.ReopenAsync();
        var stored = await f.Store.LoadAsync(); var record = stored.LatestOperation(id)!;
        Assert.Equal(LibraryOperationState.Uncertain, record.State); Assert.Null(record.ReceiptJson);
        Assert.False(record.RestoreAvailable); Assert.Equal(3, stored.Videos.Count);
        Assert.Equal(1, reopened.TrashCount); Assert.Equal(1, reopened.AttentionCount);
        Assert.Equal(2, reopened.NormalCount); Assert.Empty(reopened.ChangeHistory);
        Assert.Single(f.Recycler.Paths);
    });

    [Fact]
    public Task RestartWithReceipt_RestoresExactCanonicalIdentityAndKeepsMetadata() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var vm = await f.CreateAsync(legacyTrashCount: 1);
        var item = vm.Videos.Single(video => video.IsInRecycleBin); var id = item.Id;
        vm.Dispose(); using var reopened = await f.ReopenAsync();
        reopened.CurrentGalleryView = GalleryView.Trash; await VideoDeletionFixture.WaitForQueryAsync(reopened);
        var restoredItem = Assert.Single(reopened.FilteredVideos.Cast<VideoItem>()); Assert.Equal(id, restoredItem.Id);
        reopened.SelectedVideo = restoredItem; reopened.RestoreCommand.Execute(null); await reopened.GalleryActionTask;
        Assert.True(File.Exists(item.SourcePath));
        Assert.Equal(LibraryOperationState.Restored, (await f.Store.LoadAsync()).LatestOperation(id)!.State);
        Assert.Equal("preserved description", restoredItem.Description);
    });

    [Fact]
    public Task EnrichmentCancellation_DropsLateSourceWithoutSavingOrReplacingExistingThumbnail() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var source = new DelayedSource();
        using var vm = await f.CreateAsync(metadataSources: new[] { source });
        vm.FetchMetadataCommand.Execute(null);
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(vm.IsEditing); Assert.True(vm.IsFetchingDraft); Assert.False(vm.IsSaving);
        vm.FetchMetadataCommand.Execute(null);
        Assert.Equal(1, source.Calls);
        vm.CancelEditCommand.Execute(null);
        source.Release.TrySetResult(new WebVideoMetaResult(new VideoMeta("proposal", null, [], "", [], "incoming"),
            await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg")), ".jpg"));
        await vm.GalleryActionTask;
        Assert.Equal(1, source.Calls);
        Assert.False(vm.IsEditing); Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "cache"))); Assert.Empty(vm.ChangeHistory);
    });

    [Fact]
    public Task MetadataUndo_PreservesLaterUnrelatedFieldAndStopsOnRelatedConflict() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(); var item = vm.SelectedVideo!;
        await vm.BeginGalleryEditAsync(); vm.EditorDraft!.Fields.Single(field => field.Field == MetadataField.Studio).Value = "manual";
        vm.PreviewChangesCommand.Execute(null); await vm.ApplyGalleryChangesAsync();
        var before = (await f.Store.LoadAsync()).Videos.Single(entry => entry.Id == item.Id);
        var lease = vm.TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata)!;
        await vm.CommitEntryCandidateAsync(before, before with { Meta = before.Meta with { Description = "later unrelated" } }, lease);
        vm.ReleaseLibraryMutation(lease);
        vm.UndoCommand.Execute(null); await vm.GalleryActionTask;
        Assert.Null(item.Studio); Assert.Equal("later unrelated", item.Description);
        await vm.BeginGalleryEditAsync(); vm.EditorDraft!.Fields.Single(field => field.Field == MetadataField.Studio).Value = "manual again";
        vm.PreviewChangesCommand.Execute(null); await vm.ApplyGalleryChangesAsync();
        before = (await f.Store.LoadAsync()).Videos.Single(entry => entry.Id == item.Id);
        lease = vm.TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata)!;
        await vm.CommitEntryCandidateAsync(before, before with { Meta = before.Meta with { Studio = "conflict" } }, lease);
        vm.ReleaseLibraryMutation(lease);
        var json = await File.ReadAllTextAsync(f.Store.FilePath);
        vm.UndoCommand.Execute(null); await vm.GalleryActionTask;
        Assert.Equal("conflict", item.Studio); Assert.Equal(json, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.NotNull(vm.ChangeHistory[0].Error); Assert.True(vm.ChangeHistory[0].CanUndo);
    });

    [Fact]
    public Task BatchNativeCancellation_StopsRemainingOsCallsAndRecordsOnlySuccessfulHistory() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        foreach (var item in vm.Videos) item.IsChecked = true;
        vm.ConfirmOperationAsync = _ => Task.FromResult(true);
        f.Recycler.Override = path =>
        {
            if (f.Recycler.Paths.Count == 2) return Task.FromResult(new VideoRecycleResult(VideoRecycleOutcome.Cancelled, "cancelled") { SideEffectsKnownAbsent = true });
            File.Move(path, path + ".test-recycled");
            return Task.FromResult(new VideoRecycleResult(VideoRecycleOutcome.Succeeded) { Receipt = "fake:" + path, MovementConfirmed = true });
        };
        vm.DeleteCommand.Execute(null); await vm.GalleryActionTask;
        Assert.Equal(2, f.Recycler.Paths.Count);
        var stored = await f.Store.LoadAsync();
        Assert.Equal(2, stored.Videos.Count);
        Assert.Equal(LibraryOperationState.Failed, Assert.Single(stored.Journal).State);
        Assert.Equal(1, Assert.Single(vm.ChangeHistory).Count);
        Assert.Contains("완료 1개", vm.StatusMessage); Assert.Contains("취소 1개", vm.StatusMessage); Assert.Contains("미실행 1개", vm.StatusMessage);
    });

    [Fact]
    public Task NativeDiagnosticsAndOriginalReason_SurviveInspectionAndRestartSeparately() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var vm = await f.CreateAsync(); var item = vm.SelectedVideo!;
        f.Recycler.Result = new(VideoRecycleOutcome.Unknown, "native reason") { Diagnostics = "native diagnostics" };
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        f.Recycler.InspectOverride = (_, _) => Task.FromResult(new VideoRecycleInspection(VideoFileEvidence.PresentUnverified, VideoFileEvidence.Unknown, "inspection reason"));
        vm.CurrentGalleryView = GalleryView.Trash; await VideoDeletionFixture.WaitForQueryAsync(vm);
        vm.SelectedVideo = item; vm.ReinspectCommand.Execute(null); await vm.GalleryActionTask;
        var record = (await f.Store.LoadAsync()).LatestOperation(item.Id)!;
        Assert.Equal("native reason", record.Reason); Assert.Equal("native diagnostics", record.NativeDiagnostics);
        Assert.Equal("inspection reason", record.InspectionReason);
        vm.Dispose(); using var reopened = await f.ReopenAsync(); record = (await f.Store.LoadAsync()).LatestOperation(item.Id)!;
        Assert.Equal("native reason", record.Reason); Assert.Equal("native diagnostics", record.NativeDiagnostics);
        Assert.Equal("inspection reason", record.InspectionReason);
    });

    [Fact]
    public Task SamePathDistinctIds_KeepSeparateMetadataThroughEditUndoAndPresentMissingScans() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var vm = await f.CreateAsync(duplicatePath: true);
        var first = vm.Videos.Single(item => item.Title == "Title 0"); var second = vm.Videos.Single(item => item.Title == "Title 1");
        Assert.NotEqual(first.Id, second.Id); Assert.Equal(first.SourcePath, second.SourcePath);
        Assert.Equal(VideoPresenceState.Available, first.Presence); Assert.Equal(first.Presence, second.Presence);
        vm.SelectedVideo = first; await vm.BeginGalleryEditAsync();
        vm.EditorDraft!.Fields.Single(field => field.Field == MetadataField.Studio).Value = "only first";
        vm.PreviewChangesCommand.Execute(null); await vm.ApplyGalleryChangesAsync();
        Assert.Equal("only first", first.Studio); Assert.Null(second.Studio); Assert.Equal("Title 1", second.Title);
        var stored = await f.Store.LoadAsync(); Assert.Equal(3, stored.Videos.Count);
        Assert.Equal("only first", stored.Videos.Single(entry => entry.Id == first.Id).Meta.Studio);
        Assert.Null(stored.Videos.Single(entry => entry.Id == second.Id).Meta.Studio);
        vm.UndoCommand.Execute(null); await vm.GalleryActionTask; Assert.Null(first.Studio); Assert.Null(second.Studio);
        vm.Dispose(); using (var present = await f.ReopenAsync())
        {
            var pair = present.Videos.Where(item => item.Id == first.Id || item.Id == second.Id).ToArray();
            Assert.Equal(2, pair.Length); Assert.All(pair, item => Assert.Equal(VideoPresenceState.Available, item.Presence));
        }
        File.Delete(first.SourcePath); using var missing = await f.ReopenAsync();
        var missingPair = missing.Videos.Where(item => item.Id == first.Id || item.Id == second.Id).ToArray();
        Assert.Equal(2, missingPair.Length); Assert.All(missingPair, item => Assert.Equal(VideoPresenceState.Missing, item.Presence));
        Assert.Equal(3, (await f.Store.LoadAsync()).Videos.Count);
    });

    [Fact]
    public Task BatchRestoreCancellation_StopsRemainingCallsAndDoesNotCountStillTrashedAsRestored() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(legacyTrashCount: 3);
        vm.CurrentGalleryView = GalleryView.Trash; await VideoDeletionFixture.WaitForQueryAsync(vm);
        foreach (var item in vm.Videos) item.IsChecked = true;
        var attempts = 0;
        f.Recycler.RestoreOverride = receipt =>
        {
            if (++attempts == 2) return Task.FromResult(new VideoRecycleResult(VideoRecycleOutcome.Cancelled, "restore cancelled") { SideEffectsKnownAbsent = true });
            var path = receipt[5..]; File.Move(path + ".test-recycled", path);
            return Task.FromResult(new VideoRecycleResult(VideoRecycleOutcome.Succeeded) { Receipt = receipt, MovementConfirmed = true });
        };
        vm.RestoreCommand.Execute(null); await vm.GalleryActionTask;
        Assert.Equal(2, attempts); Assert.Equal(1, vm.ChangeHistory[0].Count); Assert.Equal(LibraryOperationIntent.Restore, vm.ChangeHistory[0].Intent);
        var stored = await f.Store.LoadAsync();
        Assert.Equal(1, stored.Videos.Count(entry => stored.LatestOperation(entry.Id)?.State == LibraryOperationState.Restored));
        Assert.Equal(2, stored.Videos.Count(entry => stored.LatestOperation(entry.Id)?.State == LibraryOperationState.Trashed));
        Assert.Contains("완료 1개", vm.StatusMessage); Assert.Contains("취소 1개", vm.StatusMessage); Assert.Contains("미실행 1개", vm.StatusMessage);
    });

    [Fact]
    public Task ExactReceiptExternalRestore_ReinspectionPersistsRecycleIntentWithRestoredStateAndSameIdAcrossRestart() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var vm = await f.CreateAsync(legacyTrashCount: 1);
        var item = vm.Videos.Single(video => video.IsInRecycleBin);
        var id = item.Id; var bytes = await File.ReadAllBytesAsync(item.SourcePath + ".test-recycled");
        File.Move(item.SourcePath + ".test-recycled", item.SourcePath);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(item.SourcePath));
        vm.CurrentGalleryView = GalleryView.Trash; await VideoDeletionFixture.WaitForQueryAsync(vm);
        vm.SelectedVideo = item; vm.ReinspectCommand.Execute(null); await vm.GalleryActionTask;
        var stored = await f.Store.LoadAsync(); var record = stored.LatestOperation(id)!;
        Assert.Equal(LibraryOperationIntent.Recycle, record.Intent); Assert.Equal(LibraryOperationState.Restored, record.State);
        Assert.True(record.MoveConfirmed); Assert.NotNull(record.ReceiptJson);
        vm.CurrentGalleryView = GalleryView.All; await VideoDeletionFixture.WaitForQueryAsync(vm);
        Assert.Contains(item, vm.FilteredVideos.Cast<VideoItem>()); Assert.Equal(VideoPresenceState.Available, item.Presence);
        vm.Dispose(); using var reopened = await f.ReopenAsync();
        Assert.Equal(3, reopened.NormalCount); Assert.Equal(0, reopened.TrashCount);
        var restored = Assert.Single(reopened.Videos, video => video.Id == id);
        Assert.Contains(restored, reopened.FilteredVideos.Cast<VideoItem>()); Assert.Equal(VideoPresenceState.Available, restored.Presence);
        Assert.Equal("preserved description", restored.Description); Assert.Single(f.Recycler.Paths);
    });

    [Theory]
    [InlineData(VideoFileEvidence.Different)]
    [InlineData(VideoFileEvidence.PresentUnverified)]
    public Task ExistingButUnconfirmedExternalSource_RemainsUncertainAcrossRestart(VideoFileEvidence evidence) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var vm = await f.CreateAsync(legacyTrashCount: 1);
        var item = vm.Videos.Single(video => video.IsInRecycleBin); var id = item.Id;
        File.Move(item.SourcePath + ".test-recycled", item.SourcePath);
        await File.WriteAllBytesAsync(item.SourcePath, new byte[] { 9, 9, 9, 9 });
        f.Recycler.InspectOverride = (_, _) => Task.FromResult(new VideoRecycleInspection(evidence, VideoFileEvidence.Missing, "unconfirmed source"));
        vm.CurrentGalleryView = GalleryView.Trash; await VideoDeletionFixture.WaitForQueryAsync(vm);
        vm.SelectedVideo = item; vm.ReinspectCommand.Execute(null); await vm.GalleryActionTask;
        var record = (await f.Store.LoadAsync()).LatestOperation(id)!;
        Assert.Equal(LibraryOperationState.Uncertain, record.State); Assert.False(record.RestoreAvailable);
        vm.Dispose(); using var reopened = await f.ReopenAsync();
        Assert.Equal(LibraryOperationState.Uncertain, (await f.Store.LoadAsync()).LatestOperation(id)!.State);
        Assert.Equal(2, reopened.NormalCount); Assert.Equal(1, reopened.TrashCount); Assert.Equal(1, reopened.AttentionCount);
        Assert.Equal(id, reopened.Videos.Single(video => video.Id == id).Id); Assert.Single(f.Recycler.Paths);
    });

    private sealed class DelayedSource : IWebVideoMetaSource
    {
        public VideoMetadataSourceKind Kind => VideoMetadataSourceKind.LibreDmm;
        public string Name => "Delayed test source";
        public string Host => "fixture.invalid";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<WebVideoMetaResult> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<WebVideoMetaResult> FetchAsync(MetadataProductCode code, bool includeThumbnail,
            ISet<string> blockedHosts, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            return Release.Task;
        }
    }

}

internal sealed class VideoDeletionFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AiriVideoDeletionTests", Guid.NewGuid().ToString("N"));
    internal LibraryStore Store { get; }
    internal FakeRecycler Recycler { get; } = new();
    internal bool FailSave { get; set; }
    internal bool FailFinalSave { get; set; }
    internal string InitialJson { get; private set; } = "";
    internal string SidecarPath => Path.Combine(Root, "video-0.srt");
    internal string ThumbnailPath => Path.Combine(Root, "cover.jpg");

    internal VideoDeletionFixture()
    {
        Directory.CreateDirectory(Root);
        Store = new LibraryStore(Path.Combine(Root, "videos.json"), (stage, _, _) =>
        {
            if ((FailSave || (FailFinalSave && Recycler.Paths.Count > 0)) && stage == LibraryStoreSaveStage.BeforeCommit) throw new IOException("injected save failure");
        }, null);
        Recycler.MoveOnSuccess = path => File.Move(path, path + ".test-recycled");
    }

    internal async Task<MainViewModel> CreateAsync(Func<string, bool>? deletionFileProbe = null,
        IEnumerable<IWebVideoMetaSource>? metadataSources = null, bool duplicatePath = false, int legacyTrashCount = 0)
    {
        var library = new LibraryData();
        for (var i = 0; i < 3; i++)
        {
            var fileName = metadataSources is null ? $"video-{i}.mp4" : $"ABP-{i + 100}.mp4";
            var path = Path.Combine(Root, fileName);
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, (byte)i });
            library.Videos.Add(new VideoEntry(LibraryPathHelper.NormalizeLibraryPath(path),
                new VideoMeta($"Title {i}", null, new[] { $"Actor {i}" }, ThumbnailPath, Array.Empty<string>(), "preserved description")));
        }
        await File.WriteAllTextAsync(SidecarPath, "test subtitle");
        await File.WriteAllTextAsync(ThumbnailPath, "test thumbnail");
        if (duplicatePath) library.Videos[1] = library.Videos[1] with { Path = library.Videos[0].Path };
        for (var i = 0; i < legacyTrashCount; i++)
        {
            var snapshot = library.Videos[i];
            var result = await Recycler.RecycleAsync(LibraryPathHelper.ResolveToAbsolute(snapshot.Path));
            library.Videos[i] = snapshot with { Revision = 2, Presence = LibraryFilePresence.Missing };
            library.Journal.Add(new LibraryJournalEntry
            {
                EntryId = snapshot.Id,
                BaseRevision = snapshot.Revision,
                OriginalPath = snapshot.Path,
                Snapshot = snapshot,
                Intent = LibraryOperationIntent.Recycle,
                State = LibraryOperationState.Trashed,
                ReceiptJson = result.Receipt,
                MoveConfirmed = true,
                RestoreAvailable = true
            });
        }
        await Store.SaveAsync(library);
        var sources = metadataSources ?? Array.Empty<IWebVideoMetaSource>();
        var vm = new MainViewModel(Store, new Scanner(library), new WebMetadataService(sources,
            new ThumbnailCache(Root), NullTranslationService.Instance, "KO"),
            ThumbnailPerformanceProbe.Disabled, new TestThumbnailImageLoader(),
            recycleService: Recycler, deletionFileProbe: deletionFileProbe);
        await vm.InitializeAsync();
        InitialJson = await File.ReadAllTextAsync(Store.FilePath);
        vm.SelectedSortOption = vm.SortOptions.First(o => o.Field == MainViewModel.SortField.Title && o.Direction == System.ComponentModel.ListSortDirection.Ascending);
        await vm.SettingsSaveTask;
        await WaitForQueryAsync(vm);
        vm.SelectedVideo = vm.FilteredVideos.Cast<VideoItem>().FirstOrDefault();
        return vm;
    }

    internal async Task<MainViewModel> ReopenAsync()
    {
        var library = await Store.LoadAsync();
        var vm = new MainViewModel(Store, new Scanner(library), new WebMetadataService(Array.Empty<IWebVideoMetaSource>(),
            new ThumbnailCache(Root), NullTranslationService.Instance, "KO"),
            ThumbnailPerformanceProbe.Disabled, new TestThumbnailImageLoader(), recycleService: Recycler);
        await vm.InitializeAsync(); await WaitForQueryAsync(vm); return vm;
    }

    internal static async Task WaitForQueryAsync(MainViewModel vm)
    {
        while (true)
        {
            var task = vm.GalleryQueryTask;
            try { await task; } catch (OperationCanceledException) { }
            if (ReferenceEquals(task, vm.GalleryQueryTask) && !vm.IsQuerying) return;
        }
    }

    public void Dispose() => Directory.Delete(Root, true);

    internal sealed class FakeRecycler : IVideoRecycleService
    {
        internal List<string> Paths { get; } = new();
        internal VideoRecycleResult Result { get; set; } = new(VideoRecycleOutcome.Succeeded) { Receipt = "fake", MovementConfirmed = true };
        internal Action<string>? MoveOnSuccess { get; set; }
        internal Func<string, Task<VideoRecycleResult>>? Override { get; set; }
        internal Func<string, string?, Task<VideoRecycleInspection>>? InspectOverride { get; set; }
        internal Func<string, Task<VideoRecycleResult>>? RestoreOverride { get; set; }
        public Task<VideoRecycleResult> RecycleAsync(string absolutePath)
        {
            Paths.Add(absolutePath);
            if (Override is not null) return Override(absolutePath);
            if (Result.Outcome == VideoRecycleOutcome.Succeeded) MoveOnSuccess?.Invoke(absolutePath);
            return Task.FromResult(Result with { Receipt = Result.Receipt is null ? null : "fake:" + absolutePath });
        }
        public Task<VideoRecycleResult> RestoreAsync(string receipt)
        {
            if (RestoreOverride is not null) return RestoreOverride(receipt);
            var path = receipt[5..]; File.Move(path + ".test-recycled", path);
            return Task.FromResult(new VideoRecycleResult(VideoRecycleOutcome.Succeeded) { Receipt = receipt, MovementConfirmed = true });
        }
        public Task<VideoRecycleInspection> InspectAsync(string absolutePath, string? receipt = null) => InspectOverride?.Invoke(absolutePath, receipt) ?? Task.FromResult(
            new VideoRecycleInspection(File.Exists(absolutePath) ? receipt == "fake:" + absolutePath ? VideoFileEvidence.MatchesReceipt : VideoFileEvidence.PresentUnverified : VideoFileEvidence.Missing,
                File.Exists(absolutePath + ".test-recycled") && receipt == "fake:" + absolutePath ? VideoFileEvidence.MatchesReceipt : VideoFileEvidence.Missing));
    }

    private sealed class Scanner(LibraryData library) : ILibraryScanner
    {
        public Task<LibraryScanResult> ScanAsync(LibraryData _, IReadOnlyList<TargetFolder> targets, CancellationToken cancellationToken) =>
            Task.FromResult(new LibraryScanResult(library.Videos.Where(v => File.Exists(LibraryPathHelper.ResolveToAbsolute(v.Path)))
                .DistinctBy(v => LibraryPathHelper.NormalizeLibraryPath(v.Path), StringComparer.OrdinalIgnoreCase).Select(v => new FileSnapshot(v.Path,
                LibraryPathHelper.ResolveToAbsolute(v.Path), v.SizeBytes, v.LastModifiedUtc, v.CreatedUtc)).ToArray(),
                Array.Empty<FileSnapshot>(), Array.Empty<VideoEntry>(), Array.Empty<UpdatedFile>()));
    }
}
