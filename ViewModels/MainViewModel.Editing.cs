using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Web;

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
    public RelayCommand EditInspectedCommand { get; private set; } = null!;
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
    public RelayCommand RefreshDraftMetadataCommand { get; private set; } = null!;
    public RelayCommand TranslateDraftDescriptionCommand { get; private set; } = null!;
    public RelayCommand FetchInspectedMetadataCommand { get; private set; } = null!;

    private void InitializeGalleryEditing()
    {
        EditCommand = new RelayCommand(_ => StartGalleryAction(() => BeginGalleryEditAsync(false)));
        EditInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => BeginGalleryEditAsync(true)));
        PreviewChangesCommand = new RelayCommand(_ => PreviewGalleryChanges(), _ => IsEditing && !IsSaving && !IsFetchingDraft);
        BackToEditCommand = new RelayCommand(_ =>
        {
            if (IsSaving || IsFetchingDraft) return;
            if (EditorDraft is { IsAutomaticProposal: true } draft)
            {
                if (!draft.IsBatch && draft.AutomaticCandidates?.GetValueOrDefault(draft.Targets[0].Id) is { } proposed)
                    foreach (var field in draft.Fields) field.ProposedValue = GalleryMetadataFields.Get(proposed.Meta, field.Field);
                draft.IsAutomaticProposal = false;
            }
            IsChangePreview = false;
        });
        ApplyChangesCommand = new RelayCommand(_ => StartGalleryAction(ApplyGalleryChangesAsync), _ => IsEditing && IsChangePreview && !IsSaving && EditorDraft?.HasChanges == true);
        CancelEditCommand = new RelayCommand(_ => CancelGalleryEdit(), _ => IsEditing && !IsSaving);
        DeleteCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryRecycleAsync(CaptureGalleryTargets(), false)));
        DeleteInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryRecycleAsync(CaptureGalleryTargets(true), false)));
        RestoreCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryRestoreAsync(CaptureGalleryTargets(), false)),
            _ => CanMutateLibrary && !IsEditing && CaptureGalleryTargets().Any(item => item.CanRestore));
        RestoreInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => RunGalleryRestoreAsync(CaptureGalleryTargets(true), false)),
            _ => CanMutateLibrary && !IsEditing && InspectedVideo?.CanRestore == true);
        ReinspectInspectedCommand = new RelayCommand(_ => StartGalleryAction(() => ReinspectGalleryEntriesAsync(CaptureGalleryTargets(true))),
            _ => CanMutateLibrary && !IsEditing && InspectedVideo?.IsInRecycleBin == true);
        ReinspectCommand = new RelayCommand(_ => StartGalleryAction(() => ReinspectGalleryEntriesAsync(CaptureGalleryTargets())));
        UndoCommand = new RelayCommand(_ => StartGalleryAction(UndoGalleryAsync), _ => CanMutateLibrary && !IsEditing && !IsSaving && ChangeHistory.Any(item => item.CanUndo));
        RefreshDraftMetadataCommand = new RelayCommand(_ => StartGalleryAction(RefreshGalleryDraftMetadataAsync), _ => IsEditing && EditorDraft?.IsBatch == false && !IsSaving && !IsFetchingDraft);
        TranslateDraftDescriptionCommand = new RelayCommand(_ => StartGalleryAction(TranslateGalleryDraftDescriptionAsync), _ => IsEditing && EditorDraft?.IsBatch == false && !IsSaving && !IsFetchingDraft);
        FetchInspectedMetadataCommand = new RelayCommand(_ => StartGalleryAction(() => EnrichGalleryTargetsAsync(true)));
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
            RefreshDraftMetadataCommand, TranslateDraftDescriptionCommand, EditCommand, EditInspectedCommand,
            DeleteCommand, DeleteInspectedCommand, RestoreCommand, RestoreInspectedCommand, ReinspectCommand, ReinspectInspectedCommand }) command?.RaiseCanExecuteChanged();
        PropertyChanged?.Invoke(this, new(nameof(IsEditing)));
        PropertyChanged?.Invoke(this, new(nameof(EditorDraft)));
        PropertyChanged?.Invoke(this, new(nameof(ToastUndoAvailable)));
        NotifyGallerySelection();
    }

    public async Task BeginGalleryEditAsync(bool inspectedOnly = false)
    {
        if (IsEditing) return;
        var items = CaptureGalleryTargets(inspectedOnly).ToArray();
        if (items.Length == 0) return;
        if (_mutationLease?.Owner == LibraryMutationOwner.AutoMetadata) await _metadataProcessingTask;
        if (!CanMutateLibrary) { StatusMessage = "진행 중인 작업이 끝난 뒤 편집할 수 있습니다."; return; }
        var entries = items.Select(item => _library.Videos.SingleOrDefault(entry => entry.Id == item.Id)).ToArray();
        if (entries.Any(entry => entry is null)) { StatusMessage = "편집 대상을 확인할 수 없습니다."; return; }
        _galleryEditLease = TryBeginMetadataEditorMutation();
        if (_galleryEditLease is null) return;
        try
        {
            foreach (var item in items) if (StopPreviewForOperationAsync is { } stop) await stop(item);
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
        foreach (var proposal in _draftProposals) _webMetadataService.DiscardCandidate(proposal);
        _draftProposals.Clear();
        if (draft?.AutomaticCandidates is { } automatic) foreach (var candidate in automatic.Values) _webMetadataService.DiscardCandidate(candidate);
        _editorDraft = null;
        IsChangePreview = false;
        if (_galleryEditLease is { } lease) EndMetadataEditorMutation(lease);
        _galleryEditLease = null;
        RefreshGalleryCommands();
    }

    public async Task ApplyGalleryChangesAsync()
    {
        var draft = EditorDraft;
        var lease = _galleryEditLease;
        if (draft is null || lease is null || IsSaving || !IsChangePreview) return;
        var proposals = draft.BuildCandidates();
        if (proposals is null || !draft.HasChanges) return;
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
            await _libraryStore.SaveAsync(candidate, CancellationToken.None);
            EnsureCurrentLease(lease);
            _library = candidate;
            foreach (var change in changes) { _webMetadataService.AcceptCandidate(change.After); ApplyMetadataToCollections(change.After); }
            AddGalleryHistory(new GalleryHistoryItem { Summary = draft.IsAutomaticProposal ? "정보 보강" : draft.IsBatch ? "일괄 편집" : "정보 편집",
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

    private async Task EnrichGalleryTargetsAsync(bool inspectedOnly = false)
    {
        await BeginGalleryEditAsync(inspectedOnly);
        var draft = EditorDraft;
        if (draft is null || _galleryEditLease is null) return;
        draft.MetadataUpdatesNeedingAttention.Clear();
        var candidates = new Dictionary<Guid, VideoEntry>();
        using var fetch = BeginDraftFetch();
        try
        {
            if (!await PrepareGalleryMetadataSourceAsync(draft, fetch.Token)) return;
            foreach (var entry in draft.Targets)
            {
                var query = string.IsNullOrWhiteSpace(entry.Meta.Title) ? System.IO.Path.GetFileNameWithoutExtension(entry.Path) : entry.Meta.Title;
                var response = await _webMetadataService.FetchEnrichmentAsync(entry, query, fetch.Token);
                if (!ReferenceEquals(EditorDraft, draft)) { if (response.Entry is { } stale) _webMetadataService.DiscardCandidate(stale); return; }
                fetch.Token.ThrowIfCancellationRequested();
                if (response.Entry is { } updated) { candidates[entry.Id] = updated; _draftProposals.Add(updated); }
                if (response.Entry is null || response.IsPartial) draft.MetadataUpdatesNeedingAttention.Add(entry.Id);
                draft.TargetResults.Add(new(entry.Meta.Title, response.Entry is not null ? response.IsPartial ? "부분 후보" : "후보 확보" : response.NoResult ? "결과 없음" : "수집 실패", response.Source, response.Error));
            }
            if (candidates.Count == 0) { draft.Error = "새 정보를 찾지 못했습니다. 변경 사항은 저장되지 않았습니다."; return; }
            draft.IsAutomaticProposal = true; draft.AutomaticCandidates = candidates;
            _ = draft.BuildCandidates(); IsChangePreview = true;
            if (!draft.HasChanges) draft.Error = "변경할 빈 정보가 없습니다.";
        }
        finally { EndDraftFetch(fetch); }
    }

    private async Task RefreshGalleryDraftMetadataAsync()
    {
        var draft = EditorDraft;
        if (draft is null || draft.IsBatch) return;
        var entry = draft.Targets[0];
        using var fetch = BeginDraftFetch();
        try
        {
            if (!await PrepareGalleryMetadataSourceAsync(draft, fetch.Token)) return;
            var empty = entry with { Meta = new VideoMeta(string.Empty, null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty) };
            var response = await _webMetadataService.FetchEnrichmentAsync(empty, entry.Meta.Title, fetch.Token);
            if (!ReferenceEquals(EditorDraft, draft)) { if (response.Entry is { } stale) _webMetadataService.DiscardCandidate(stale); return; }
            draft.ProposalSource = response.Source;
            draft.ProposalReason = response.Error;
            draft.IsProposalPartial = response.IsPartial;
            draft.TargetResults.Add(new(entry.Meta.Title, response.Entry is not null ? response.IsPartial ? "부분 후보" : "후보 확보" : response.NoResult ? "결과 없음" : "수집 실패", response.Source, response.Error));
            var proposal = response.Entry;
            if (proposal is null) { draft.Error = response.Error ?? "수집 결과가 없습니다."; return; }
            fetch.Token.ThrowIfCancellationRequested();
            _draftProposals.Add(proposal);
            foreach (var field in draft.Fields)
            {
                var value = GalleryMetadataFields.Get(proposal.Meta, field.Field);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    field.ProposedValue = value;
                    field.ProposalSource = response.Source;
                    field.ProposalReason = response.Error;
                }
            }
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
            var result = await _webMetadataService.TranslateResultDescriptionAsync(new WebVideoMetaResult(
                draft.Targets[0].Meta with { Description = field.Value }, null, null), fetch.Token);
            if (ReferenceEquals(EditorDraft, draft)) { fetch.Token.ThrowIfCancellationRequested(); field.Value = result.Meta.Description; }
        }
        finally { EndDraftFetch(fetch); }
    }

    private async Task<bool> PrepareGalleryMetadataSourceAsync(GalleryEditorDraft draft, CancellationToken token)
    {
        if (!_webMetadataService.RequiresCrawlerSession) return true;
        if (await EnsureCrawlerReadyAsync(token)) { token.ThrowIfCancellationRequested(); return ReferenceEquals(EditorDraft, draft); }
        if (ReferenceEquals(EditorDraft, draft))
        {
            draft.Error = StatusMessage;
            draft.ProposalSource = "141Jav";
            draft.ProposalReason = StatusMessage;
            foreach (var entry in draft.Targets) draft.TargetResults.Add(new(entry.Meta.Title, "수집 준비 실패", "141Jav", StatusMessage));
        }
        return false;
    }

    private CancellationTokenSource BeginDraftFetch()
    {
        var fetch = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
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
            var validState = item.Intent is null ? item.EntryIds.All(id => !_library.IsInRecycleBin(id)) :
                item.EntryIds.All(id => !_unsavedNativeRecords.ContainsKey(id) &&
                    _library.LatestOperation(id) is { } record &&
                    (item.Intent == LibraryOperationIntent.Recycle ? record.State == LibraryOperationState.Trashed && record.RestoreAvailable : record.State == LibraryOperationState.Restored));
            item.CanUndo = ReferenceEquals(item, latest) && validState;
            if (ReferenceEquals(item, latest) && !validState) item.Error = "대상 상태 확인이 필요합니다. 휴지통에서 재검사해 주세요.";
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
