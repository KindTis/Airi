using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Data;
using System.Windows.Threading;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.ViewModels;

public partial class MainViewModel
{
    private GalleryView _currentGalleryView;
    private VideoItem? _inspectedVideo;
    private CancellationTokenSource? _galleryQueryCancellation;
    private long _galleryQueryGeneration;
    private long _publishedQueryGeneration;
    private bool _galleryQueryScheduled;
    private bool _hasGalleryResult;
    private HashSet<Guid> _galleryResultIds = new();
    private readonly HashSet<GalleryFacetValue> _actorFilters = new();
    private readonly HashSet<GalleryFacetValue> _studioFilters = new();
    private readonly HashSet<GalleryFacetValue> _genreFilters = new();
    private TaskCompletionSource _galleryQueryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _isQuerying;
    private LibrarySnapshot? _gallerySnapshotCache;
    private bool _gallerySnapshotDirty = true;
    internal Func<LibrarySnapshot, GalleryQuery, long, CancellationToken, Task<GalleryQueryResult>>? GalleryQueryEvaluator { get; set; }
    public ObservableCollection<GalleryFacetOption> ActorFacets { get; } = new();
    public ObservableCollection<GalleryFacetOption> StudioFacets { get; } = new();
    public ObservableCollection<GalleryFacetOption> GenreFacets { get; } = new();
    public ObservableCollection<GalleryChip> QueryChips { get; } = new();
    public GalleryView CurrentGalleryView { get => _currentGalleryView; set => SetGalleryView(value); }
    public VideoItem? InspectedVideo
    {
        get => _inspectedVideo;
        set
        {
            if (!IsEditing && SetProperty(ref _inspectedVideo, value))
            { RestoreInspectedCommand?.RaiseCanExecuteChanged(); ReinspectInspectedCommand?.RaiseCanExecuteChanged(); }
        }
    }
    public bool IsQuerying { get => _isQuerying; private set => SetProperty(ref _isQuerying, value); }
    public long PublishedQueryGeneration => _publishedQueryGeneration;
    public Task GalleryQueryTask => _galleryQueryCompletion.Task;
    public int ResultCount => _galleryResultIds.Count;
    public IReadOnlyList<string> LibraryFolderPaths => _settings?.Targets.Select(target => target.Root).ToArray() ?? Array.Empty<string>();
    public int NormalCount { get; private set; }
    public int IncompleteCount { get; private set; }
    public int MissingCount { get; private set; }
    public int TrashCount { get; private set; }
    public int AttentionCount { get; private set; }
    public int CheckedCount => Videos.Count(item => item.IsChecked);
    public int VisibleCheckedCount => Videos.Count(item => item.IsChecked && _galleryResultIds.Contains(item.Id));
    public int HiddenCheckedCount => CheckedCount - VisibleCheckedCount;
    public bool? AllVisibleChecked => ResultCount == 0 || VisibleCheckedCount == 0 ? false :
        VisibleCheckedCount == ResultCount ? true : null;
    public bool CanToggleVisibleChecked => ResultCount > 0 && !IsEditing;
    public RelayCommand SetViewCommand { get; private set; } = null!;
    public RelayCommand ToggleVisibleCheckedCommand { get; private set; } = null!;
    public RelayCommand ClearCheckedCommand { get; private set; } = null!;
    public RelayCommand ClearFiltersCommand { get; private set; } = null!;
    public RelayCommand InspectCommand { get; private set; } = null!;
    public event Action? GalleryScrollToTopRequested;
    public event Action? GalleryQueryRequested;
    internal event Action? GalleryCheckedWorkCompleted;
    private bool _queryMovesToTop;

    private void InitializeGallery()
    {
        _galleryQueryCompletion.TrySetResult();
        SetViewCommand = new RelayCommand(parameter =>
        {
            if (parameter is GalleryView view) SetGalleryView(view);
            else if (Enum.TryParse<GalleryView>(parameter?.ToString(), out var parsed)) SetGalleryView(parsed);
        });
        ToggleVisibleCheckedCommand = new RelayCommand(_ =>
        {
            if (!GuardGalleryEdit()) return;
            var value = AllVisibleChecked != true;
            SetGalleryChecked(Videos.Where(item => _galleryResultIds.Contains(item.Id)), value);
        }, _ => CanToggleVisibleChecked);
        ClearCheckedCommand = new RelayCommand(_ => { if (GuardGalleryEdit()) ClearGalleryChecked(); });
        ClearFiltersCommand = new RelayCommand(_ => ClearGalleryFilters());
        InspectCommand = new RelayCommand(parameter =>
        {
            if (parameter is VideoItem item && !IsEditing) { InspectedVideo = item; }
        });
        InitializeGalleryEditing();
    }

    private bool GuardGalleryEdit()
    {
        if (!IsEditing) return true;
        StatusMessage = "적용하거나 취소해 주세요.";
        return false;
    }

    private void SetGalleryView(GalleryView value)
    {
        if (_currentGalleryView == value || !GuardGalleryEdit()) return;
        _currentGalleryView = value;
        PropertyChanged?.Invoke(this, new(nameof(CurrentGalleryView)));
        _searchQuery = string.Empty;
        PropertyChanged?.Invoke(this, new(nameof(SearchQuery)));
        _actorFilters.Clear(); _studioFilters.Clear(); _genreFilters.Clear();
        _selectedActor = AllActorsLabel;
        PropertyChanged?.Invoke(this, new(nameof(SelectedActor)));
        ClearGalleryChecked();
        RequestGalleryQuery();
    }

    public void AddFacetFilter(GalleryFacet facet, string? value, bool unspecified = false)
    {
        if (!GuardGalleryEdit()) return;
        Filters(facet).Add(new(value, unspecified));
        RequestGalleryQuery();
    }
    public void SearchForTag(string tag) => SearchQuery = tag;
    private HashSet<GalleryFacetValue> Filters(GalleryFacet facet) => facet switch
    { GalleryFacet.Actors => _actorFilters, GalleryFacet.Studio => _studioFilters, _ => _genreFilters };
    private void ClearGalleryFilters()
    {
        if (!GuardGalleryEdit()) return;
        _actorFilters.Clear(); _studioFilters.Clear(); _genreFilters.Clear();
        _selectedActor = AllActorsLabel; _searchQuery = string.Empty;
        PropertyChanged?.Invoke(this, new(nameof(SearchQuery)));
        PropertyChanged?.Invoke(this, new(nameof(SelectedActor)));
        RequestGalleryQuery();
    }
    private bool _updatingGalleryChecks;

    private void ClearGalleryChecked() => SetGalleryChecked(Videos, false);

    internal void CheckGalleryRange(IEnumerable<VideoItem> items)
    {
        if (GuardGalleryEdit()) SetGalleryChecked(items, true);
    }

    private void SetGalleryChecked(IEnumerable<VideoItem> items, bool value)
    {
        _updatingGalleryChecks = true;
        try
        {
            foreach (var item in items) item.SetCheckedFromOwner(value);
        }
        finally { _updatingGalleryChecks = false; }
        NotifyGallerySelection();
    }

    private void CompleteGalleryCheckedWork(IReadOnlySet<Guid> checkedTargets, IReadOnlySet<Guid> completedTargets)
    {
        if (checkedTargets.Count == 0 || completedTargets.Count == 0) return;
        SetGalleryChecked(Videos.Where(item => checkedTargets.Contains(item.Id) && completedTargets.Contains(item.Id)), false);
        if (checkedTargets.IsSubsetOf(completedTargets) && CheckedCount == 0)
            GalleryCheckedWorkCompleted?.Invoke();
    }
    private IReadOnlyList<VideoItem> CaptureGalleryTargets(bool inspectedOnly = false, VideoItem? focusedItem = null)
    {
        if (inspectedOnly) return InspectedVideo is { } inspected ? new[] { inspected } : Array.Empty<VideoItem>();
        var checkedItems = Videos.Where(item => item.IsChecked).ToArray();
        return checkedItems.Length > 0 ? checkedItems : (focusedItem ?? SelectedVideo) is { } active ? new[] { active } : Array.Empty<VideoItem>();
    }
    private void NotifyGallerySelection()
    {
        foreach (var property in new[] { nameof(CheckedCount), nameof(VisibleCheckedCount), nameof(HiddenCheckedCount),
            nameof(AllVisibleChecked), nameof(CanToggleVisibleChecked) }) PropertyChanged?.Invoke(this, new(property));
        ToggleVisibleCheckedCommand?.RaiseCanExecuteChanged();
        RestoreCommand?.RaiseCanExecuteChanged(); RestoreInspectedCommand?.RaiseCanExecuteChanged();
        ReinspectCommand?.RaiseCanExecuteChanged(); ReinspectInspectedCommand?.RaiseCanExecuteChanged();
    }
    private void ConfigureGalleryItem(VideoItem item)
    {
        item.CheckedChangeAllowed = GuardGalleryEdit;
        item.PropertyChanged -= GalleryItemChanged;
        item.PropertyChanged += GalleryItemChanged;
    }
    private void GalleryItemChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(VideoItem.IsChecked))
        {
            if (!_updatingGalleryChecks) NotifyGallerySelection();
        }
        else if (args.PropertyName is not (nameof(VideoItem.IsActive) or nameof(VideoItem.ThumbnailSource) or nameof(VideoItem.ThumbnailLoadState)))
        {
            _gallerySnapshotDirty = true;
            if (args.PropertyName is nameof(VideoItem.Presence) or nameof(VideoItem.RecycleState)) RequestGalleryQuery(moveToTop: false);
        }
    }

    private LibrarySnapshot CaptureGallerySnapshot()
    {
        if (!_gallerySnapshotDirty && _gallerySnapshotCache is not null) return _gallerySnapshotCache;
        var canonical = _library.Videos.ToDictionary(entry => entry.Id);
        var operations = _library.Journal.GroupBy(record => record.EntryId).ToDictionary(group => group.Key, group => group.Last());
        _gallerySnapshotDirty = false;
        return _gallerySnapshotCache = new LibrarySnapshot(Videos.Select(item =>
        {
            var entry = canonical.GetValueOrDefault(item.Id) ?? new VideoEntry(item.LibraryPath,
                new VideoMeta(item.Title, item.ReleaseDate, item.Actors.ToArray(), item.ThumbnailPath, item.Tags.ToArray(), item.Description)
                { Studio = item.Studio, Genres = item.Genres.ToArray(), RawDate = item.RawDate }, item.SizeBytes,
                item.LastModifiedUtc, item.CreatedUtc) { Id = item.Id, Revision = item.Revision };
            var operation = operations.GetValueOrDefault(item.Id);
            var trash = operation?.State is LibraryOperationState.Pending or LibraryOperationState.Trashed or
                LibraryOperationState.RestorePending or LibraryOperationState.Uncertain or LibraryOperationState.RepairRequired;
            return new GalleryEntrySnapshot(CloneEntry(entry), item.Presence, trash,
                operation?.State is LibraryOperationState.Pending or LibraryOperationState.RestorePending or
                LibraryOperationState.Uncertain or LibraryOperationState.RepairRequired);
        }).ToArray());
    }

    private void RequestGalleryQuery(bool moveToTop = true)
    {
        if (_disposed) return;
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(() => RequestGalleryQuery(moveToTop))); return; }
        GalleryQueryRequested?.Invoke();
        _galleryQueryGeneration++;
        _galleryQueryCancellation?.Cancel();
        _galleryQueryCompletion.TrySetCanceled();
        _galleryQueryCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _queryMovesToTop |= moveToTop;
        IsQuerying = true;
        if (_galleryQueryScheduled) return;
        _galleryQueryScheduled = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(StartGalleryQuery));
    }

    private void StartGalleryQuery()
    {
        _galleryQueryScheduled = false;
        if (_disposed) { _galleryQueryCompletion.TrySetCanceled(); return; }
        var generation = _galleryQueryGeneration;
        var completion = _galleryQueryCompletion;
        _galleryQueryCancellation?.Dispose();
        var cancellation = _galleryQueryCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token);
        var snapshot = CaptureGallerySnapshot();
        var query = new GalleryQuery(CurrentGalleryView, SearchQuery, _actorFilters.ToArray(), _studioFilters.ToArray(),
            _genreFilters.ToArray(), SelectedSortOption.Field.ToString(), SelectedSortOption.Direction);
        _ = CalculateGalleryQueryAsync(snapshot, query, generation, cancellation.Token, completion);
    }
    private async Task CalculateGalleryQueryAsync(LibrarySnapshot snapshot, GalleryQuery query, long generation,
        CancellationToken token, TaskCompletionSource completion)
    {
        try
        {
            var evaluator = GalleryQueryEvaluator;
            var result = evaluator is null ? await Task.Run(() => GalleryQueryEngine.Evaluate(snapshot, query, token), token).ConfigureAwait(false) :
                await evaluator(snapshot, query, generation, token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || token.IsCancellationRequested || generation != _galleryQueryGeneration) { completion.TrySetCanceled(); return; }
                _hasGalleryResult = true;
                _galleryResultIds = result.Ids.ToHashSet();
                _publishedQueryGeneration = generation;
                NormalCount = result.NormalCount; IncompleteCount = result.IncompleteCount; MissingCount = result.MissingCount;
                TrashCount = result.TrashCount; AttentionCount = result.AttentionCount;
                PublishFacets(ActorFacets, GalleryFacet.Actors, result.Actors, _actorFilters);
                PublishFacets(StudioFacets, GalleryFacet.Studio, result.Studios, _studioFilters);
                PublishFacets(GenreFacets, GalleryFacet.Genres, result.Genres, _genreFilters);
                PublishQueryChips();
                if (FilteredVideos is ListCollectionView list)
                {
                    using (list.DeferRefresh()) { list.SortDescriptions.Clear(); list.CustomSort = new GalleryIdOrder(result.Ids); }
                }
                else FilteredVideos.Refresh();
                var items = Videos.ToDictionary(item => item.Id);
                if (SelectedVideo is null || !_galleryResultIds.Contains(SelectedVideo.Id))
                    SelectedVideo = result.Ids.Count > 0 ? items.GetValueOrDefault(result.Ids[0]) : null;
                if (InspectedVideo is not null && !_galleryResultIds.Contains(InspectedVideo.Id) && !IsEditing) InspectedVideo = null;
                foreach (var property in new[] { nameof(PublishedQueryGeneration), nameof(ResultCount), nameof(NormalCount),
                    nameof(IncompleteCount), nameof(MissingCount), nameof(TrashCount), nameof(AttentionCount) })
                    PropertyChanged?.Invoke(this, new(property));
                NotifyGallerySelection();
                IsQuerying = false;
                if (_queryMovesToTop) { _queryMovesToTop = false; GalleryScrollToTopRequested?.Invoke(); }
                completion.TrySetResult();
            });
        }
        catch (OperationCanceledException) { completion.TrySetCanceled(); }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (generation == _galleryQueryGeneration && !_disposed) { IsQuerying = false; StatusMessage = $"조회 실패: {ex.Message}"; }
            });
            completion.TrySetException(ex);
        }
    }
    private void PublishFacets(ObservableCollection<GalleryFacetOption> target, GalleryFacet facet,
        IReadOnlyList<GalleryFacetCount> counts, HashSet<GalleryFacetValue> selected)
    {
        var previous = target.ToDictionary(option => option.Key);
        var index = 0;
        foreach (var count in counts)
        {
            if (!previous.TryGetValue(count.Value, out var option)) option = new(count.Value, count.Count, selected.Contains(count.Value), (changed, value) =>
            {
                if (!GuardGalleryEdit()) { changed.SetSelected(selected.Contains(changed.Key)); return; }
                if (value) selected.Add(changed.Key); else selected.Remove(changed.Key);
                changed.SetSelected(value); RequestGalleryQuery();
            });
            option.Update(count.Count, selected.Contains(count.Value), count.DisplayLabel);
            if (index >= target.Count) target.Add(option);
            else if (!ReferenceEquals(target[index], option))
            {
                var oldIndex = target.IndexOf(option);
                if (oldIndex >= 0) target.Move(oldIndex, index); else target.Insert(index, option);
            }
            index++;
        }
        while (target.Count > index) target.RemoveAt(target.Count - 1);
    }
    private void PublishQueryChips()
    {
        QueryChips.Clear();
        if (!string.IsNullOrWhiteSpace(SearchQuery)) QueryChips.Add(new(SearchQuery, new RelayCommand(_ => SearchQuery = string.Empty)));
        foreach (var facet in Enum.GetValues<GalleryFacet>())
            foreach (var value in Filters(facet).ToArray()) QueryChips.Add(new($"{facet switch { GalleryFacet.Actors => "배우", GalleryFacet.Studio => "제작사", _ => "장르" }} {FacetLabel(facet, value)}", new RelayCommand(_ =>
            { if (GuardGalleryEdit()) { Filters(facet).Remove(value); RequestGalleryQuery(); } })));
    }

    private string FacetLabel(GalleryFacet facet, GalleryFacetValue key)
    {
        var options = facet switch { GalleryFacet.Actors => ActorFacets, GalleryFacet.Studio => StudioFacets, _ => GenreFacets };
        return options.FirstOrDefault(option => option.Key == key)?.Label ??
            (facet == GalleryFacet.Actors && !key.IsUnspecified ? new ActorName(key.Value ?? string.Empty).DisplayName : key.Label);
    }
    private sealed class GalleryIdOrder(IReadOnlyList<Guid> ids) : IComparer
    {
        private readonly Dictionary<Guid, int> _positions = ids.Select((id, index) => (id, index)).ToDictionary(pair => pair.id, pair => pair.index);
        public int Compare(object? x, object? y) => _positions.GetValueOrDefault(((VideoItem)x!).Id, int.MaxValue)
            .CompareTo(_positions.GetValueOrDefault(((VideoItem)y!).Id, int.MaxValue));
    }
}
