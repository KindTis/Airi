using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;

namespace Airi.ViewModels;

public partial class MainViewModel
{
    private GalleryEditorDraft? _editorDraft;
    private LibraryMutationLease? _galleryEditLease;
    private bool _isChangePreview;
    private bool _isSaving;
    private string? _toastMessage;
    private readonly List<VideoEntry> _draftProposals = new();
    private DispatcherTimer? _toastTimer;
    private CancellationTokenSource? _draftFetchCts;
    public bool IsFetchingDraft => _draftFetchCts is not null;
    public GalleryEditorDraft? EditorDraft => _editorDraft;
    public bool IsEditing => _editorDraft is not null;
    public bool IsChangePreview { get => _isChangePreview; private set => SetProperty(ref _isChangePreview, value); }
    public bool IsSaving { get => _isSaving; private set => SetProperty(ref _isSaving, value); }
    public ObservableCollection<GalleryHistoryItem> ChangeHistory { get; } = new();
    public string? ToastMessage { get => _toastMessage; private set { SetProperty(ref _toastMessage, value); PropertyChanged?.Invoke(this, new(nameof(ToastUndoAvailable))); } }
    public bool ToastUndoAvailable => ToastMessage is not null && ChangeHistory.Any(item => item.CanUndo);
    public Task GalleryActionTask { get; private set; } = Task.CompletedTask;
    public Func<GalleryOperationConfirmation, Task<bool>>? ConfirmOperationAsync { get; set; }
    public Func<VideoItem, Task>? StopPreviewForOperationAsync { get; set; }
    public RelayCommand EditCommand { get; private set; } = null!;
    public RelayCommand PreviewChangesCommand { get; private set; } = null!;
    public RelayCommand BackToEditCommand { get; private set; } = null!;
    public RelayCommand ApplyChangesCommand { get; private set; } = null!;
    public RelayCommand CancelEditCommand { get; private set; } = null!;
    public RelayCommand DeleteCommand { get; private set; } = null!;
    public RelayCommand DeleteInspectedCommand { get; private set; } = null!;
    public RelayCommand RestoreCommand { get; private set; } = null!;
    public RelayCommand ReinspectCommand { get; private set; } = null!;
    public RelayCommand RestoreInspectedCommand { get; private set; } = null!;
    public RelayCommand ReinspectInspectedCommand { get; private set; } = null!;
    public RelayCommand UndoCommand { get; private set; } = null!;
    public RelayCommand TranslateDraftDescriptionCommand { get; private set; } = null!;

    private void InitializeGalleryEditing()
    {
        EditCommand = new RelayCommand(target => StartGalleryAction(() => BeginGalleryEditAsync(CaptureGalleryTargets(focusedItem: target as VideoItem))));
        PreviewChangesCommand = new RelayCommand(_ => PreviewGalleryChanges(), _ => IsEditing && !IsSaving && !IsFetchingDraft);
        BackToEditCommand = new RelayCommand(_ =>
        {
            if (IsSaving || IsFetchingDraft) return;
            if (EditorDraft is { IsAutomaticProposal: true } draft)
            {
                draft.IsAutomaticProposal = false;
            }
            IsChangePreview = false;
        });
        ApplyChangesCommand = new RelayCommand(_ => StartGalleryAction(ApplyGalleryChangesAsync), _ => CanSaveGalleryDraft());
        CancelEditCommand = new RelayCommand(_ => CancelGalleryEdit(), _ => IsEditing && !IsSaving);
        DeleteCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryNativeAsync(CaptureGalleryTargets(), LibraryOperationIntent.Delete, false)));
        DeleteInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryNativeAsync(CaptureGalleryTargets(true), LibraryOperationIntent.Delete, false)));
        RestoreCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryRestoreAsync(CaptureGalleryTargets(), false)),
            _ => CanMutateLibrary && !IsEditing && CaptureGalleryTargets().Any(item => item.CanRestore));
        RestoreInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryRestoreAsync(CaptureGalleryTargets(true), false)),
            _ => CanMutateLibrary && !IsEditing && InspectedVideo?.CanRestore == true);
        ReinspectInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => ReinspectGalleryEntriesAsync(CaptureGalleryTargets(true))),
            _ => CanMutateLibrary && !IsEditing && InspectedVideo?.IsInRecycleBin == true);
        ReinspectCommand = new RelayCommand(_ => StartGalleryAction(() => ReinspectGalleryEntriesAsync(CaptureGalleryTargets())));
        UndoCommand = new RelayCommand(_ => StartGalleryAction(UndoGalleryAsync), _ => CanMutateLibrary && !IsEditing && !IsSaving && ChangeHistory.Any(item => item.CanUndo));
        TranslateDraftDescriptionCommand = new RelayCommand(_ => StartGalleryAction(TranslateGalleryDraftDescriptionAsync), _ => IsEditing && EditorDraft?.IsBatch == false && !IsSaving && !IsFetchingDraft);
        FetchMetadataCommand = new RelayCommand(
            target => StartGalleryAction(() => UpdateGalleryMetadataAsync(target as VideoItem)),
            target => IsEditing
                ? target is null && EditorDraft?.IsBatch == false && !IsSaving && !IsFetchingDraft && !IsChangePreview
                : CanMutateLibrary);
    }

    private void StartGalleryAction(Func<Task> action)
    {
        if (!GalleryActionTask.IsCompleted || IsSaving) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GalleryActionTask = completion.Task;
        _ = CompleteGalleryActionAsync(action, completion);
    }
    private async Task CompleteGalleryActionAsync(Func<Task> action, TaskCompletionSource completion)
    {
        try { await ExecuteGalleryActionAsync(action); }
        finally { completion.TrySetResult(); }
    }
    private async Task ExecuteGalleryActionAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { if (!_disposed) StatusMessage = "작업이 취소되었습니다."; }
        catch (Exception ex)
        {
            AppLogger.Error("Gallery operation failed.", ex);
            if (!_disposed) { StatusMessage = $"작업 실패: {ex.Message}"; if (EditorDraft is not null) EditorDraft.Error = ex.Message; }
        }
        finally { if (!_disposed) RefreshGalleryCommands(); }
    }
    private void RefreshGalleryCommands()
    {
        foreach (var command in new[] { PreviewChangesCommand, ApplyChangesCommand, CancelEditCommand, UndoCommand,
            FetchMetadataCommand, TranslateDraftDescriptionCommand, EditCommand,
            DeleteCommand, DeleteInspectedCommand, RestoreCommand, RestoreInspectedCommand, ReinspectCommand, ReinspectInspectedCommand }) command?.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new(nameof(IsEditing)));
        PropertyChanged?.Invoke(this, new(nameof(EditorDraft)));
        PropertyChanged?.Invoke(this, new(nameof(ToastUndoAvailable)));
        NotifyGallerySelection();
    }

    public Task BeginGalleryEditAsync() => BeginGalleryEditAsync(CaptureGalleryTargets());

    private async Task BeginGalleryEditAsync(IReadOnlyList<VideoItem> items)
    {
        if (IsEditing || _metadataShutdown.IsCancellationRequested) return;
        if (items.Count == 0) return;
        if (_mutationLease?.Owner == LibraryMutationOwner.AutoMetadata) await _metadataProcessingTask;
        if (_metadataShutdown.IsCancellationRequested) return;
        if (!CanMutateLibrary) { StatusMessage = "진행 중인 작업이 끝난 뒤 편집할 수 있습니다."; return; }
        var entries = items.Select(item => _library.Videos.SingleOrDefault(entry => entry.Id == item.Id)).ToArray();
        if (entries.Any(entry => entry is null)) { StatusMessage = "편집 대상을 확인할 수 없습니다."; return; }
        _galleryEditLease = TryBeginMetadataEditorMutation();
        if (_galleryEditLease is null) return;
        try
        {
            foreach (var item in items) if (StopPreviewForOperationAsync is { } stop) await stop(item);
            _metadataShutdown.Token.ThrowIfCancellationRequested();
            InspectedVideo = items[0];
            _editorDraft = new GalleryEditorDraft(entries.Select(entry => CloneEntry(entry!)).ToArray(), items[0]);
        }
        catch
        {
            if (_galleryEditLease is { } failedLease) EndMetadataEditorMutation(failedLease);
            _galleryEditLease = null;
            throw;
        }
        IsChangePreview = false;
        RefreshGalleryCommands();
    }

    private void PreviewGalleryChanges()
    {
        if (EditorDraft is null || IsSaving) return;
        var candidates = EditorDraft.BuildCandidates();
        if (candidates is null) return;
        IsChangePreview = true;
        RefreshGalleryCommands();
    }
    private void CancelGalleryEdit()
    {
        if (IsSaving || EditorDraft is null) return;
        EndGalleryEdit(accepted: false);
        InspectedVideo = null;
    }
    private void EndGalleryEdit(bool accepted)
    {
        var draft = _editorDraft;
        _draftFetchCts?.Cancel();
        if (accepted) draft?.AcceptChanges();
        draft?.Dispose();
        ReleaseDraftProposals();
        _editorDraft = null;
        IsChangePreview = false;
        if (_galleryEditLease is { } lease) EndMetadataEditorMutation(lease);
        _galleryEditLease = null;
        RefreshGalleryCommands();
    }

    private void ReleaseDraftProposals()
    {
        foreach (var proposal in _draftProposals) _webMetadataService.DiscardCandidate(proposal);
        _draftProposals.Clear();
    }

    private bool CanSaveGalleryDraft() => EditorDraft is { } draft && !IsSaving && !IsFetchingDraft
        && (IsChangePreview ? draft.HasChanges : !draft.IsBatch);

    public async Task ApplyGalleryChangesAsync()
    {
        var draft = EditorDraft;
        var lease = _galleryEditLease;
        if (draft is null || lease is null || !CanSaveGalleryDraft()) return;
        var proposals = draft.BuildCandidates();
        if (proposals is null) return;
        if (!draft.HasChanges)
        {
            draft.Error = "저장할 변경 사항이 없습니다.";
            return;
        }
        EnsureCurrentLease(lease);
        var changes = new List<GalleryMetadataUndo>();
        foreach (var before in draft.Targets)
        {
            var current = _library.Videos.SingleOrDefault(entry => entry.Id == before.Id);
            if (current is null || current.Revision != before.Revision)
            { draft.Error = "대상 정보가 변경되었습니다. 초안을 보존했으며 적용할 수 없습니다."; return; }
            var proposed = proposals[before.Id];
            var fields = Enum.GetValues<MetadataField>().Where(field => !GalleryMetadataFields.Equal(before.Meta, proposed.Meta, field) ||
                before.Meta.ManualFieldLocks.Contains(field) != proposed.Meta.ManualFieldLocks.Contains(field)).ToArray();
            if (fields.Length > 0) changes.Add(new(before, proposed with { Revision = checked(current.Revision + 1) }, fields));
        }
        if (changes.Count == 0) return;
        var checkedTargets = Videos.Where(item => item.IsChecked).Select(item => item.Id).ToHashSet();
        var completedTargets = draft.Targets
            .Where(entry => !draft.IsAutomaticProposal || !draft.MetadataUpdatesNeedingAttention.Contains(entry.Id))
            .Select(entry => entry.Id).ToHashSet();
        var changed = changes.ToDictionary(change => change.Before.Id, change => change.After);
        var candidate = _library.WithVideos(_library.Videos.Select(entry => changed.GetValueOrDefault(entry.Id) ?? entry));
        IsSaving = true; RefreshGalleryCommands();
        try
        {
            _lifetimeCts.Token.ThrowIfCancellationRequested();
            await _libraryStore.SaveAsync(candidate, CancellationToken.None);
            foreach (var change in changes) _webMetadataService.AcceptCandidate(change.After);
            EnsureCurrentLease(lease);
            _library = candidate;
            foreach (var change in changes) ApplyMetadataToCollections(change.After);
            var includesMetadataUpdate = draft.IsAutomaticProposal || draft.Fields.Any(field => field.FetchedMetadata is not null);
            AddGalleryHistory(new GalleryHistoryItem { Summary = includesMetadataUpdate ? "메타데이터 갱신" : draft.IsBatch ? "일괄 편집" : "정보 편집",
                Count = changes.Count, Metadata = changes, EntryIds = changes.Select(change => change.Before.Id).ToArray() });
            var needsAttention = draft.IsAutomaticProposal ? draft.MetadataUpdatesNeedingAttention.Count : 0;
            var savedMessage = $"{changes.Count}개 영상의 정보를 저장했습니다.";
            ShowGalleryToast(needsAttention == 0 ? savedMessage : $"{savedMessage} · {needsAttention}개 확인 필요");
            EndGalleryEdit(accepted: true);
            CompleteGalleryCheckedWork(checkedTargets, completedTargets);
            InspectedVideo = null;
            RequestGalleryQuery(moveToTop: false);
        }
        catch (Exception ex) { draft.Error = $"저장 실패: {ex.Message}"; StatusMessage = draft.Error; }
        finally { IsSaving = false; RefreshGalleryCommands(); }
    }

    private async Task UpdateGalleryMetadataAsync(VideoItem? target)
    {
        if (!IsEditing)
        {
            var targets = target is null ? CaptureGalleryTargets() : new[] { target };
            await BeginGalleryEditAsync(targets.Where(item => !item.IsInRecycleBin).ToArray());
        }
        else if (target is not null || EditorDraft?.IsBatch != false || IsChangePreview) return;

        var draft = EditorDraft;
        if (draft is null || _galleryEditLease is null) return;
        draft.Error = null;
        draft.TargetResults.Clear();
        draft.MetadataUpdatesNeedingAttention.Clear();
        var candidates = new Dictionary<Guid, VideoEntry>();
        using var fetch = BeginDraftFetch();
        try
        {
            foreach (var entry in draft.Targets)
            {
                var response = await _webMetadataService.FetchEnrichmentAsync(draft.GetMetadataUpdateTarget(entry), fetch.Token);
                if (!TrackDraftProposal(draft, response.Entry, fetch.Token)) return;
                if (!draft.IsBatch)
                {
                    draft.ProposalSource = response.Source;
                    draft.ProposalReason = null;
                    if (response.Entry is { } fetched)
                    {
                        draft.FillFromMetadata(fetched.Meta, response.FieldSources.Keys);
                        draft.ProposalReason = response.Detail ?? "가져온 정보를 편집창에 채웠습니다.";
                    }
                    else draft.Error = response.Detail ?? MetadataOutcomeLabel(response.Status);
                    return;
                }
                if (response.Entry is { } updated) candidates[entry.Id] = updated;
                if (response.Status is not (MetadataLookupOutcome.Found or MetadataLookupOutcome.Unchanged))
                    draft.MetadataUpdatesNeedingAttention.Add(entry.Id);
                draft.TargetResults.Add(new(entry.Meta.Title, MetadataOutcomeLabel(response.Status), response.Source, response.Detail));
            }
            if (candidates.Count == 0) { draft.Error = "새 정보를 찾지 못했습니다. 변경 사항은 저장되지 않았습니다."; return; }
            draft.IsAutomaticProposal = true; draft.AutomaticCandidates = candidates;
            _ = draft.BuildCandidates(); IsChangePreview = true;
            if (!draft.HasChanges) draft.Error = "갱신할 변경 사항이 없습니다.";
        }
        finally { EndDraftFetch(fetch); }
    }

    private async Task TranslateGalleryDraftDescriptionAsync()
    {
        var draft = EditorDraft;
        var field = draft?.Fields.FirstOrDefault(item => item.Field == MetadataField.Description);
        if (draft is null || field is null) return;
        using var fetch = BeginDraftFetch();
        try
        {
            var original = field.FetchedMetadata?.Description ?? (field.ValueEdited ? field.Value : draft.Targets[0].Meta.Description);
            var meta = draft.Targets[0].Meta with { Description = original };
            var result = await _webMetadataService.TranslateDescriptionAsync(meta, fetch.Token);
            if (ReferenceEquals(EditorDraft, draft) && !string.Equals(original, result.Description, StringComparison.Ordinal))
            {
                fetch.Token.ThrowIfCancellationRequested();
                field.FillFromMetadata(meta with { Korean = (meta.Korean ?? new KoreanMetadata()) with { Description = result.Description } });
            }
        }
        finally { EndDraftFetch(fetch); }
    }

    private bool TrackDraftProposal(GalleryEditorDraft draft, VideoEntry? proposal, CancellationToken token)
    {
        if (!ReferenceEquals(EditorDraft, draft) || token.IsCancellationRequested)
        {
            if (proposal is not null) _webMetadataService.DiscardCandidate(proposal);
            token.ThrowIfCancellationRequested();
            return false;
        }
        if (proposal is not null) _draftProposals.Add(proposal);
        return true;
    }

    private static string MetadataOutcomeLabel(MetadataLookupOutcome outcome) => outcome switch
    {
        MetadataLookupOutcome.Unidentified => "품번 식별 불가",
        MetadataLookupOutcome.NotFound => "결과 없음",
        MetadataLookupOutcome.Failed => "조회 실패",
        MetadataLookupOutcome.Partial => "부분 후보",
        MetadataLookupOutcome.Found => "후보 확보",
        MetadataLookupOutcome.Unchanged => "변경 없음",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    private CancellationTokenSource BeginDraftFetch()
    {
        var shutdownToken = _metadataShutdown.Token;
        shutdownToken.ThrowIfCancellationRequested();
        var fetch = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        _draftFetchCts = fetch;
        PropertyChanged?.Invoke(this, new(nameof(IsFetchingDraft))); RefreshGalleryCommands();
        return fetch;
    }
    private void EndDraftFetch(CancellationTokenSource fetch)
    {
        if (ReferenceEquals(_draftFetchCts, fetch)) _draftFetchCts = null;
        PropertyChanged?.Invoke(this, new(nameof(IsFetchingDraft))); RefreshGalleryCommands();
    }

    private void AddGalleryHistory(GalleryHistoryItem item)
    {
        ChangeHistory.Insert(0, item); RefreshHistoryUndo();
    }
    private void RefreshHistoryUndo()
    {
        var latest = ChangeHistory.FirstOrDefault(item => !item.IsUndone);
        foreach (var item in ChangeHistory)
        {
            var validState = item.Intent is null ? item.EntryIds.All(id => _videoIndex.ContainsKey(id) && !_library.IsInRecycleBin(id)) :
                item.Intent != LibraryOperationIntent.Delete && item.EntryIds.All(id => !_unsavedNativeRecords.ContainsKey(id) &&
                    _library.LatestOperation(id) is { } record &&
                    (item.Intent == LibraryOperationIntent.Recycle ? record.State == LibraryOperationState.Trashed && record.RestoreAvailable : record.State == LibraryOperationState.Restored));
            item.CanUndo = ReferenceEquals(item, latest) && validState;
            if (ReferenceEquals(item, latest) && !validState && item.Intent != LibraryOperationIntent.Delete)
                item.Error = "대상 상태 확인이 필요합니다. 휴지통에서 재검사해 주세요.";
        }
        UndoCommand?.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new(nameof(ToastUndoAvailable)));
    }
    private void ShowGalleryToast(string message)
    {
        ToastMessage = message;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(6500), DispatcherPriority.Background,
            (_, _) => { _toastTimer?.Stop(); ToastMessage = null; }, _dispatcher);
        _toastTimer.Start();
    }
    private async Task UndoGalleryAsync()
    {
        if (!CanMutateLibrary || IsEditing) return;
        var history = ChangeHistory.FirstOrDefault(item => item.CanUndo);
        if (history is null) return;
        if (history.Intent is not null) { await UndoNativeGalleryHistoryAsync(history); return; }
        var lease = TryBeginMetadataEditorMutation();
        if (lease is null) return;
        IsSaving = true;
        try
        {
            var replacements = new Dictionary<Guid, VideoEntry>();
            foreach (var change in history.Metadata)
            {
                var current = _library.Videos.SingleOrDefault(entry => entry.Id == change.After.Id);
                if (current is null || _library.IsInRecycleBin(current.Id)) throw new InvalidOperationException("대상 상태가 바뀌어 되돌릴 수 없습니다.");
                var meta = current.Meta;
                foreach (var field in change.Fields)
                {
                    if (!GalleryMetadataFields.Equal(current.Meta, change.After.Meta, field) ||
                        current.Meta.ManualFieldLocks.Contains(field) != change.After.Meta.ManualFieldLocks.Contains(field))
                        throw new InvalidOperationException("관련 필드가 변경되어 되돌릴 수 없습니다.");
                    meta = GalleryMetadataFields.CopyField(meta, change.Before.Meta, field);
                    var locks = meta.ManualFieldLocks.ToHashSet();
                    if (change.Before.Meta.ManualFieldLocks.Contains(field)) locks.Add(field); else locks.Remove(field);
                    meta = meta with { ManualFieldLocks = locks.OrderBy(value => value).ToArray() };
                }
                replacements[current.Id] = current with { Meta = meta, Revision = checked(current.Revision + 1) };
            }
            var candidate = _library.WithVideos(_library.Videos.Select(entry => replacements.GetValueOrDefault(entry.Id) ?? entry));
            await _libraryStore.SaveAsync(candidate, CancellationToken.None);
            EnsureCurrentLease(lease); _library = candidate;
            foreach (var entry in replacements.Values) ApplyMetadataToCollections(entry);
            history.Error = null;
            history.IsUndone = true; RefreshHistoryUndo(); RequestGalleryQuery(moveToTop: false);
            ShowGalleryToast("변경을 되돌렸습니다.");
        }
        catch (Exception ex) { history.Error = ex.Message; StatusMessage = $"되돌리기 실패: {ex.Message}"; }
        finally { IsSaving = false; EndMetadataEditorMutation(lease); RefreshGalleryCommands(); }
    }
}
