using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;

namespace Airi.ViewModels;

internal sealed record VideoDeletionTarget(VideoItem Item, string LibraryPath, string AbsolutePath, string Title, string FileName);

public partial class MainViewModel
{
    private readonly IVideoRecycleService _videoRecycleService;
    private readonly Func<string, bool> _deletionFileProbe;
    private VideoDeletionTarget? _videoDeletionTarget;
    private readonly HashSet<Guid> _nativeOperationIds = new();
    private readonly Dictionary<Guid, LibraryJournalEntry> _unsavedNativeRecords = new();
    internal Task VideoDeletionTask { get; private set; } = Task.CompletedTask;
    internal bool IsVideoDeletionTarget(VideoItem item) => _nativeOperationIds.Contains(item.Id) || ReferenceEquals(_videoDeletionTarget?.Item, item);

    internal Task DeleteSelectedVideoAsync(Func<VideoDeletionTarget, Task<bool>> confirm, Func<VideoItem, Task> stopPreview)
    {
        _dispatcher.VerifyAccess();
        var item = SelectedVideo;
        if (item is null || IsEditing || !CanMutateLibrary) return Task.CompletedTask;
        VideoDeletionTarget target;
        try { target = CreateDeletionTarget(item); }
        catch (Exception ex) { StatusMessage = $"비디오 삭제 실패: {ex.Message}"; return Task.CompletedTask; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoDeletionTask = completion.Task;
        _videoDeletionTarget = target;
        _ = CompleteLegacyDeletionAsync(target, confirm, stopPreview, completion);
        return completion.Task;
    }
    private async Task CompleteLegacyDeletionAsync(VideoDeletionTarget target, Func<VideoDeletionTarget, Task<bool>> confirm,
        Func<VideoItem, Task> stopPreview, TaskCompletionSource completion)
    {
        try { await RunGalleryNativeAsync(new[] { target.Item }, LibraryOperationIntent.Delete, false,
            _ => confirm(target), stopPreview); }
        catch (Exception ex) { StatusMessage = $"비디오 삭제 실패: {ex.Message}"; }
        finally { _videoDeletionTarget = null; completion.TrySetResult(); }
    }
    private VideoDeletionTarget CreateDeletionTarget(VideoItem item)
    {
        var libraryPath = LibraryPathHelper.NormalizeLibraryPath(item.LibraryPath);
        var absolutePath = LibraryPathHelper.ResolveToAbsolute(libraryPath);
        var target = new VideoDeletionTarget(item, libraryPath, absolutePath, item.Title, Path.GetFileName(absolutePath));
        if (!IsValidDeletionIdentity(target)) throw new IOException("대상 항목이나 파일 경로가 변경되었습니다.");
        return target;
    }
    private bool IsValidDeletionIdentity(VideoDeletionTarget target) => !_disposed && Videos.Contains(target.Item) &&
        _videoIndex.TryGetValue(target.Item.Id, out var indexed) && ReferenceEquals(indexed, target.Item) &&
        _library.Videos.Any(entry => entry.Id == target.Item.Id &&
            LibraryPathHelper.NormalizeLibraryPath(entry.Path).Equals(target.LibraryPath, StringComparison.OrdinalIgnoreCase)) &&
        LibraryPathHelper.NormalizeLibraryPath(target.Item.LibraryPath).Equals(target.LibraryPath, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(target.Item.SourcePath).Equals(target.AbsolutePath, StringComparison.OrdinalIgnoreCase);

    private Task RunGalleryRecycleAsync(IReadOnlyList<VideoItem> items, bool undo) =>
        RunGalleryNativeAsync(items, LibraryOperationIntent.Recycle, undo);
    private Task RunGalleryRestoreAsync(IReadOnlyList<VideoItem> items, bool undo) =>
        RunGalleryNativeAsync(items, LibraryOperationIntent.Restore, undo);

    private async Task RunGalleryNativeAsync(IReadOnlyList<VideoItem> items, LibraryOperationIntent intent, bool undo,
        Func<GalleryOperationConfirmation, Task<bool>>? confirmation = null, Func<VideoItem, Task>? stopPreview = null)
    {
        if (IsEditing || !CanMutateLibrary || items.Count == 0) return;
        var captured = items.DistinctBy(item => item.Id).Select(item => (Item: item,
            Entry: _library.Videos.SingleOrDefault(entry => entry.Id == item.Id))).Where(pair => pair.Entry is not null).ToArray();
        if (captured.Length == 0) return;
        var lease = TryBeginLibraryMutation(LibraryMutationOwner.VideoDeletion);
        if (lease is null) return;
        var checkedTargets = items.Where(item => item.IsChecked).Select(item => item.Id).ToHashSet();
        var successes = new List<Guid>();
        var errors = new List<string>();
        var deletesEntries = intent == LibraryOperationIntent.Delete;
        var recyclesFiles = intent != LibraryOperationIntent.Restore;
        var recycledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cancelled = 0;
        var notRun = 0;
        IsSaving = true;
        foreach (var pair in captured) _nativeOperationIds.Add(pair.Item.Id);
        try
        {
            var effective = new List<(VideoItem Item, VideoEntry Entry, bool FileExists)>();
            foreach (var pair in captured)
            {
                var entry = pair.Entry!;
                try
                {
                    var fileExists = recyclesFiles && await ProbeNativeTargetAsync(pair.Item, entry, lease);
                    if (recyclesFiles && !deletesEntries && !fileExists) throw new IOException("영상 파일이 없습니다.");
                    effective.Add((pair.Item, entry, fileExists));
                }
                catch (Exception ex) { errors.Add($"{entry.Meta.Title}: {ex.Message}"); }
            }
            if (effective.Count == 0) { StatusMessage = "작업 실패: " + string.Join(" / ", errors); return; }
            var confirm = confirmation ?? ConfirmOperationAsync;
            if (recyclesFiles)
            {
                if (confirm is null) { StatusMessage = "삭제 확인이 필요합니다."; return; }
                var info = new GalleryOperationConfirmation(intent, effective.Select(pair => pair.Entry.Meta.Title).ToArray())
                { Paths = effective.Select(pair => LibraryPathHelper.ResolveToAbsolute(pair.Entry.Path)).ToArray() };
                if (!await confirm(info)) return;
            }
            for (var targetIndex = 0; targetIndex < effective.Count; targetIndex++)
            {
                var pair = effective[targetIndex];
                var stopAfterCurrent = false;
                var entry = pair.Entry;
                var item = pair.Item;
                try
                {
                    EnsureCurrentLease(lease);
                    var current = _library.Videos.SingleOrDefault(value => value.Id == entry.Id);
                    if (current is null || current.Revision != entry.Revision || !IsValidDeletionIdentity(CreateDeletionTarget(item)))
                        throw new IOException("대상 정보나 경로가 변경되었습니다.");
                    var latest = _library.LatestOperation(entry.Id);
                    var absolute = LibraryPathHelper.ResolveToAbsolute(entry.Path);
                    // Multiple catalogue entries can point to the same file. Move it at most once.
                    var fileExpected = pair.FileExists && !recycledPaths.Contains(absolute);
                    if (recyclesFiles)
                    {
                        await VerifyNativeTargetAsync(item, entry, lease, fileExpected);
                    }
                    else
                    {
                        if (latest?.State != LibraryOperationState.Trashed || !latest.RestoreAvailable || string.IsNullOrWhiteSpace(latest.ReceiptJson))
                            throw new IOException("정확한 복원 근거가 없습니다. 휴지통에서 재검사해 주세요.");
                        if (await Task.Run(() => File.Exists(absolute) || Directory.Exists(absolute))) throw new IOException("원래 경로에 이미 항목이 있습니다.");
                        if (!await Task.Run(() => Directory.Exists(Path.GetDirectoryName(absolute)))) throw new IOException("원래 부모 폴더가 없습니다.");
                    }
                    var stop = stopPreview ?? StopPreviewForOperationAsync;
                    if (stop is not null) await stop(item);
                    EnsureCurrentLease(lease);
                    if (recyclesFiles) await VerifyNativeTargetAsync(item, entry, lease, fileExpected);
                    if (deletesEntries && !fileExpected)
                    {
                        var candidate = WithoutLibraryEntries(_library, new[] { entry.Id });
                        await _libraryStore.SaveAsync(candidate, CancellationToken.None);
                        EnsureCurrentLease(lease);
                        _library = candidate;
                        RemoveDeletedGalleryItem(item);
                        successes.Add(entry.Id);
                        continue;
                    }
                    var pending = new LibraryJournalEntry
                    {
                        EntryId = entry.Id, BaseRevision = entry.Revision, OriginalPath = entry.Path,
                        Snapshot = CloneEntry(entry), Intent = intent,
                        State = recyclesFiles ? LibraryOperationState.Pending : LibraryOperationState.RestorePending,
                        ReceiptJson = intent == LibraryOperationIntent.Restore ? latest!.ReceiptJson : null,
                        RestoreAvailable = intent == LibraryOperationIntent.Restore
                    };
                    var pendingEntry = entry with { Revision = checked(entry.Revision + 1) };
                    var pendingLibrary = _library.WithVideos(_library.Videos.Select(value => value.Id == entry.Id ? pendingEntry : value));
                    pendingLibrary.Journal.Add(pending);
                    // A failure here must result in zero calls to Windows Shell.
                    await _libraryStore.SaveAsync(pendingLibrary, CancellationToken.None);
                    EnsureCurrentLease(lease); _library = pendingLibrary; item.Revision = pendingEntry.Revision;
                    ApplyJournalToItem(item, pending); RequestGalleryQuery(moveToTop: false);
                    VideoRecycleResult result;
                    try
                    {
                        result = recyclesFiles ? await _videoRecycleService.RecycleAsync(absolute) :
                            await _videoRecycleService.RestoreAsync(pending.ReceiptJson!);
                    }
                    catch (Exception ex) { result = new(VideoRecycleOutcome.Unknown, ex.Message); }
                    EnsureCurrentLease(lease);
                    if (result.Outcome == VideoRecycleOutcome.Cancelled)
                    {
                        stopAfterCurrent = true;
                        cancelled++;
                        notRun = effective.Count - targetIndex - 1;
                    }
                    var final = ClassifyNativeResult(pending, result);
                    var deleted = IsCompletedDeletion(final);
                    var finalEntry = pendingEntry with { Revision = checked(pendingEntry.Revision + 1),
                        Presence = final.State == LibraryOperationState.Restored ? LibraryFilePresence.Available :
                            final.State == LibraryOperationState.Trashed ? LibraryFilePresence.Missing : pendingEntry.Presence,
                        PresenceProbedAtUtc = final.MoveConfirmed ? DateTime.UtcNow : pendingEntry.PresenceProbedAtUtc };
                    var finalLibrary = deleted ? WithoutLibraryEntries(_library, new[] { entry.Id }) :
                        _library.WithVideos(_library.Videos.Select(value => value.Id == entry.Id ? finalEntry : value));
                    if (!deleted)
                        finalLibrary.Journal = finalLibrary.Journal.Select(record => record.OperationId == pending.OperationId ? final : record).ToList();
                    try
                    {
                        await _libraryStore.SaveAsync(finalLibrary, CancellationToken.None);
                        EnsureCurrentLease(lease); _library = finalLibrary;
                        _unsavedNativeRecords.Remove(entry.Id);
                        if (deleted)
                        {
                            RemoveDeletedGalleryItem(item);
                            recycledPaths.Add(absolute);
                            successes.Add(entry.Id);
                            continue;
                        }
                        item.Revision = finalEntry.Revision;
                        ApplyJournalToItem(item, final);
                        if (IsCompletedNativeIntent(final))
                        {
                            successes.Add(entry.Id);
                            item.Presence = final.State == LibraryOperationState.Restored ? VideoPresenceState.Available : VideoPresenceState.Missing;
                            RemoveFromMetadataQueue(LibraryPathHelper.NormalizeLibraryPath(entry.Path));
                        }
                        else errors.Add($"{entry.Meta.Title}: {final.Reason ?? final.State.ToString()}");
                    }
                    catch (Exception ex)
                    {
                        // Durable state is still Pending. Session-only evidence must never masquerade as durable success.
                        _unsavedNativeRecords[entry.Id] = final;
                        item.RecycleReason = $"OS 결과 저장 실패: {ex.Message}. 재검사가 필요합니다.";
                        errors.Add($"{entry.Meta.Title}: 결과 저장 실패");
                    }
                    RequestGalleryQuery(moveToTop: false);
                }
                catch (Exception ex) { errors.Add($"{entry.Meta.Title}: {ex.Message}"); }
                if (stopAfterCurrent) break;
            }
            var action = deletesEntries ? "삭제" : recyclesFiles ? "휴지통 이동" : "복원";
            if (successes.Count > 0 && !undo)
                AddGalleryHistory(new GalleryHistoryItem { Summary = action,
                    Intent = intent, Count = successes.Count, EntryIds = successes.ToArray() });
            if (successes.Count > 0) ShowGalleryToast($"{successes.Count}개 {action} 완료" + (errors.Count > 0 ? $" · {errors.Count}개 확인 필요" : string.Empty));
            var summary = $"완료 {successes.Count}개 · 취소 {cancelled}개 · 미실행 {notRun}개";
            StatusMessage = errors.Count > 0 ? summary + " · 작업 실패 또는 확인 필요: " + string.Join(" / ", errors) : summary;
            if (errors.Count == 0 && successes.Count > 0) InspectedVideo = null;
            CompleteGalleryCheckedWork(checkedTargets, successes.ToHashSet());
        }
        finally
        {
            foreach (var pair in captured) _nativeOperationIds.Remove(pair.Item.Id);
            IsSaving = false; ReleaseLibraryMutation(lease); RandomPlayCommand.RaiseCanExecuteChanged(); RefreshHistoryUndo(); RefreshGalleryCommands();
        }
    }

    private static bool ProbeDeletionFile(string path)
    {
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) throw new IOException("영상 경로에 폴더가 있습니다.");
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException)
        {
            // An unavailable drive or share does not establish that the file is missing.
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) throw;
            _ = File.GetAttributes(root);
            return false;
        }
    }

    private async Task<bool> ProbeNativeTargetAsync(VideoItem item, VideoEntry entry, LibraryMutationLease lease)
    {
        EnsureCurrentLease(lease);
        var target = CreateDeletionTarget(item);
        if (_library.IsInRecycleBin(entry.Id) || _unsavedNativeRecords.ContainsKey(entry.Id))
            throw new IOException("확인이 필요한 이동 기록이 있어 다시 실행할 수 없습니다.");
        var exists = await Task.Run(() => _deletionFileProbe(target.AbsolutePath));
        EnsureCurrentLease(lease);
        if (!IsValidDeletionIdentity(target) || _library.Videos.SingleOrDefault(value => value.Id == entry.Id)?.Revision != entry.Revision)
            throw new IOException("대상 항목이나 파일 경로가 변경되었습니다.");
        return exists;
    }

    private async Task VerifyNativeTargetAsync(VideoItem item, VideoEntry entry, LibraryMutationLease lease, bool fileExpected)
    {
        if (await ProbeNativeTargetAsync(item, entry, lease) != fileExpected)
            throw new IOException("파일 상태가 변경되었습니다. 대상을 확인하고 다시 시도해 주세요.");
    }

    private static LibraryData WithoutLibraryEntries(LibraryData library, IEnumerable<Guid> entryIds)
    {
        var ids = entryIds.ToHashSet();
        var candidate = library.WithVideos(library.Videos.Where(entry => !ids.Contains(entry.Id)));
        candidate.Journal.RemoveAll(record => ids.Contains(record.EntryId));
        return candidate;
    }

    private void RemoveDeletedGalleryItem(VideoItem item)
    {
        RemoveFromMetadataQueue(LibraryPathHelper.NormalizeLibraryPath(item.LibraryPath));
        _videoIndex.Remove(item.Id);
        Videos.Remove(item);
    }

    private static bool IsCompletedDeletion(LibraryJournalEntry record) =>
        record is { Intent: LibraryOperationIntent.Delete, State: LibraryOperationState.Trashed };

    private static bool IsCompletedNativeIntent(LibraryJournalEntry record) =>
        record.Intent == LibraryOperationIntent.Restore ? record.State == LibraryOperationState.Restored : record.State == LibraryOperationState.Trashed;

    private static LibraryJournalEntry ClassifyNativeResult(LibraryJournalEntry pending, VideoRecycleResult result)
    {
        var exact = result.Outcome == VideoRecycleOutcome.Succeeded && result.MovementConfirmed && !string.IsNullOrWhiteSpace(result.Receipt);
        var state = exact ? pending.Intent == LibraryOperationIntent.Restore ? LibraryOperationState.Restored : LibraryOperationState.Trashed :
            result.Outcome == VideoRecycleOutcome.Succeeded && result.MovementConfirmed ? LibraryOperationState.RepairRequired :
            result.Outcome is VideoRecycleOutcome.Failed or VideoRecycleOutcome.Cancelled && result.SideEffectsKnownAbsent ?
                pending.Intent == LibraryOperationIntent.Restore ? LibraryOperationState.Trashed : LibraryOperationState.Failed : LibraryOperationState.Uncertain;
        return pending with
        {
            State = state, Outcome = result.Outcome.ToString(), Reason = result.Reason, NativeDiagnostics = result.Diagnostics,
            ReceiptJson = result.Receipt ?? pending.ReceiptJson,
            ObservedDestinationPath = result.RecycledPath ?? result.ObservedDestinationPath,
            MoveConfirmed = result.MovementConfirmed,
            RestoreAvailable = (exact && pending.Intent != LibraryOperationIntent.Delete) ||
                (pending.Intent == LibraryOperationIntent.Restore && state == LibraryOperationState.Trashed && pending.RestoreAvailable)
        };
    }

    private void ApplyJournalToItem(VideoItem item, LibraryJournalEntry? record)
    {
        item.RecycleState = record?.State;
        item.RecycleReason = string.Join(" · ", new[] { record?.Reason, record?.NativeDiagnostics, record?.InspectionReason, record?.Error }
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        item.CanRestore = record?.State == LibraryOperationState.Trashed && record.RestoreAvailable && !string.IsNullOrWhiteSpace(record.ReceiptJson);
    }
    private async Task ReinspectGalleryEntriesAsync(IReadOnlyList<VideoItem> items)
    {
        if (!CanMutateLibrary || IsEditing) return;
        var lease = TryBeginLibraryMutation(LibraryMutationOwner.VideoDeletion);
        if (lease is null) return;
        IsSaving = true;
        try
        {
            var candidate = _library.WithVideos(_library.Videos);
            var recovered = new List<LibraryJournalEntry>();
            var deletedIds = new HashSet<Guid>();
            foreach (var item in items.DistinctBy(item => item.Id))
            {
                var durable = candidate.LatestOperation(item.Id);
                if (durable is null) continue;
                var evidence = _unsavedNativeRecords.GetValueOrDefault(item.Id) ?? durable;
                var inspection = await _videoRecycleService.InspectAsync(LibraryPathHelper.ResolveToAbsolute(durable.OriginalPath), evidence.ReceiptJson);
                var updated = ReconcileInspection(evidence, inspection);
                if (IsCompletedDeletion(updated)) deletedIds.Add(item.Id);
                if (_unsavedNativeRecords.ContainsKey(item.Id) && IsCompletedNativeIntent(updated))
                    recovered.Add(updated);
                candidate.Journal = candidate.Journal.Select(record => record.OperationId == durable.OperationId ? updated with { OperationId = durable.OperationId } : record).ToList();
            }
            if (deletedIds.Count > 0) candidate = WithoutLibraryEntries(candidate, deletedIds);
            await _libraryStore.SaveAsync(candidate, CancellationToken.None);
            EnsureCurrentLease(lease); _library = candidate;
            foreach (var item in items.DistinctBy(item => item.Id))
            {
                _unsavedNativeRecords.Remove(item.Id);
                if (deletedIds.Contains(item.Id)) { RemoveDeletedGalleryItem(item); continue; }
                var record = candidate.LatestOperation(item.Id);
                ApplyJournalToItem(item, record);
                if (record?.State == LibraryOperationState.Restored) item.Presence = VideoPresenceState.Available;
                if (record?.State == LibraryOperationState.Trashed) item.Presence = VideoPresenceState.Missing;
            }
            foreach (var group in recovered.GroupBy(record => record.Intent))
                AddGalleryHistory(new GalleryHistoryItem { Summary = group.Key switch
                {
                    LibraryOperationIntent.Delete => "삭제",
                    LibraryOperationIntent.Recycle => "휴지통 이동 결과 저장",
                    _ => "복원 결과 저장"
                },
                    Intent = group.Key, Count = group.Count(), EntryIds = group.Select(record => record.EntryId).ToArray() });
            RequestGalleryQuery(moveToTop: false); RefreshHistoryUndo();
            StatusMessage = deletedIds.Count > 0 ? $"{deletedIds.Count}개 삭제 완료" : "이동·복원 기록을 재검사했습니다.";
        }
        finally { IsSaving = false; ReleaseLibraryMutation(lease); }
    }
    private static LibraryJournalEntry ReconcileInspection(LibraryJournalEntry record, VideoRecycleInspection inspection)
    {
        var hasReceipt = !string.IsNullOrWhiteSpace(record.ReceiptJson);
        var state = hasReceipt && inspection.SourceState == VideoFileEvidence.Missing && inspection.RecycleState == VideoFileEvidence.MatchesReceipt ? LibraryOperationState.Trashed :
            hasReceipt && inspection.SourceState == VideoFileEvidence.MatchesReceipt && inspection.RecycleState == VideoFileEvidence.Missing ? LibraryOperationState.Restored :
            inspection.SourceState == VideoFileEvidence.Missing && inspection.RecycleState == VideoFileEvidence.Missing && record.State == LibraryOperationState.Trashed ?
                LibraryOperationState.RepairRequired : LibraryOperationState.Uncertain;
        return record with { State = state, InspectionReason = inspection.Reason,
            MoveConfirmed = record.MoveConfirmed || state is LibraryOperationState.Trashed or LibraryOperationState.Restored,
            RestoreAvailable = state == LibraryOperationState.Trashed && hasReceipt && record.Intent != LibraryOperationIntent.Delete };
    }
    private async Task<LibraryData> RecoverPendingJournalAsync(LibraryData library)
    {
        var pending = library.Journal.GroupBy(record => record.EntryId).Select(group => group.Last()).Where(record =>
            record.State is LibraryOperationState.Pending or LibraryOperationState.RestorePending or LibraryOperationState.Uncertain).ToArray();
        if (pending.Length == 0) return library;
        var candidate = library.WithVideos(library.Videos);
        var deletedIds = new HashSet<Guid>();
        foreach (var record in pending)
        {
            VideoRecycleInspection inspection;
            try { inspection = await _videoRecycleService.InspectAsync(LibraryPathHelper.ResolveToAbsolute(record.OriginalPath), record.ReceiptJson); }
            catch (Exception ex) { inspection = new(VideoFileEvidence.Unknown, VideoFileEvidence.Unknown, ex.Message); }
            var reconciled = ReconcileInspection(record, inspection);
            if (IsCompletedDeletion(reconciled)) deletedIds.Add(record.EntryId);
            candidate.Journal = candidate.Journal.Select(value => value.OperationId == record.OperationId ? reconciled : value).ToList();
        }
        if (deletedIds.Count > 0) candidate = WithoutLibraryEntries(candidate, deletedIds);
        try { await _libraryStore.SaveAsync(candidate, CancellationToken.None); return candidate; }
        catch (Exception ex) { AppLogger.Error("Pending journal recovery could not be saved; durable pending state retained.", ex); return library; }
    }
    private async Task UndoNativeGalleryHistoryAsync(GalleryHistoryItem history)
    {
        var items = Videos.Where(item => history.EntryIds.Contains(item.Id)).ToArray();
        if (items.Length != history.EntryIds.Count) { StatusMessage = "되돌릴 대상을 찾을 수 없습니다."; return; }
        var expected = history.Intent == LibraryOperationIntent.Recycle ? LibraryOperationState.Trashed : LibraryOperationState.Restored;
        if (history.EntryIds.Any(id => _library.LatestOperation(id)?.State != expected || _unsavedNativeRecords.ContainsKey(id)))
        { StatusMessage = "대상 상태가 바뀌었거나 확인이 필요하여 되돌릴 수 없습니다."; return; }
        var before = _library.Journal.Count;
        if (history.Intent == LibraryOperationIntent.Recycle) await RunGalleryRestoreAsync(items, true);
        else await RunGalleryRecycleAsync(items, true);
        var resultState = history.Intent == LibraryOperationIntent.Recycle ? LibraryOperationState.Restored : LibraryOperationState.Trashed;
        if (_library.Journal.Count > before && history.EntryIds.All(id => _library.LatestOperation(id)?.State == resultState))
        { history.IsUndone = true; RefreshHistoryUndo(); }
    }
}
