using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Web;

namespace Airi.ViewModels
{
    public enum StartupLibraryState
    {
        Loading,
        Publishing,
        Scanning,
        ApplyingScan,
        SavingScan,
        Ready,
        Faulted
    }

    internal enum LibraryMutationOwner
    {
        StartupScan,
        AutoMetadata,
        EditorSave,
        VideoDeletion
    }

    internal sealed record LibraryMutationLease(LibraryMutationOwner Owner, Guid Identity);

    internal readonly record struct ThumbnailRuntimeDiagnostics(
        bool Exists,
        long RuntimeIdentity,
        long Generation,
        string? Outcome,
        bool HasInFlight,
        string? SourcePath,
        int DecodePixelWidth)
    {
        public static ThumbnailRuntimeDiagnostics Missing { get; } =
            new(false, 0, 0, null, false, null, 0);
    }

    /// <summary>
    /// Loads persisted library data, exposes UI-facing collections, and coordinates scanning/diff and web metadata enrichment.
    /// </summary>
    public partial class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private enum ThumbnailRequestOutcome
        {
            Loading,
            Loaded,
            Failed,
            Cancelled
        }

        private sealed class ThumbnailRuntimeState
        {
            public long RuntimeIdentity { get; init; }
            public long Generation { get; set; }
            public ThumbnailRealization? Realization { get; set; }
            public ThumbnailInFlight? InFlight { get; set; }
        }

        private sealed record ThumbnailRealization(
            long Generation,
            Guid Identity,
            string SourcePath,
            int DecodePixelWidth,
            ThumbnailRequestOutcome Outcome);

        private sealed record ThumbnailInFlight(
            long Generation,
            Guid Identity,
            CancellationTokenSource Cancellation);

        private const string AllActorsLabel = "All Actors";
        private const int InitialLoadBatchSize = 40;
        private readonly Random _random = new();
        private readonly ILibraryStore _libraryStore;
        private readonly SettingsStore _settingsStore;
        private readonly ILibraryScanner _libraryScanner;
        private readonly WebMetadataService _webMetadataService;
        private readonly Dispatcher _dispatcher;
        private readonly ThumbnailPerformanceProbe _performanceProbe;
        private readonly IThumbnailImageLoader _thumbnailImageLoader;
        private readonly Func<VideoEntry, CancellationToken, VideoItem>? _storedVideoMapper;
        private readonly object _lifetimeSchedulingLock = new();
        private bool _lifetimeEnded;
        private readonly Dictionary<Guid, VideoItem> _videoIndex = new();
        private readonly object _metadataQueueLock = new();
        private readonly Queue<string> _pendingMetadata = new();
        private readonly HashSet<string> _metadataScheduled = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _thumbnailUriCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _thumbnailRuntimeLock = new();
        private readonly Dictionary<VideoItem, ThumbnailRuntimeState> _thumbnailRuntime = new(ReferenceEqualityComparer.Instance);
        private long _nextThumbnailRuntimeIdentity;
        private bool _disposed;
        private Task _metadataProcessingTask = Task.CompletedTask;
        private Task _settingsSaveTask = Task.CompletedTask;
        private readonly string _fallbackThumbnailUri;
        private AppSettings? _settings;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly CancellationTokenSource _metadataShutdown = new();
        public enum SortField
        {
            Title,
            ReleaseDate,
            CreatedUtc
        }

        public sealed record SortOption(string Label, SortField Field, ListSortDirection Direction);


        private LibraryData _library;
        private string _searchQuery = string.Empty;
        private string _selectedActor = AllActorsLabel;
        private string _statusMessage = string.Empty;
        private VideoItem? _selectedVideo;
        private StartupLibraryState _startupState = StartupLibraryState.Loading;
        private LibraryMutationLease? _mutationLease;
        private SortOption _selectedSortOption;
        private bool _showMissingMetadataOnly;
        private bool _isInitialLoading = true;
        private bool _isActorListLoading = true;
        private bool _startupPublishesStoredLibrary;

        public ObservableCollection<VideoItem> Videos { get; }
        public ObservableCollection<string> Actors { get; }
        public ICollectionView FilteredVideos { get; }

        public ObservableCollection<SortOption> SortOptions { get; }
        public SortOption SelectedSortOption
        {
            get => _selectedSortOption;
            set
            {
                if (value is null || !GuardGalleryEdit()) return;

                if (SetProperty(ref _selectedSortOption, value))
                {
                    ApplySort(value.Field, value.Direction);
                    if (_settings is not null)
                    {
                        _settings.Sort = new SortSettings
                        {
                            Field = value.Field.ToString(),
                            Direction = value.Direction.ToString()
                        };
                        _settingsSaveTask = SaveSortSettingsAsync(_settings);
                    }
                }
            }
        }

        internal Task SettingsSaveTask => _settingsSaveTask;

        public RelayCommand RandomPlayCommand { get; }
        public RelayCommand FetchMetadataCommand { get; private set; } = null!;
        public RelayCommand ClearSearchCommand { get; }


        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<VideoItem>? PlayVideoRequested;

        public MainViewModel(
            ILibraryStore libraryStore,
            ILibraryScanner libraryScanner,
            WebMetadataService webMetadataService,
            IThumbnailImageLoader thumbnailImageLoader)
            : this(
                libraryStore,
                libraryScanner,
                webMetadataService,
                ThumbnailPerformanceProbe.Disabled,
                thumbnailImageLoader)
        {
        }

        internal MainViewModel(
            ILibraryStore libraryStore,
            ILibraryScanner libraryScanner,
            WebMetadataService webMetadataService,
            ThumbnailPerformanceProbe performanceProbe,
            IThumbnailImageLoader thumbnailImageLoader,
            Func<VideoEntry, CancellationToken, VideoItem>? storedVideoMapper = null,
            SettingsStore? settingsStore = null,
            IVideoRecycleService? recycleService = null,
            Func<string, bool>? deletionFileProbe = null)
        {
            _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            _videoRecycleService = recycleService ?? new WindowsVideoRecycleService();
            _deletionFileProbe = deletionFileProbe ?? ProbeDeletionFile;
            _settingsStore = settingsStore ?? new SettingsStore(_libraryStore.FilePath);
            _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
            _webMetadataService = webMetadataService ?? throw new ArgumentNullException(nameof(webMetadataService));
            _performanceProbe = performanceProbe ?? throw new ArgumentNullException(nameof(performanceProbe));
            _thumbnailImageLoader = thumbnailImageLoader ?? throw new ArgumentNullException(nameof(thumbnailImageLoader));
            _storedVideoMapper = storedVideoMapper;
            var applicationDispatcher = Application.Current?.Dispatcher;
            _dispatcher = applicationDispatcher is not null && applicationDispatcher.CheckAccess()
                ? applicationDispatcher
                : Dispatcher.CurrentDispatcher;
            _library = new LibraryData();
            _fallbackThumbnailUri = GetFallbackThumbnailUri();

            Videos = new ObservableCollection<VideoItem>();
            Videos.CollectionChanged += OnVideosCollectionChanged;
            Actors = new ObservableCollection<string>(BuildActorList(Videos));
            Actors.CollectionChanged += OnActorsCollectionChanged;
            FilteredVideos = CollectionViewSource.GetDefaultView(Videos);
            FilteredVideos.Filter = FilterVideo;

            SortOptions = new ObservableCollection<SortOption>(new[]
            {
                new SortOption("출시일 최신순", SortField.ReleaseDate, ListSortDirection.Descending),
                new SortOption("출시일 오래된순", SortField.ReleaseDate, ListSortDirection.Ascending),
                new SortOption("제목 가나다순", SortField.Title, ListSortDirection.Ascending),
                new SortOption("등록순", SortField.CreatedUtc, ListSortDirection.Descending),
                new SortOption("제목 역순", SortField.Title, ListSortDirection.Descending),
                new SortOption("등록 오래된순", SortField.CreatedUtc, ListSortDirection.Ascending)
            });

            RandomPlayCommand = new RelayCommand(
                _ => PlayRandomVideo(),
                _ => FilteredVideos.Cast<VideoItem>().Any(v => v.Presence == VideoPresenceState.Available && !v.IsInRecycleBin && !IsVideoDeletionTarget(v)));
            ClearSearchCommand = new RelayCommand(_ => ClearSearch());

            InitializeGallery();
            var defaultSort = SortOptions.First(option => option.Field == SortField.ReleaseDate && option.Direction == ListSortDirection.Descending);
            _selectedSortOption = defaultSort;
            ApplySort(defaultSort.Field, defaultSort.Direction, updateStatus: false);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedSortOption)));

            SelectedActor = AllActorsLabel;
            UpdateStatus();
            SelectedVideo = Videos.FirstOrDefault();
        }

        public string SearchQuery
        {
            get => _searchQuery;
            set
            {
                if (!GuardGalleryEdit()) { PropertyChanged?.Invoke(this, new(nameof(SearchQuery))); return; }
                if (SetProperty(ref _searchQuery, value ?? string.Empty)) RequestGalleryQuery();
            }
        }

        public string SelectedActor
        {
            get => _selectedActor;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? AllActorsLabel : value;

                if (Actors is not null && !Actors.Any(actor => actor.Equals(normalized, StringComparison.OrdinalIgnoreCase))
                    && !ActorFacets.Any(option => string.Equals(option.Key.Value, normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    normalized = AllActorsLabel;
                }

                if (!GuardGalleryEdit()) return;
                if (SetProperty(ref _selectedActor, normalized))
                {
                    _actorFilters.Clear();
                    if (normalized != AllActorsLabel)
                    {
                        foreach (var option in ActorFacets.Where(option => string.Equals(option.Label, normalized, StringComparison.OrdinalIgnoreCase)))
                            _actorFilters.Add(option.Key);
                        if (_actorFilters.Count == 0) _actorFilters.Add(new(normalized));
                    }
                    RequestGalleryQuery();
                }
            }
        }

        public bool ShowMissingMetadataOnly
        {
            get => _showMissingMetadataOnly;
            set
            {
                if (!GuardGalleryEdit()) return;
                if (SetProperty(ref _showMissingMetadataOnly, value)) SetGalleryView(value ? GalleryView.Incomplete : GalleryView.All);
            }
        }

        public VideoItem? SelectedVideo
        {
            get => _selectedVideo;
            set
            {
                if (ReferenceEquals(_selectedVideo, value)) return;
                if (_selectedVideo is not null) _selectedVideo.IsActive = false;
                SetProperty(ref _selectedVideo, value);
                if (value is not null) value.IsActive = true;
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        public StartupLibraryState StartupState => _startupState;

        public bool IsScanning => StartupState is
            StartupLibraryState.Scanning or
            StartupLibraryState.ApplyingScan or
            StartupLibraryState.SavingScan;

        public bool IsFetchingMetadata => _mutationLease?.Owner == LibraryMutationOwner.AutoMetadata;

        public bool CanMutateLibrary =>
            !_disposed && StartupState == StartupLibraryState.Ready && _mutationLease is null;

        public bool CanUseCommandBar => true;

        internal CancellationToken LifetimeToken => _lifetimeCts.Token;
        internal LibraryMutationLease? CurrentMutationLease => _mutationLease;

        public bool IsInitialLoading
        {
            get => _isInitialLoading;
            private set
            {
                if (SetProperty(ref _isInitialLoading, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowVideoSkeleton)));
                }
            }
        }

        public bool IsActorListLoading
        {
            get => _isActorListLoading;
            private set
            {
                if (SetProperty(ref _isActorListLoading, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowActorSkeleton)));
                }
            }
        }

        public bool ShowVideoSkeleton => IsInitialLoading || (IsScanning && Videos.Count == 0);
        public bool ShowActorSkeleton => IsActorListLoading || (IsScanning && Actors.Count <= 1);

        internal void SetStartupState(StartupLibraryState next)
        {
            _dispatcher.VerifyAccess();
            if (_startupState == next)
            {
                return;
            }

            var allowed = (_startupState, next) switch
            {
                (StartupLibraryState.Loading, StartupLibraryState.Publishing or StartupLibraryState.Faulted) => true,
                (StartupLibraryState.Publishing, StartupLibraryState.Scanning or StartupLibraryState.Ready or StartupLibraryState.Faulted) => true,
                (StartupLibraryState.Scanning, StartupLibraryState.SavingScan or StartupLibraryState.ApplyingScan or StartupLibraryState.Ready) => true,
                (StartupLibraryState.ApplyingScan, StartupLibraryState.SavingScan or StartupLibraryState.Ready or StartupLibraryState.Faulted) => true,
                (StartupLibraryState.SavingScan, StartupLibraryState.ApplyingScan or StartupLibraryState.Ready or StartupLibraryState.Faulted) => true,
                _ => false
            };
            if (!allowed)
            {
                throw new InvalidOperationException($"Invalid startup library state transition: {_startupState} -> {next}.");
            }

            var wasScanning = IsScanning;
            _startupState = next;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartupState)));
            if (wasScanning != IsScanning)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsScanning)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowVideoSkeleton)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowActorSkeleton)));
            }
            RaiseMutationStateChanged();
            UpdateStatus(updateMessage: false);
        }

        internal LibraryMutationLease? TryBeginLibraryMutation(LibraryMutationOwner owner)
        {
            _dispatcher.VerifyAccess();
            if (_disposed || _mutationLease is not null)
            {
                return null;
            }

            var lease = new LibraryMutationLease(owner, Guid.NewGuid());
            _mutationLease = lease;
            RaiseMutationStateChanged();
            return lease;
        }

        internal void ReleaseLibraryMutation(LibraryMutationLease lease)
        {
            _dispatcher.VerifyAccess();
            if (_mutationLease?.Identity != lease.Identity || _mutationLease.Owner != lease.Owner)
            {
                return;
            }

            _mutationLease = null;
            RaiseMutationStateChanged();
        }

        internal LibraryMutationLease? TryBeginMetadataEditorMutation()
        {
            _dispatcher.VerifyAccess();
            if (!CanMutateLibrary)
            {
                StatusMessage = "Library is busy. Try again when startup work completes.";
                return null;
            }
            return TryBeginLibraryMutation(LibraryMutationOwner.EditorSave);
        }

        internal void EndMetadataEditorMutation(LibraryMutationLease lease) => ReleaseLibraryMutation(lease);

        private void EnsureCurrentLease(LibraryMutationLease lease)
        {
            _dispatcher.VerifyAccess();
            if (_mutationLease?.Identity != lease.Identity || _mutationLease.Owner != lease.Owner)
            {
                throw new InvalidOperationException("The active library mutation lease changed unexpectedly.");
            }
        }

        private void RaiseMutationStateChanged()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMutateLibrary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsFetchingMetadata)));
            FetchMetadataCommand?.RaiseCanExecuteChanged();
            UndoCommand?.RaiseCanExecuteChanged(); RestoreCommand?.RaiseCanExecuteChanged();
            RestoreInspectedCommand?.RaiseCanExecuteChanged(); ReinspectInspectedCommand?.RaiseCanExecuteChanged();
        }

        public Task RequestThumbnailAsync(
            VideoItem item,
            int decodePixelWidth,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            _dispatcher.VerifyAccess();
            ObjectDisposedException.ThrowIf(_disposed, this);

            decodePixelWidth = Math.Clamp(decodePixelWidth, 64, 520);
            var sourcePath = NormalizeThumbnailSourcePath(item.ThumbnailPath);
            long generation;
            long runtimeIdentity;
            Guid identity;
            CancellationTokenSource requestCancellation;
            bool releaseLoadedOwner;

            lock (_thumbnailRuntimeLock)
            {
                var state = GetOrCreateThumbnailRuntimeState(item);
                if (state.Realization is { } existing &&
                    StringComparer.OrdinalIgnoreCase.Equals(existing.SourcePath, sourcePath) &&
                    existing.DecodePixelWidth == decodePixelWidth)
                {
                    return Task.CompletedTask;
                }

                state.InFlight?.Cancellation.Cancel();
                state.InFlight?.Cancellation.Dispose();
                releaseLoadedOwner = state.Realization?.Outcome == ThumbnailRequestOutcome.Loaded;
                state.Generation++;
                generation = state.Generation;
                runtimeIdentity = state.RuntimeIdentity;
                identity = Guid.NewGuid();
                requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                state.Realization = new ThumbnailRealization(
                    generation,
                    identity,
                    sourcePath,
                    decodePixelWidth,
                    ThumbnailRequestOutcome.Loading);
                state.InFlight = new ThumbnailInFlight(generation, identity, requestCancellation);
            }

            if (releaseLoadedOwner)
            {
                _performanceProbe.LeaveDecodedStrongReferenceOwner(
                    ThumbnailDecodedOwnerKind.RealizedItemSource,
                    runtimeIdentity);
            }
            item.BeginThumbnailLoad(_thumbnailImageLoader.FallbackSource);
            _performanceProbe.RecordThumbnailRequest(
                runtimeIdentity,
                generation,
                sourcePath,
                decodePixelWidth);
            return LoadAndApplyThumbnailAsync(
                item,
                item.ThumbnailPath,
                sourcePath,
                decodePixelWidth,
                generation,
                identity,
                requestCancellation.Token);
        }

        public void ReleaseThumbnail(VideoItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            _dispatcher.VerifyAccess();

            long runtimeIdentity = 0;
            bool releaseLoadedOwner = false;
            lock (_thumbnailRuntimeLock)
            {
                if (_thumbnailRuntime.TryGetValue(item, out var state) &&
                    (state.Realization is not null || state.InFlight is not null))
                {
                    runtimeIdentity = state.RuntimeIdentity;
                    releaseLoadedOwner = state.Realization?.Outcome == ThumbnailRequestOutcome.Loaded;
                    state.Generation++;
                    state.InFlight?.Cancellation.Cancel();
                    state.InFlight?.Cancellation.Dispose();
                    state.InFlight = null;
                    state.Realization = null;
                }
            }

            if (releaseLoadedOwner)
            {
                _performanceProbe.LeaveDecodedStrongReferenceOwner(
                    ThumbnailDecodedOwnerKind.RealizedItemSource,
                    runtimeIdentity);
            }
            item.ReleaseThumbnail(_thumbnailImageLoader.FallbackSource);
        }

        internal long GetOrCreateThumbnailRuntimeIdentity(VideoItem item)
        {
            ArgumentNullException.ThrowIfNull(item);
            _dispatcher.VerifyAccess();
            lock (_thumbnailRuntimeLock)
            {
                return GetOrCreateThumbnailRuntimeState(item).RuntimeIdentity;
            }
        }

        internal ThumbnailRuntimeDiagnostics GetThumbnailRuntimeDiagnostics(VideoItem item)
        {
            lock (_thumbnailRuntimeLock)
            {
                if (!_thumbnailRuntime.TryGetValue(item, out var state))
                {
                    return ThumbnailRuntimeDiagnostics.Missing;
                }

                return new ThumbnailRuntimeDiagnostics(
                    true,
                    state.RuntimeIdentity,
                    state.Generation,
                    state.Realization?.Outcome.ToString(),
                    state.InFlight is not null,
                    state.Realization?.SourcePath,
                    state.Realization?.DecodePixelWidth ?? 0);
            }
        }

        internal int GetThumbnailInFlightCount()
        {
            lock (_thumbnailRuntimeLock)
            {
                return _thumbnailRuntime.Values.Count(state => state.InFlight is not null);
            }
        }

        internal int GetThumbnailRuntimeStateCount()
        {
            lock (_thumbnailRuntimeLock)
            {
                return _thumbnailRuntime.Count;
            }
        }

        internal int GetLoadedThumbnailItemSourceCount()
        {
            lock (_thumbnailRuntimeLock)
            {
                return _thumbnailRuntime.Count(pair =>
                    pair.Value.Realization?.Outcome == ThumbnailRequestOutcome.Loaded &&
                    pair.Key.ThumbnailSource is { } source &&
                    !ReferenceEquals(source, _thumbnailImageLoader.FallbackSource));
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_isInitialized)
            {
                return;
            }

            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            LibraryMutationLease? startupLease = null;
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetimeCts.Token);
            var startupToken = linkedCts.Token;
            var settingsReady = false;
            try
            {
                if (_isInitialized)
                {
                    return;
                }

                await _dispatcher.InvokeAsync(() =>
                {
                    startupLease = TryBeginLibraryMutation(LibraryMutationOwner.StartupScan)
                        ?? throw new InvalidOperationException("A library mutation is already active.");
                    StatusMessage = "Loading settings...";
                });

                var loadedSettings = await _settingsStore
                    .LoadAsync(startupToken)
                    .ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    _settings = loadedSettings;
                    PropertyChanged?.Invoke(this, new(nameof(LibraryFolderPaths)));
                    ApplyLoadedSort(loadedSettings.Sort);
                    StatusMessage = "Loading library...";
                });
                settingsReady = true;

                var loadedLibrary = await _libraryStore.LoadAsync(startupToken).ConfigureAwait(false);
                loadedLibrary = await RecoverPendingJournalAsync(loadedLibrary).ConfigureAwait(false);
                _performanceProbe.TryMark(StartupTimingMarker.LibraryLoaded);
                _startupPublishesStoredLibrary = loadedLibrary.Videos.Count > 0;
                AppLogger.Info($"Library loaded. Videos: {loadedLibrary.Videos.Count}.");

                await _dispatcher.InvokeAsync(() => SetStartupState(StartupLibraryState.Publishing));
                try
                {
                    await LoadLibraryDataAsync(
                        loadedLibrary,
                        startupLease!,
                        startupToken,
                        transitionToScanning: true,
                        endSkeletonsWhenEmpty: false).ConfigureAwait(false);
                }
                catch (Exception publishingException) when (!_lifetimeCts.IsCancellationRequested)
                {
                    await RecoverStoredLibraryAsync(startupLease!, publishingException).ConfigureAwait(false);
                    _isInitialized = true;
                    return;
                }

                await RunStartupScanAsync(startupLease!, startupToken).ConfigureAwait(false);
                _isInitialized = true;
            }
            catch (OperationCanceledException) when (!_lifetimeCts.IsCancellationRequested)
            {
                if (startupLease is not null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (StartupState != StartupLibraryState.Loading)
                        {
                            SetStartupState(StartupLibraryState.Ready);
                        }
                        ReleaseLibraryMutation(startupLease);
                    });
                }
                _isInitialized = false;
                throw;
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (!_lifetimeCts.IsCancellationRequested)
                {
                    var status = settingsReady
                        ? $"Library initialization failed: {ex.Message}"
                        : $"Settings initialization failed; library load and scan stopped: {ex.Message}";
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (StartupState == StartupLibraryState.Scanning)
                        {
                            SetStartupState(StartupLibraryState.ApplyingScan);
                        }
                        if (StartupState != StartupLibraryState.Faulted)
                        {
                            SetStartupState(StartupLibraryState.Faulted);
                        }
                        if (startupLease is not null)
                        {
                            ReleaseLibraryMutation(startupLease);
                        }
                        IsInitialLoading = false;
                        IsActorListLoading = false;
                        StatusMessage = status;
                    });
                    AppLogger.Error(
                        settingsReady
                            ? "Library initialization failed."
                            : "Settings initialization failed.",
                        ex);
                    _performanceProbe.TryMark(StartupTimingMarker.StartupTerminal);
                }
                throw;
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private async Task LoadLibraryDataAsync(
            LibraryData data,
            LibraryMutationLease startupLease,
            CancellationToken cancellationToken,
            bool transitionToScanning,
            bool endSkeletonsWhenEmpty,
            string resetKind = "StoredReset",
            string publishKind = "StoredPublish",
            string finalizeKind = "StoredFinalize")
        {
            if (!await TryInvokeLifetimeBoundDispatcherBatchAsync(resetKind, 0, () =>
            {
                EnsureCurrentLease(startupLease);
                _library = data;
                Videos.Clear();
                _videoIndex.Clear();
                Actors.Clear();
                Actors.Add(AllActorsLabel);
                SelectedVideo = null;
                IsInitialLoading = true;
                IsActorListLoading = true;
            }).ConfigureAwait(false))
            {
                throw new OperationCanceledException(_lifetimeCts.Token);
            }

            var entries = data.Videos;
            var actorNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < entries.Count; index += InitialLoadBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lifetimeCts.Token.ThrowIfCancellationRequested();

                var currentIndex = index;
                var batch = await Task.Run(
                    () => MapStoredBatch(entries, currentIndex, InitialLoadBatchSize, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                foreach (var actor in batch.SelectMany(item => item.Actors))
                {
                    actorNames.Add(actor);
                }

                var published = await TryInvokeLifetimeBoundDispatcherBatchAsync(
                    publishKind,
                    batch.Length,
                    () =>
                    {
                        EnsureCurrentLease(startupLease);
                        var firstNonEmptyBatch = batch.Length > 0 && IsInitialLoading;
                        if (firstNonEmptyBatch)
                        {
                            IsInitialLoading = false;
                        }
                        foreach (var item in batch)
                        {
                            RegisterVideo(item);
                            Videos.Add(item);
                        }

                        if (firstNonEmptyBatch)
                        {
                            SelectedVideo ??= Videos[0];
                            _performanceProbe.TryMark(StartupTimingMarker.FirstBatchPublished);
                        }

                        StatusMessage = $"Loading library... {Videos.Count}/{entries.Count}";
                    }).ConfigureAwait(false);
                if (!published)
                {
                    throw new OperationCanceledException(_lifetimeCts.Token);
                }
            }

            var actorSnapshot = await Task.Run(
                () => actorNames.OrderBy(actor => actor, StringComparer.OrdinalIgnoreCase).ToArray(),
                cancellationToken).ConfigureAwait(false);
            var finalized = await TryInvokeLifetimeBoundDispatcherBatchAsync(
                finalizeKind,
                entries.Count,
                () =>
                {
                    EnsureCurrentLease(startupLease);
                    Actors.Clear();
                    Actors.Add(AllActorsLabel);
                    foreach (var actor in actorSnapshot)
                    {
                        Actors.Add(actor);
                    }
                    FilteredVideos.Refresh();
                    SelectedVideo = Videos.FirstOrDefault();
                    if (entries.Count > 0 || endSkeletonsWhenEmpty)
                    {
                        IsInitialLoading = false;
                        IsActorListLoading = false;
                    }
                    _performanceProbe.TryMark(StartupTimingMarker.AllItemsPublished);
                    if (transitionToScanning)
                    {
                        SetStartupState(StartupLibraryState.Scanning);
                        StatusMessage = "Scanning library...";
                    }
                }).ConfigureAwait(false);
            if (!finalized)
            {
                throw new OperationCanceledException(_lifetimeCts.Token);
            }
        }

        private VideoItem[] MapStoredBatch(
            IReadOnlyList<VideoEntry> entries,
            int startIndex,
            int batchSize,
            CancellationToken cancellationToken)
        {
            var count = Math.Min(batchSize, entries.Count - startIndex);
            var batch = new VideoItem[count];
            for (var offset = 0; offset < count; offset++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                var entry = entries[startIndex + offset];
                batch[offset] = _storedVideoMapper?.Invoke(entry, cancellationToken) ?? MapVideo(entry);
                batch[offset].Id = entry.Id;
                batch[offset].Revision = entry.Revision;
            }
            return batch;
        }

        private async Task RecoverStoredLibraryAsync(
            LibraryMutationLease startupLease,
            Exception publishingException)
        {
            var recoveryStatus = publishingException is OperationCanceledException
                ? "Stored library publishing cancelled; persisted library restored."
                : "Stored library publishing failed; persisted library restored.";
            try
            {
                var persisted = await _libraryStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                await LoadLibraryDataAsync(
                    persisted,
                    startupLease,
                    _lifetimeCts.Token,
                    transitionToScanning: false,
                    endSkeletonsWhenEmpty: true).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    EnsureCurrentLease(startupLease);
                    StatusMessage = recoveryStatus;
                    SetStartupState(StartupLibraryState.Ready);
                    ReleaseLibraryMutation(startupLease);
                });
                AppLogger.Error(recoveryStatus, publishingException);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception recoveryException)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsInitialLoading = false;
                    IsActorListLoading = false;
                    StatusMessage = "Startup recovery failed. Restart required.";
                    SetStartupState(StartupLibraryState.Faulted);
                    ReleaseLibraryMutation(startupLease);
                });
                AppLogger.Error("Startup recovery failed. Restart required.", recoveryException);
            }
            finally
            {
                _performanceProbe.TryMark(StartupTimingMarker.StartupTerminal);
            }
        }

        private Task<bool> TryInvokeLifetimeBoundDispatcherBatchAsync(
            string kind,
            int itemCount,
            Action action)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_lifetimeSchedulingLock)
            {
                if (_lifetimeEnded)
                {
                    completion.SetResult(false);
                    return completion.Task;
                }

                _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                {
                    lock (_lifetimeSchedulingLock)
                    {
                        if (_lifetimeEnded)
                        {
                            completion.TrySetResult(false);
                            return;
                        }
                    }

                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        action();
                        _performanceProbe.RecordDispatcherBatch(
                            kind,
                            itemCount,
                            Stopwatch.GetTimestamp() - started);
                        completion.TrySetResult(true);
                    }
                    catch (Exception ex)
                    {
                        completion.TrySetException(ex);
                    }
                }));
            }
            return completion.Task;
        }

        private async Task RunStartupScanAsync(
            LibraryMutationLease startupLease,
            CancellationToken cancellationToken)
        {
            AppLogger.Info("Initiating initial library scan.");
            var commitStarted = false;
            try
            {
                var result = await _libraryScanner
                    .ScanAsync(_library, _settings!.Targets, cancellationToken)
                    .ConfigureAwait(false);
                ScanPreparationSeed? seed = null;
                if (!await TryInvokeLifetimeBoundDispatcherBatchAsync(
                    "ScanSeedCapture",
                    _library.Videos.Count,
                    () => seed = CaptureScanPreparationSeed(startupLease)).ConfigureAwait(false))
                {
                    return;
                }

                var plan = await Task.Run(
                    () => PrepareScanApplyPlan(result, seed!, cancellationToken),
                    cancellationToken).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    EnsureCurrentLease(startupLease);
                    SetStartupState(StartupLibraryState.SavingScan);
                });
                cancellationToken.ThrowIfCancellationRequested();
                await _libraryStore.SaveAsync(plan.FinalLibrary, CancellationToken.None).ConfigureAwait(false);
                if (!await TryInvokeLifetimeBoundDispatcherBatchAsync("ScanCommitStart", 0, () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    EnsureCurrentLease(startupLease);
                    SetStartupState(StartupLibraryState.ApplyingScan);
                    _library = plan.FinalLibrary;
                    commitStarted = true;
                }).ConfigureAwait(false))
                {
                    return;
                }

                var applied = 0;
                foreach (var batch in plan.Operations.Chunk(InitialLoadBatchSize))
                {
                    var operationBatch = batch.ToArray();
                    if (!await TryInvokeLifetimeBoundDispatcherBatchAsync(
                        "ScanApply",
                        operationBatch.Length,
                        () =>
                        {
                            EnsureCurrentLease(startupLease);
                            var firstNewBatch = !_startupPublishesStoredLibrary &&
                                IsInitialLoading &&
                                operationBatch.Any(operation => operation is AddScanItem);
                            if (firstNewBatch)
                            {
                                IsInitialLoading = false;
                            }
                            foreach (var operation in operationBatch)
                            {
                                ApplyScanOperation(operation);
                            }
                            if (firstNewBatch)
                            {
                                SelectedVideo ??= Videos.FirstOrDefault();
                                _performanceProbe.TryMark(StartupTimingMarker.FirstBatchPublished);
                            }
                            applied += operationBatch.Length;
                            StatusMessage = $"Applying scan results... {applied}/{plan.Operations.Count}";
                        }).ConfigureAwait(false))
                    {
                        return;
                    }
                }

                if (!await TryInvokeLifetimeBoundDispatcherBatchAsync("ScanFinalize", plan.Operations.Count, () =>
                {
                    EnsureCurrentLease(startupLease);
                    Actors.Clear();
                    Actors.Add(AllActorsLabel);
                    foreach (var actor in plan.ActorSnapshot)
                    {
                        Actors.Add(actor);
                    }
                    FilteredVideos.Refresh();
                    SelectedVideo = Videos.FirstOrDefault(video =>
                        ReferenceEquals(video, SelectedVideo)) ?? Videos.FirstOrDefault();
                    if (Videos.Count > 0)
                    {
                        IsInitialLoading = false;
                        IsActorListLoading = false;
                    }
                    RequestGalleryQuery(moveToTop: false);
                    _performanceProbe.TryMark(StartupTimingMarker.AllItemsPublished);
                }).ConfigureAwait(false))
                {
                    return;
                }

                if (!await TryInvokeLifetimeBoundDispatcherBatchAsync("StartupTerminal", 0, () =>
                {
                    EnsureCurrentLease(startupLease);
                    foreach (var path in plan.MetadataPaths)
                    {
                        EnqueueMetadataForProcessing(path);
                    }
                    IsInitialLoading = false;
                    IsActorListLoading = false;
                    ReleaseLibraryMutation(startupLease);
                    var autoMetadataLease = !_metadataShutdown.IsCancellationRequested && HasPendingMetadata()
                        ? TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata) : null;
                    SetStartupState(StartupLibraryState.Ready);
                    StatusMessage = $"Scan complete: {plan.AddedCount} added, {plan.MissingCount} missing, {plan.UpdatedCount} updated.";
                    if (autoMetadataLease is not null) StartMetadataProcessing(autoMetadataLease);
                }).ConfigureAwait(false))
                {
                    return;
                }

                AppLogger.Info(
                    $"Scan apply summary: {plan.AddedCount} added, {plan.MissingCount} missing, {plan.UpdatedCount} updated.");
                _performanceProbe.TryMark(StartupTimingMarker.StartupTerminal);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (commitStarted)
                {
                    await RecoverStartupScanAsync(startupLease, ex).ConfigureAwait(false);
                }
                else
                {
                    await CompleteStartupWithoutScanCommitAsync(startupLease, ex).ConfigureAwait(false);
                }
            }
        }

        private async Task CompleteStartupWithoutScanCommitAsync(
            LibraryMutationLease startupLease,
            Exception exception)
        {
            var status = exception is OperationCanceledException
                ? "Scan cancelled; stored library remains unchanged."
                : $"Scan failed; stored library remains unchanged: {exception.Message}";
            await _dispatcher.InvokeAsync(() =>
            {
                EnsureCurrentLease(startupLease);
                IsInitialLoading = false;
                IsActorListLoading = false;
                StatusMessage = status;
                SetStartupState(StartupLibraryState.Ready);
                ReleaseLibraryMutation(startupLease);
            });
            AppLogger.Error(status, exception);
            _performanceProbe.TryMark(StartupTimingMarker.StartupTerminal);
        }

        private async Task RecoverStartupScanAsync(
            LibraryMutationLease startupLease,
            Exception exception)
        {
            var status = exception is OperationCanceledException
                ? "Scan commit cancelled; persisted library restored."
                : $"Scan commit failed; persisted library restored: {exception.Message}";
            try
            {
                var persisted = await _libraryStore.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                await LoadLibraryDataAsync(
                    persisted,
                    startupLease,
                    _lifetimeCts.Token,
                    transitionToScanning: false,
                    endSkeletonsWhenEmpty: true,
                    resetKind: "StartupRecoveryReset",
                    publishKind: "StartupRecovery",
                    finalizeKind: "RecoveryFinalize").ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                {
                    EnsureCurrentLease(startupLease);
                    StatusMessage = status;
                    SetStartupState(StartupLibraryState.Ready);
                    ReleaseLibraryMutation(startupLease);
                });
                AppLogger.Error(status, exception);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception recoveryException)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsInitialLoading = false;
                    IsActorListLoading = false;
                    StatusMessage = "Startup recovery failed. Restart required.";
                    SetStartupState(StartupLibraryState.Faulted);
                    ReleaseLibraryMutation(startupLease);
                });
                AppLogger.Error("Startup recovery failed. Restart required.", recoveryException);
            }
            finally
            {
                _performanceProbe.TryMark(StartupTimingMarker.StartupTerminal);
            }
        }

        internal ScanPreparationSeed CaptureScanPreparationSeed(LibraryMutationLease startupLease)
        {
            _dispatcher.VerifyAccess();
            EnsureCurrentLease(startupLease);
            return new ScanPreparationSeed(
                _library.Videos.ToArray(),
                _videoIndex.ToArray(),
                _library.WithVideos(_library.Videos.Select(CloneEntry)));
        }

        internal ScanApplyPlan PrepareScanApplyPlan(
            LibraryScanResult result,
            ScanPreparationSeed seed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var videos = seed.VideoReferences.Select(CloneEntry).ToArray();
            var itemIdentities = seed.ItemIdentitiesById.ToDictionary(pair => pair.Key, pair => pair.Value);
            var existing = new Dictionary<Guid, ExistingScanItemSnapshot>();
            foreach (var entry in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = LibraryPathHelper.NormalizeLibraryPath(entry.Path);
                if (itemIdentities.TryGetValue(entry.Id, out var item))
                {
                    existing[entry.Id] = new ExistingScanItemSnapshot(normalized, entry, item);
                }
            }
            var preparation = new ScanPreparationSnapshot(
                Array.AsReadOnly(videos),
                new System.Collections.ObjectModel.ReadOnlyDictionary<Guid, ExistingScanItemSnapshot>(existing));

            var snapshots = result.Snapshots.ToDictionary(
                snapshot => LibraryPathHelper.NormalizeLibraryPath(snapshot.LibraryPath),
                StringComparer.OrdinalIgnoreCase);
            var finalVideos = new List<VideoEntry>(preparation.Videos.Count + result.NewFiles.Count);
            var operations = new List<ScanApplyOperation>(preparation.Videos.Count + result.NewFiles.Count);
            foreach (var entry in preparation.Videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = LibraryPathHelper.NormalizeLibraryPath(entry.Path);
                if (!preparation.ExistingById.TryGetValue(entry.Id, out var existingItem) ||
                    seed.Envelope?.IsInRecycleBin(entry.Id) == true)
                {
                    finalVideos.Add(entry);
                    continue;
                }

                if (snapshots.TryGetValue(normalized, out var snapshot))
                {
                    var createdUtc = snapshot.CreatedUtc != default ? snapshot.CreatedUtc : entry.CreatedUtc;
                    var updatedEntry = entry with
                    {
                        SizeBytes = snapshot.SizeBytes,
                        LastModifiedUtc = snapshot.LastWriteUtc,
                        CreatedUtc = createdUtc,
                        Presence = LibraryFilePresence.Available,
                        PresenceProbedAtUtc = DateTime.UtcNow
                    };
                    finalVideos.Add(updatedEntry);
                    operations.Add(new UpdateScanItem(
                        existingItem.ItemIdentity,
                        snapshot.AbsolutePath,
                        snapshot.SizeBytes,
                        snapshot.LastWriteUtc,
                        createdUtc,
                        VideoPresenceState.Available));
                }
                else
                {
                    finalVideos.Add(entry with { Presence = LibraryFilePresence.Missing, PresenceProbedAtUtc = DateTime.UtcNow });
                    operations.Add(new UpdateScanItem(
                        existingItem.ItemIdentity,
                        LibraryPathHelper.ResolveToAbsolute(entry.Path),
                        entry.SizeBytes,
                        entry.LastModifiedUtc,
                        entry.CreatedUtc,
                        VideoPresenceState.Missing));
                }
            }

            var metadataPaths = new List<string>(result.NewFiles.Count);
            foreach (var snapshot in result.NewFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (finalVideos.Any(existingEntry => string.Equals(
                    LibraryPathHelper.NormalizeLibraryPath(existingEntry.Path),
                    LibraryPathHelper.NormalizeLibraryPath(snapshot.LibraryPath), StringComparison.OrdinalIgnoreCase)))
                    continue;
                var entry = CreateEntryFromSnapshot(snapshot);
                var item = MapVideo(entry);
                finalVideos.Add(CloneEntry(entry));
                operations.Add(new AddScanItem(entry, item, entry.Path));
                metadataPaths.Add(entry.Path);
            }

            var finalLibrary = (seed.Envelope ?? new LibraryData()).WithVideos(finalVideos.Select(CloneEntry));
            var actors = finalLibrary.Videos
                .SelectMany(entry => entry.Meta.Actors)
                .Where(actor => !string.IsNullOrWhiteSpace(actor))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(actor => actor, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ScanApplyPlan(
                finalLibrary,
                operations,
                metadataPaths,
                actors,
                result.NewFiles.Count,
                result.MissingEntries.Count,
                result.UpdatedEntries.Count);
        }

        private static VideoEntry CloneEntry(VideoEntry entry) => entry with
        {
            Meta = entry.Meta with
            {
                Actors = entry.Meta.Actors.ToArray(),
                Tags = entry.Meta.Tags.ToArray(),
                Genres = entry.Meta.Genres.ToArray(),
                ManualFieldLocks = entry.Meta.ManualFieldLocks.ToArray(),
                AdditionalData = entry.Meta.AdditionalData is null ? null : new(entry.Meta.AdditionalData)
            }
        };

        private void ApplyScanOperation(ScanApplyOperation operation)
        {
            _dispatcher.VerifyAccess();
            switch (operation)
            {
                case UpdateScanItem update:
                    update.Item.UpdateFileState(
                        update.AbsolutePath,
                        update.SizeBytes,
                        update.LastWriteUtc,
                        update.Presence,
                        update.CreatedUtc);
                    break;
                case AddScanItem add:
                    RegisterVideo(add.Item);
                    Videos.Add(add.Item);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown scan operation: {operation.GetType().Name}.");
            }
        }

        private async Task ProcessMetadataQueueAsync(LibraryMutationLease lease, CancellationToken token)
        {
            if (lease.Owner != LibraryMutationOwner.AutoMetadata)
            {
                throw new InvalidOperationException("Metadata queue processing requires an AutoMetadata lease.");
            }

            try
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    token.ThrowIfCancellationRequested();
                    EnsureCurrentLease(lease);
                });
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string normalizedPath;
                    lock (_metadataQueueLock)
                    {
                        if (_pendingMetadata.Count == 0)
                        {
                            break;
                        }

                        normalizedPath = _pendingMetadata.Dequeue();
                        _metadataScheduled.Remove(normalizedPath);
                    }

                    await ProcessMetadataForPathAsync(normalizedPath, lease, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            finally
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    ReleaseLibraryMutation(lease);
                    if (!_disposed) StatusMessage = BuildLibrarySummary();
                });
            }
        }

        private async Task ProcessMetadataForPathAsync(string normalizedPath, LibraryMutationLease lease, CancellationToken token)
        {
            VideoEntry[] entries = await _dispatcher.InvokeAsync(() => _library.Videos.Where(entry =>
                LibraryPathHelper.NormalizeLibraryPath(entry.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)).ToArray());
            foreach (var entry in entries) await ProcessMetadataForEntryAsync(entry, normalizedPath, lease, token).ConfigureAwait(false);
        }

        private async Task ProcessMetadataForEntryAsync(VideoEntry entry, string normalizedPath, LibraryMutationLease lease, CancellationToken token)
        {
            var displayName = Path.GetFileName(LibraryPathHelper.ResolveToAbsolute(normalizedPath));

            await _dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"{displayName} · 메타데이터 갱신 중…";
            });

            try
            {
                var result = await _webMetadataService.FetchEnrichmentAsync(entry, token).ConfigureAwait(false);
                if (result.Entry is { } updatedEntry)
                {
                    await CommitEntryCandidateAsync(entry, updatedEntry, lease, token).ConfigureAwait(false);
                }
                await _dispatcher.InvokeAsync(() => StatusMessage = $"{displayName} · {MetadataOutcomeLabel(result.Status)}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Metadata enrichment failed for {normalizedPath}", ex);
            }
        }

        private void ApplyMetadataToCollections(VideoEntry updatedEntry)
        {
            if (_videoIndex.TryGetValue(updatedEntry.Id, out var existing))
            {
                var oldThumbnail = existing.ThumbnailPath;
                var realizedWidth = GetRealizedThumbnailWidth(existing);
                existing.Revision = updatedEntry.Revision;
                existing.Title = updatedEntry.Meta.Korean?.Title ?? updatedEntry.Meta.Title;
                existing.ProductCode = updatedEntry.ProductCode;
                existing.ReleaseDate = updatedEntry.Meta.Date;
                existing.RawDate = updatedEntry.Meta.RawDate;
                existing.Actors = updatedEntry.Meta.Actors;
                existing.GenreFilterValues = updatedEntry.Meta.Genres;
                existing.DisplayActors = MetadataDisplay.Actors(updatedEntry.Meta).Select(actor => actor.DisplayName).ToArray();
                existing.Tags = updatedEntry.Meta.Tags.Select(tag => KoreanMetadata.Label(updatedEntry.Meta.Korean?.Tags, tag)).ToArray();
                existing.Description = updatedEntry.Meta.Korean?.Description ?? updatedEntry.Meta.Description;
                existing.Studio = updatedEntry.Meta.Studio;
                existing.Genres = updatedEntry.Meta.Genres.Select(genre => KoreanMetadata.Label(updatedEntry.Meta.Korean?.Genres, genre)).ToArray();
                existing.ThumbnailPath = updatedEntry.Meta.Thumbnail;
                existing.ThumbnailUri = ResolveThumbnailPath(updatedEntry.Meta.Thumbnail);
                if (!StringComparer.OrdinalIgnoreCase.Equals(oldThumbnail, existing.ThumbnailPath))
                {
                    ReleaseThumbnail(existing);
                    if (realizedWidth > 0) _ = RequestThumbnailAsync(existing, realizedWidth);
                }
            }
            else
            {
                var mapped = MapVideo(updatedEntry);
                Videos.Add(mapped);
                RegisterVideo(mapped);
            }

            RefreshActorList();
            FilteredVideos.Refresh();
        }

        private void EnqueueMetadataForProcessing(string path)
        {
            var normalized = LibraryPathHelper.NormalizeLibraryPath(path);

            lock (_metadataQueueLock)
            {
                if (_metadataScheduled.Add(normalized))
                {
                    _pendingMetadata.Enqueue(normalized);
                }
            }
        }

        private void RemoveFromMetadataQueue(string normalizedPath)
        {
            lock (_metadataQueueLock)
            {
                if (_pendingMetadata.Count == 0)
                {
                    _metadataScheduled.Remove(normalizedPath);
                    return;
                }

                var remaining = new Queue<string>(_pendingMetadata.Where(p => !p.Equals(normalizedPath, StringComparison.OrdinalIgnoreCase)));
                _pendingMetadata.Clear();
                foreach (var item in remaining)
                {
                    _pendingMetadata.Enqueue(item);
                }

                _metadataScheduled.Remove(normalizedPath);
            }
        }

        private bool HasPendingMetadata()
        {
            lock (_metadataQueueLock)
            {
                return _pendingMetadata.Count > 0;
            }
        }

        private void StartMetadataProcessing(LibraryMutationLease lease)
        {
            _dispatcher.VerifyAccess();
            var token = _metadataShutdown.Token;
            if (token.IsCancellationRequested)
            {
                ReleaseLibraryMutation(lease);
                return;
            }
            if (!_metadataProcessingTask.IsCompleted)
                throw new InvalidOperationException("Metadata processing is already active.");
            _metadataProcessingTask = Task.Run(() => ProcessMetadataQueueAsync(lease, token));
        }

        internal Task StopMetadataForShutdownAsync()
        {
            _dispatcher.VerifyAccess();
            _metadataShutdown.Cancel();
            return _metadataProcessingTask;
        }

        private void ApplyLoadedSort(SortSettings sort)
        {
            var selected = SortOptions.First(option =>
                option.Field.ToString() == sort.Field &&
                option.Direction.ToString() == sort.Direction);
            _selectedSortOption = selected;
            ApplySort(selected.Field, selected.Direction, updateStatus: false);
            PropertyChanged?.Invoke(
                this,
                new PropertyChangedEventArgs(nameof(SelectedSortOption)));
        }

        private async Task SaveSortSettingsAsync(AppSettings settings)
        {
            try
            {
                await _settingsStore
                    .SaveAsync(settings, _lifetimeCts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                    StatusMessage = $"Sort settings save failed: {ex.Message}");
                AppLogger.Error("Sort settings save failed.", ex);
            }
        }

        private void ApplySort(SortField field, ListSortDirection direction, bool updateStatus = true)
        {
            if (SortOptions is not null && _selectedSortOption is not null) RequestGalleryQuery();
        }

        private void PlayRandomVideo()
        {
            var candidates = FilteredVideos.Cast<VideoItem>()
                .Where(v => v.Presence == VideoPresenceState.Available && !v.IsInRecycleBin && !IsVideoDeletionTarget(v))
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var choice = candidates[_random.Next(candidates.Count)];
            SelectedVideo = choice;
            StatusMessage = $"Random pick: {choice.Title}";
            PlayVideoRequested?.Invoke(choice);
        }
        public async Task ApplyMetadataEditAsync(VideoItem item, MetadataEditResult result)
        {
            LibraryMutationLease? lease = null;
            await _dispatcher.InvokeAsync(() => lease = TryBeginMetadataEditorMutation());
            if (lease is null)
            {
                return;
            }

            try
            {
                await ApplyMetadataEditAsync(item, result, lease).ConfigureAwait(false);
            }
            finally
            {
                await _dispatcher.InvokeAsync(() => EndMetadataEditorMutation(lease));
            }
        }

        internal async Task ApplyMetadataEditAsync(
            VideoItem item,
            MetadataEditResult result,
            LibraryMutationLease lease)
        {
            if (item is null)
            {
                return;
            }
            if (lease.Owner != LibraryMutationOwner.EditorSave)
            {
                throw new InvalidOperationException("Metadata edits require an EditorSave lease.");
            }

            var entry = await _dispatcher.InvokeAsync(() =>
            {
                EnsureCurrentLease(lease);
                return _library.Videos.SingleOrDefault(entry => entry.Id == item.Id) ?? throw new InvalidOperationException("편집 대상이 없습니다.");
            });
            var updatedMeta = entry.Meta with
            {
                Title = result.Title?.Trim() ?? string.Empty,
                Date = result.ReleaseDate,
                RawDate = null,
                Actors = result.Actors ?? Array.Empty<string>(),
                Tags = result.Tags ?? Array.Empty<string>(),
                Description = result.Description?.Trim() ?? string.Empty,
                Thumbnail = result.ThumbnailPath ?? string.Empty
            };
            var changed = Enum.GetValues<MetadataField>().Where(field => !MetadataValuesEqual(entry.Meta, updatedMeta, field)).ToArray();
            var authored = updatedMeta with { Korean = null, ActorNames = Array.Empty<ActorName>() };
            foreach (var field in changed) updatedMeta = GalleryMetadataFields.CopyField(updatedMeta, authored, field);
            updatedMeta = updatedMeta with { ManualFieldLocks = entry.Meta.ManualFieldLocks.Concat(changed).Distinct().ToArray() };
            await CommitEntryCandidateAsync(entry, entry with { Meta = updatedMeta }, lease).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => StatusMessage = $"'{updatedMeta.Title}' 정보를 저장했습니다.");
        }

        private static bool MetadataValuesEqual(VideoMeta left, VideoMeta right, MetadataField field) => field switch
        {
            MetadataField.Title => left.Title == right.Title,
            MetadataField.Date => left.Date == right.Date && left.RawDate == right.RawDate,
            MetadataField.Actors => left.Actors.SequenceEqual(right.Actors),
            MetadataField.Tags => left.Tags.SequenceEqual(right.Tags),
            MetadataField.Thumbnail => left.Thumbnail == right.Thumbnail,
            MetadataField.Description => left.Description == right.Description,
            MetadataField.Studio => left.Studio == right.Studio,
            MetadataField.Genres => left.Genres.SequenceEqual(right.Genres),
            _ => false
        };

        internal async Task CommitEntryCandidateAsync(VideoEntry before, VideoEntry proposal, LibraryMutationLease lease,
            CancellationToken cancellationToken = default)
        {
            LibraryData candidate;
            VideoEntry confirmed;
            try
            {
                confirmed = proposal with { Id = before.Id, Revision = checked(before.Revision + 1) };
                candidate = await _dispatcher.InvokeAsync(() =>
                {
                    EnsureCurrentLease(lease);
                    var current = _library.Videos.SingleOrDefault(entry => entry.Id == before.Id);
                    if (current is null || current.Revision != before.Revision)
                        throw new InvalidOperationException("대상 정보가 변경되었습니다. 미리보기를 다시 확인해 주세요.");
                    return _library.WithVideos(_library.Videos.Select(entry => entry.Id == before.Id ? confirmed : entry));
                });
                _lifetimeCts.Token.ThrowIfCancellationRequested();
                cancellationToken.ThrowIfCancellationRequested();
                await _libraryStore.SaveAsync(candidate, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _webMetadataService.DiscardCandidate(proposal);
                throw;
            }
            _webMetadataService.AcceptCandidate(proposal);
            await _dispatcher.InvokeAsync(() =>
            {
                EnsureCurrentLease(lease);
                _library = candidate!;
                ApplyMetadataToCollections(confirmed);
                RequestGalleryQuery(moveToTop: false);
                RefreshActorList();
                FilteredVideos.Refresh();
                UpdateStatus();
            });
        }

        private bool FilterVideo(object? obj) => obj is VideoItem video &&
            (!_hasGalleryResult || _galleryResultIds.Contains(video.Id));

        private void ClearSearch()
        {
            if (string.IsNullOrWhiteSpace(SearchQuery) && string.Equals(SelectedActor, AllActorsLabel, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            SelectedActor = AllActorsLabel;
            SearchQuery = string.Empty;
        }

        private void UpdateStatus(bool updateMessage = true)
        {
            RandomPlayCommand.RaiseCanExecuteChanged();

            if (!updateMessage || IsScanning || IsFetchingMetadata)
            {
                return;
            }

            StatusMessage = BuildLibrarySummary();
        }

        private void OnVideosCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems is not null)
            {
                foreach (var oldItem in e.OldItems.OfType<VideoItem>())
                {
                    RemoveThumbnailRuntimeState(oldItem);
                }
            }
            else if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                VideoItem[] removed;
                lock (_thumbnailRuntimeLock)
                {
                    removed = _thumbnailRuntime.Keys.Where(item => !Videos.Contains(item)).ToArray();
                }
                foreach (var item in removed)
                {
                    RemoveThumbnailRuntimeState(item);
                }
            }

            _gallerySnapshotDirty = true;
            if (e.NewItems is not null) foreach (VideoItem item in e.NewItems) ConfigureGalleryItem(item);
            if (e.OldItems is not null) foreach (VideoItem item in e.OldItems) item.PropertyChanged -= GalleryItemChanged;
            RequestGalleryQuery(moveToTop: false);
            NotifyGallerySelection();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowVideoSkeleton)));
        }

        private void OnActorsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowActorSkeleton)));
        }

        private string BuildLibrarySummary()
        {
            var total = Videos.Count;
            var missing = Videos.Count(IsMetadataIncomplete);
            return $"All videos updated. {total} videos. Missing metadata {missing}.";
        }

        private bool IsMetadataIncomplete(VideoItem item)
        {
            var missingThumbnail = string.IsNullOrWhiteSpace(item.ThumbnailUri) || string.Equals(item.ThumbnailUri, _fallbackThumbnailUri, StringComparison.OrdinalIgnoreCase);
            return missingThumbnail;
        }

        private VideoItem MapVideo(VideoEntry entry)
        {
            var libraryPath = LibraryPathHelper.NormalizeLibraryPath(entry.Path);
            var absolutePath = LibraryPathHelper.ResolveToAbsolute(libraryPath);
            var lastModified = entry.LastModifiedUtc.Kind == DateTimeKind.Utc
                ? entry.LastModifiedUtc
                : entry.LastModifiedUtc == default
                    ? DateTime.MinValue
                    : DateTime.SpecifyKind(entry.LastModifiedUtc, DateTimeKind.Utc);

            var createdUtc = entry.CreatedUtc;
            if (createdUtc != default && createdUtc.Kind != DateTimeKind.Utc)
            {
                createdUtc = DateTime.SpecifyKind(createdUtc, DateTimeKind.Utc);
            }

            if (createdUtc == default)
            {
                createdUtc = lastModified;
            }

            var item = new VideoItem
            {
                LibraryPath = libraryPath,
                Id = entry.Id,
                ProductCode = entry.ProductCode,
                Revision = entry.Revision,
                Studio = entry.Meta.Studio,
                Genres = entry.Meta.Genres.Select(genre => KoreanMetadata.Label(entry.Meta.Korean?.Genres, genre)).ToArray(),
                GenreFilterValues = entry.Meta.Genres,
                RawDate = entry.Meta.RawDate,
                Title = entry.Meta.Korean?.Title ?? entry.Meta.Title,
                ReleaseDate = entry.Meta.Date,
                Actors = entry.Meta.Actors,
                DisplayActors = MetadataDisplay.Actors(entry.Meta).Select(actor => actor.DisplayName).ToArray(),
                Tags = entry.Meta.Tags.Select(tag => KoreanMetadata.Label(entry.Meta.Korean?.Tags, tag)).ToArray(),
                Description = entry.Meta.Korean?.Description ?? entry.Meta.Description,
                ThumbnailUri = ResolveThumbnailPath(entry.Meta.Thumbnail),
                ThumbnailPath = entry.Meta.Thumbnail
            };

            ApplyJournalToItem(item, _library.LatestOperation(entry.Id));
            item.ReleaseThumbnail(_thumbnailImageLoader.FallbackSource);
            item.UpdateFileState(absolutePath, entry.SizeBytes, lastModified, entry.Presence switch
            { LibraryFilePresence.Available => VideoPresenceState.Available, LibraryFilePresence.Missing => VideoPresenceState.Missing, _ => VideoPresenceState.Checking }, createdUtc);
            return item;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _dispatcher.VerifyAccess();
            _disposed = true;
            lock (_lifetimeSchedulingLock)
            {
                _lifetimeEnded = true;
            }
            _toastTimer?.Stop();
            _draftFetchCts?.Cancel();
            _editorDraft?.Dispose();
            ReleaseDraftProposals();
            _editorDraft = null;
            _galleryQueryCancellation?.Cancel();
            _galleryQueryCompletion.TrySetCanceled();
            _metadataShutdown.Cancel();
            _lifetimeCts.Cancel();
            _mutationLease = null;
            RaiseMutationStateChanged();
            Videos.CollectionChanged -= OnVideosCollectionChanged;
            Actors.CollectionChanged -= OnActorsCollectionChanged;

            VideoItem[] items;
            lock (_thumbnailRuntimeLock)
            {
                items = _thumbnailRuntime.Keys.ToArray();
            }
            foreach (var item in items)
            {
                RemoveThumbnailRuntimeState(item);
            }
        }

        private async Task LoadAndApplyThumbnailAsync(
            VideoItem item,
            string originalThumbnailPath,
            string sourcePath,
            int decodePixelWidth,
            long generation,
            Guid identity,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await _thumbnailImageLoader.LoadAsync(
                    originalThumbnailPath,
                    decodePixelWidth,
                    cancellationToken).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() =>
                    CompleteThumbnailRequest(
                        item,
                        sourcePath,
                        decodePixelWidth,
                        generation,
                        identity,
                        result));
            }
            catch (OperationCanceledException)
            {
                await _dispatcher.InvokeAsync(() =>
                    CompleteThumbnailCancellation(
                        item,
                        sourcePath,
                        decodePixelWidth,
                        generation,
                        identity));
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected thumbnail request failure.", ex);
                await _dispatcher.InvokeAsync(() =>
                    CompleteThumbnailRequest(
                        item,
                        sourcePath,
                        decodePixelWidth,
                        generation,
                        identity,
                        new ThumbnailImageResult(_thumbnailImageLoader.FallbackSource, true)));
            }
        }

        private void CompleteThumbnailRequest(
            VideoItem item,
            string sourcePath,
            int decodePixelWidth,
            long generation,
            Guid identity,
            ThumbnailImageResult result)
        {
            _dispatcher.VerifyAccess();
            lock (_thumbnailRuntimeLock)
            {
                if (!TryGetCurrentThumbnailRequest(
                        item, sourcePath, decodePixelWidth, generation, identity,
                        out var state, out var realization, out var slot))
                {
                    return;
                }

                if (result.IsFallback)
                {
                    item.FailThumbnailLoad(_thumbnailImageLoader.FallbackSource);
                    state.Realization = realization with { Outcome = ThumbnailRequestOutcome.Failed };
                }
                else
                {
                    item.CompleteThumbnailLoad(result.Source);
                    state.Realization = realization with { Outcome = ThumbnailRequestOutcome.Loaded };
                    _performanceProbe.EnterDecodedStrongReferenceOwner(
                        ThumbnailDecodedOwnerKind.RealizedItemSource,
                        state.RuntimeIdentity);
                    _performanceProbe.TryMark(StartupTimingMarker.FirstThumbnailApplied);
                }

                slot.Cancellation.Dispose();
                state.InFlight = null;
            }
        }

        private void CompleteThumbnailCancellation(
            VideoItem item,
            string sourcePath,
            int decodePixelWidth,
            long generation,
            Guid identity)
        {
            _dispatcher.VerifyAccess();
            lock (_thumbnailRuntimeLock)
            {
                if (!TryGetCurrentThumbnailRequest(
                        item, sourcePath, decodePixelWidth, generation, identity,
                        out var state, out var realization, out var slot))
                {
                    return;
                }

                item.ReleaseThumbnail(_thumbnailImageLoader.FallbackSource);
                state.Realization = realization with { Outcome = ThumbnailRequestOutcome.Cancelled };
                slot.Cancellation.Dispose();
                state.InFlight = null;
            }
        }

        private bool TryGetCurrentThumbnailRequest(
            VideoItem item,
            string sourcePath,
            int decodePixelWidth,
            long generation,
            Guid identity,
            out ThumbnailRuntimeState state,
            out ThumbnailRealization realization,
            out ThumbnailInFlight slot)
        {
            if (_thumbnailRuntime.TryGetValue(item, out state!) &&
                state.Generation == generation &&
                state.Realization is { } currentRealization &&
                currentRealization.Generation == generation &&
                currentRealization.Identity == identity &&
                StringComparer.OrdinalIgnoreCase.Equals(currentRealization.SourcePath, sourcePath) &&
                currentRealization.DecodePixelWidth == decodePixelWidth &&
                state.InFlight is { } currentSlot &&
                currentSlot.Generation == generation &&
                currentSlot.Identity == identity &&
                item.ThumbnailLoadState == ThumbnailLoadState.Loading)
            {
                realization = currentRealization;
                slot = currentSlot;
                return true;
            }

            realization = null!;
            slot = null!;
            return false;
        }

        private ThumbnailRuntimeState GetOrCreateThumbnailRuntimeState(VideoItem item)
        {
            if (_thumbnailRuntime.TryGetValue(item, out var state))
            {
                return state;
            }

            state = new ThumbnailRuntimeState
            {
                RuntimeIdentity = Interlocked.Increment(ref _nextThumbnailRuntimeIdentity)
            };
            _thumbnailRuntime.Add(item, state);
            return state;
        }

        private void RemoveThumbnailRuntimeState(VideoItem item)
        {
            ThumbnailInFlight? slot = null;
            long runtimeIdentity = 0;
            bool releaseLoadedOwner = false;
            lock (_thumbnailRuntimeLock)
            {
                if (_thumbnailRuntime.Remove(item, out var state))
                {
                    slot = state.InFlight;
                    runtimeIdentity = state.RuntimeIdentity;
                    releaseLoadedOwner = state.Realization?.Outcome == ThumbnailRequestOutcome.Loaded;
                }
            }

            slot?.Cancellation.Cancel();
            slot?.Cancellation.Dispose();
            if (releaseLoadedOwner)
            {
                _performanceProbe.LeaveDecodedStrongReferenceOwner(
                    ThumbnailDecodedOwnerKind.RealizedItemSource,
                    runtimeIdentity);
            }
            item.ReleaseThumbnail(_thumbnailImageLoader.FallbackSource);
        }

        private int GetRealizedThumbnailWidth(VideoItem item)
        {
            lock (_thumbnailRuntimeLock)
            {
                return _thumbnailRuntime.TryGetValue(item, out var state)
                    ? state.Realization?.DecodePixelWidth ?? 0
                    : 0;
            }
        }

        private static string NormalizeThumbnailSourcePath(string thumbnailPath)
        {
            if (string.IsNullOrWhiteSpace(thumbnailPath))
            {
                return string.Empty;
            }

            if (Uri.TryCreate(thumbnailPath.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile)
            {
                return "uri:" + thumbnailPath.Trim().ToUpperInvariant();
            }

            try
            {
                return Path.GetFullPath(LibraryPathHelper.ResolveToAbsolute(thumbnailPath));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return "path:" + thumbnailPath.Trim();
            }
        }

        private VideoEntry CreateEntryFromSnapshot(FileSnapshot snapshot)
        {
            var title = Path.GetFileNameWithoutExtension(snapshot.LibraryPath);
            var meta = new VideoMeta(
                string.IsNullOrWhiteSpace(title) ? "Untitled" : title,
                null,
                Array.Empty<string>(),
                string.Empty,
                Array.Empty<string>(),
                string.Empty);

            var entry = new VideoEntry(snapshot.LibraryPath, meta, snapshot.SizeBytes, snapshot.LastWriteUtc, snapshot.CreatedUtc)
            { Presence = LibraryFilePresence.Available, PresenceProbedAtUtc = DateTime.UtcNow };
            var code = MetadataProductCode.FromEntry(entry)?.Value;
            return code is null ? entry : entry with { ProductCode = code, Meta = meta with { Title = string.Empty } };
        }

        private string ResolveThumbnailPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var candidate = LibraryPathHelper.ResolveToAbsolute(path);
                if (_thumbnailUriCache.TryGetValue(candidate, out var cachedUri))
                {
                    return cachedUri;
                }

                if (File.Exists(candidate))
                {
                    var uri = new Uri(candidate).AbsoluteUri;
                    _thumbnailUriCache[candidate] = uri;
                    return uri;
                }
            }

            return _fallbackThumbnailUri;
        }

        private string GetFallbackThumbnailUri()
        {
            var fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "noimage.jpg");
            return File.Exists(fallbackPath) ? new Uri(fallbackPath).AbsoluteUri : string.Empty;
        }

        private void RegisterVideo(VideoItem item)
        {
            _videoIndex[item.Id] = item;
        }

        private void RefreshActorList()
        {
            var snapshot = BuildActorList(Videos).ToList();
            var previousSelection = SelectedActor;

            Actors.Clear();
            foreach (var actor in snapshot)
            {
                Actors.Add(actor);
            }

            if (!snapshot.Any(actor => actor.Equals(previousSelection, StringComparison.OrdinalIgnoreCase)))
            {
                SelectedActor = AllActorsLabel;
            }
        }

        private static IEnumerable<string> BuildActorList(IEnumerable<VideoItem> videos)
        {
            yield return AllActorsLabel;

            foreach (var actor in videos
                         .SelectMany(v => v.Actors)
                         .Where(name => !string.IsNullOrWhiteSpace(name))
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            {
                yield return actor;
            }
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            return true;
        }
    }
}




