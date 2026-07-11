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
        ManualFetch,
        EditorSave
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
    public class MainViewModel : INotifyPropertyChanged, IDisposable
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
        private readonly ILibraryScanner _libraryScanner;
        private readonly WebMetadataService _webMetadataService;
        private readonly Dispatcher _dispatcher;
        private readonly CrawlerSessionProvider _crawlerSessionProvider;
        private readonly OneFourOneJavMetaSource _oneFourOneJavSource;
        private readonly IOneFourOneJavCrawlerSessionFactory _crawlerSessionFactory;
        private readonly ThumbnailPerformanceProbe _performanceProbe;
        private readonly IThumbnailImageLoader _thumbnailImageLoader;
        private readonly Func<VideoEntry, CancellationToken, VideoItem>? _storedVideoMapper;
        private readonly object _lifetimeSchedulingLock = new();
        private bool _lifetimeEnded;
        private readonly Dictionary<string, VideoItem> _videoIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _metadataQueueLock = new();
        private readonly Queue<string> _pendingMetadata = new();
        private readonly HashSet<string> _metadataScheduled = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, string> _thumbnailUriCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _thumbnailRuntimeLock = new();
        private readonly Dictionary<VideoItem, ThumbnailRuntimeState> _thumbnailRuntime = new(ReferenceEqualityComparer.Instance);
        private long _nextThumbnailRuntimeIdentity;
        private bool _disposed;
        private Task _metadataProcessingTask = Task.CompletedTask;
        private readonly string _fallbackThumbnailUri;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly SemaphoreSlim _crawlerStartGate = new(1, 1);
        private readonly object _crawlerStateLock = new();
        private IOneFourOneJavCrawlerSessionHandle? _crawlerHandle;
        private IOneFourOneJavCrawlerSession? _crawlerSession;
        private Task? _crawlerMonitorTask;
        private int _crawlerMonitorGeneration;
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
        private bool _isCrawlerRunning;
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
                if (value is null)
                {
                    return;
                }

                if (SetProperty(ref _selectedSortOption, value))
                {
                    ApplySort(value.Field, value.Direction);
                }
            }
        }

        public RelayCommand RandomPlayCommand { get; }
        public RelayCommand FetchMetadataCommand { get; }
        public RelayCommand ClearSearchCommand { get; }
        public RelayCommand StartCrawlerCommand { get; }


        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<VideoItem>? PlayVideoRequested;

        public MainViewModel(
            ILibraryStore libraryStore,
            ILibraryScanner libraryScanner,
            WebMetadataService webMetadataService,
            CrawlerSessionProvider crawlerSessionProvider,
            OneFourOneJavMetaSource oneFourOneJavSource,
            IOneFourOneJavCrawlerSessionFactory crawlerSessionFactory,
            IThumbnailImageLoader thumbnailImageLoader)
            : this(
                libraryStore,
                libraryScanner,
                webMetadataService,
                crawlerSessionProvider,
                oneFourOneJavSource,
                crawlerSessionFactory,
                ThumbnailPerformanceProbe.Disabled,
                thumbnailImageLoader)
        {
        }

        internal MainViewModel(
            ILibraryStore libraryStore,
            ILibraryScanner libraryScanner,
            WebMetadataService webMetadataService,
            CrawlerSessionProvider crawlerSessionProvider,
            OneFourOneJavMetaSource oneFourOneJavSource,
            IOneFourOneJavCrawlerSessionFactory crawlerSessionFactory,
            ThumbnailPerformanceProbe performanceProbe,
            IThumbnailImageLoader thumbnailImageLoader,
            Func<VideoEntry, CancellationToken, VideoItem>? storedVideoMapper = null)
        {
            _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
            _webMetadataService = webMetadataService ?? throw new ArgumentNullException(nameof(webMetadataService));
            _crawlerSessionProvider = crawlerSessionProvider ?? throw new ArgumentNullException(nameof(crawlerSessionProvider));
            _oneFourOneJavSource = oneFourOneJavSource ?? throw new ArgumentNullException(nameof(oneFourOneJavSource));
            _crawlerSessionFactory = crawlerSessionFactory ?? throw new ArgumentNullException(nameof(crawlerSessionFactory));
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
                new SortOption("제목 내림차순", SortField.Title, ListSortDirection.Descending),
                new SortOption("제목 오름차순", SortField.Title, ListSortDirection.Ascending),
                new SortOption("출시일 내림차순", SortField.ReleaseDate, ListSortDirection.Descending),
                new SortOption("출시일 오름차순", SortField.ReleaseDate, ListSortDirection.Ascending),
                new SortOption("생성일 내림차순", SortField.CreatedUtc, ListSortDirection.Descending),
                new SortOption("생성일 오름차순", SortField.CreatedUtc, ListSortDirection.Ascending)
            });

            RandomPlayCommand = new RelayCommand(
                _ => PlayRandomVideo(),
                _ => FilteredVideos.Cast<VideoItem>().Any(v => v.Presence == VideoPresenceState.Available));
            FetchMetadataCommand = new RelayCommand(
                async _ => await FetchMissingMetadataWithCrawlerAsync().ConfigureAwait(false),
                _ => CanMutateLibrary);
            ClearSearchCommand = new RelayCommand(_ => ClearSearch());
            StartCrawlerCommand = new RelayCommand(async _ => await StartCrawlerAsync(CancellationToken.None).ConfigureAwait(false), _ => !IsCrawlerRunning);

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
                if (SetProperty(ref _searchQuery, value))
                {
                    FilteredVideos.Refresh();
                    UpdateStatus();
                }
            }
        }

        public string SelectedActor
        {
            get => _selectedActor;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? AllActorsLabel : value;

                if (Actors is not null && !Actors.Any(actor => actor.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
                {
                    normalized = AllActorsLabel;
                }

                if (SetProperty(ref _selectedActor, normalized))
                {
                    FilteredVideos?.Refresh();
                    UpdateStatus();
                }
            }
        }

        public bool ShowMissingMetadataOnly
        {
            get => _showMissingMetadataOnly;
            set
            {
                if (SetProperty(ref _showMissingMetadataOnly, value))
                {
                    FilteredVideos.Refresh();
                    UpdateStatus();
                }
            }
        }

        public VideoItem? SelectedVideo
        {
            get => _selectedVideo;
            set => SetProperty(ref _selectedVideo, value);
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

        public bool IsFetchingMetadata => _mutationLease?.Owner is
            LibraryMutationOwner.AutoMetadata or
            LibraryMutationOwner.ManualFetch;

        public bool CanMutateLibrary =>
            !_disposed && StartupState == StartupLibraryState.Ready && _mutationLease is null;

        public bool CanUseCommandBar => true;

        internal CancellationToken LifetimeToken => _lifetimeCts.Token;
        internal LibraryMutationLease? CurrentMutationLease => _mutationLease;

        public bool IsCrawlerRunning
        {
            get => _isCrawlerRunning;
            private set
            {
                if (SetProperty(ref _isCrawlerRunning, value))
                {
                    StartCrawlerCommand?.RaiseCanExecuteChanged();
                }
            }
        }

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
                (StartupLibraryState.Scanning, StartupLibraryState.ApplyingScan or StartupLibraryState.Ready) => true,
                (StartupLibraryState.ApplyingScan, StartupLibraryState.SavingScan or StartupLibraryState.Ready or StartupLibraryState.Faulted) => true,
                (StartupLibraryState.SavingScan, StartupLibraryState.Ready or StartupLibraryState.Faulted) => true,
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

            lock (_thumbnailRuntimeLock)
            {
                if (_thumbnailRuntime.TryGetValue(item, out var state) &&
                    (state.Realization is not null || state.InFlight is not null))
                {
                    state.Generation++;
                    state.InFlight?.Cancellation.Cancel();
                    state.InFlight?.Cancellation.Dispose();
                    state.InFlight = null;
                    state.Realization = null;
                }
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
                    StatusMessage = "Loading library...";
                });
                var loadedLibrary = await _libraryStore.LoadAsync(startupToken).ConfigureAwait(false);
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
                        StatusMessage = $"Library initialization failed: {ex.Message}";
                    });
                    AppLogger.Error("Library initialization failed.", ex);
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
                var result = await _libraryScanner.ScanAsync(_library, cancellationToken).ConfigureAwait(false);
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
                    SetStartupState(StartupLibraryState.SavingScan);
                    _performanceProbe.TryMark(StartupTimingMarker.AllItemsPublished);
                }).ConfigureAwait(false))
                {
                    return;
                }

                await _libraryStore.SaveAsync(plan.FinalLibrary, _lifetimeCts.Token).ConfigureAwait(false);

                LibraryMutationLease? autoMetadataLease = null;
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
                    if (HasPendingMetadata())
                    {
                        autoMetadataLease = TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata);
                    }
                    SetStartupState(StartupLibraryState.Ready);
                    StatusMessage = $"Scan complete: {plan.AddedCount} added, {plan.MissingCount} missing, {plan.UpdatedCount} updated.";
                }).ConfigureAwait(false))
                {
                    return;
                }

                AppLogger.Info(
                    $"Scan apply summary: {plan.AddedCount} added, {plan.MissingCount} missing, {plan.UpdatedCount} updated.");
                if (autoMetadataLease is not null)
                {
                    StartMetadataProcessing(autoMetadataLease);
                }
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

        private async Task FetchMissingMetadataWithCrawlerAsync()
        {
            LibraryMutationLease? lease = null;
            await _dispatcher.InvokeAsync(() =>
            {
                if (IsFetchingMetadata)
                {
                    StatusMessage = "Metadata fetch already in progress.";
                    return;
                }
                if (!CanMutateLibrary)
                {
                    StatusMessage = "Library is busy. Try again when startup work completes.";
                    return;
                }
                lease = TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);
            });
            if (lease is null)
            {
                return;
            }

            var missing = Videos.Where(IsMetadataIncomplete).ToList();
            if (missing.Count == 0)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "No videos require crawler metadata.";
                    ReleaseLibraryMutation(lease);
                });
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Crawler metadata fetch starting for {missing.Count} videos.";
            });

            AppLogger.Info($"[Crawler] Starting metadata fetch for {missing.Count} videos with missing metadata.");

            var preserveStatusMessage = false;
            try
            {
                if (!await EnsureCrawlerReadyAsync(_lifetimeCts.Token).ConfigureAwait(false))
                {
                    preserveStatusMessage = true;
                    return;
                }

                var total = missing.Count;
                var index = 0;

                foreach (var video in missing)
                {
                    index++;
                    var normalizedPath = LibraryPathHelper.NormalizeLibraryPath(video.LibraryPath);
                    var entry = FindEntry(normalizedPath);
                    if (entry is null)
                    {
                        AppLogger.Info($"[Crawler] Skipping {video.LibraryPath}; no library entry found.");
                        continue;
                    }

                    var query = BuildCrawlerQuery(video, entry);
                    if (string.IsNullOrWhiteSpace(query))
                    {
                        AppLogger.Info($"[Crawler] Skipping {video.LibraryPath}; unable to build crawler query.");
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"[{index}/{total}] Skipped {video.Title}: query unavailable.";
                        });
                        continue;
                    }

                    var displayName = string.IsNullOrWhiteSpace(video.Title)
                        ? Path.GetFileName(video.LibraryPath)
                        : video.Title;

                    await _dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = $"[{index}/{total}] Fetching metadata for {displayName}...";
                    });

                    var updatedEntry = await _webMetadataService.EnrichAsync(
                        entry,
                        query,
                        _lifetimeCts.Token).ConfigureAwait(false);
                    if (updatedEntry is null)
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"[{index}/{total}] No new metadata for {displayName}.";
                        });
                        continue;
                    }

                    await _dispatcher.InvokeAsync(() => EnsureCurrentLease(lease));
                    UpdateLibraryEntry(updatedEntry.Path, _ => updatedEntry);

                    await _dispatcher.InvokeAsync(() =>
                    {
                        ApplyMetadataToCollections(updatedEntry);
                        StatusMessage = $"[{index}/{total}] Metadata updated for {displayName}.";
                    });

                    await _dispatcher.InvokeAsync(() => EnsureCurrentLease(lease));
                    await _libraryStore.SaveAsync(_library, _lifetimeCts.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    ReleaseLibraryMutation(lease);
                    if (!preserveStatusMessage)
                    {
                        StatusMessage = BuildLibrarySummary();
                    }
                });

                AppLogger.Info("[Crawler] Metadata fetch completed.");
            }
        }

        private async Task<bool> EnsureCrawlerReadyAsync(CancellationToken cancellationToken = default)
        {
            var (handle, session) = GetCrawlerState();
            if (handle is not null && session is not null && handle.IsBrowserOpen())
            {
                return true;
            }

            if (handle is not null)
            {
                DisposeCrawler();
                await _dispatcher.InvokeAsync(() => IsCrawlerRunning = false);
            }

            return await StartCrawlerAsync(cancellationToken).ConfigureAwait(false);
        }

        private static string BuildCrawlerQuery(VideoItem video, VideoEntry entry)
        {
            if (!string.IsNullOrWhiteSpace(video?.Title))
            {
                var normalized = LibraryPathHelper.NormalizeCode(video.Title);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            var fallbackSource = string.IsNullOrWhiteSpace(entry.Path)
                ? video?.LibraryPath ?? string.Empty
                : entry.Path;

            var fileStem = Path.GetFileNameWithoutExtension(fallbackSource);
            return LibraryPathHelper.NormalizeCode(fileStem);
        }

        private async Task<bool> StartCrawlerAsync(CancellationToken cancellationToken = default)
        {
            await _crawlerStartGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (handle, session) = GetCrawlerState();
                if (handle is not null && session is not null && handle.IsBrowserOpen())
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = "Crawler already running. Close the browser window to start a new session.";
                    });
                    return true;
                }

                if (handle is not null)
                {
                    DisposeCrawler();
                    await _dispatcher.InvokeAsync(() => IsCrawlerRunning = false);
                }

                await _dispatcher.InvokeAsync(() =>
                {
                    IsCrawlerRunning = true;
                    StatusMessage = "Crawler starting...";
                });

                AppLogger.Info("Starting Selenium crawler.");

                try
                {
                    var result = await _crawlerSessionFactory.StartAsync(cancellationToken).ConfigureAwait(false);
                    await _dispatcher.InvokeAsync(() =>
                    {
                        SetCrawlerState(result.Handle, result.Session);
                        IsCrawlerRunning = true;
                        StatusMessage = result.Summary;
                    });

                    StartCrawlerMonitor(result.Handle);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    DisposeCrawler();
                    await _dispatcher.InvokeAsync(() => IsCrawlerRunning = false);
                    throw;
                }
                catch (Exception ex)
                {
                    DisposeCrawler();

                    await _dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = $"Crawler failed: {ex.Message}";
                        IsCrawlerRunning = false;
                    });

                    AppLogger.Error("Crawler failed while using Selenium.", ex);
                    return false;
                }
            }
            finally
            {
                _crawlerStartGate.Release();
            }
        }


        public async Task<WebVideoMetaResult?> TryFetchOneFourOneJavMetadataAsync(string query, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return null;
            }

            try
            {
                if (!await EnsureCrawlerReadyAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }

                var result = await _oneFourOneJavSource.FetchAsync(query, cancellationToken).ConfigureAwait(false);
                return result is null
                    ? null
                    : await _webMetadataService.TranslateResultDescriptionAsync(result, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLogger.Error($"[141Jav] Failed to fetch crawler metadata for '{query}'.", ex);
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Crawler metadata fetch failed: {ex.Message}";
                });
                return null;
            }
        }

        private void StartCrawlerMonitor(IOneFourOneJavCrawlerSessionHandle monitoredHandle)
        {
            var generation = Interlocked.Increment(ref _crawlerMonitorGeneration);

            _crawlerMonitorTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        if (!IsCurrentCrawlerHandle(monitoredHandle))
                        {
                            break;
                        }

                        try
                        {
                            if (!monitoredHandle.IsBrowserOpen())
                            {
                                break;
                            }
                        }
                        catch (Exception)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    var disposedCurrent = DisposeCrawlerIfCurrent(monitoredHandle);

                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (disposedCurrent)
                        {
                            IsCrawlerRunning = false;
                            StatusMessage = BuildLibrarySummary();
                        }
                    });

                    if (Volatile.Read(ref _crawlerMonitorGeneration) == generation)
                    {
                        _crawlerMonitorTask = null;
                    }
                }
            });
        }

        private void DisposeCrawler()
        {
            DisposeCrawlerIfCurrent(null);
        }

        private (IOneFourOneJavCrawlerSessionHandle? Handle, IOneFourOneJavCrawlerSession? Session) GetCrawlerState()
        {
            lock (_crawlerStateLock)
            {
                return (_crawlerHandle, _crawlerSession);
            }
        }

        private void SetCrawlerState(
            IOneFourOneJavCrawlerSessionHandle handle,
            IOneFourOneJavCrawlerSession session)
        {
            lock (_crawlerStateLock)
            {
                _crawlerHandle = handle;
                _crawlerSession = session;
                _crawlerSessionProvider.SetSession(session);
            }
        }

        private bool IsCurrentCrawlerHandle(IOneFourOneJavCrawlerSessionHandle handle)
        {
            lock (_crawlerStateLock)
            {
                return ReferenceEquals(_crawlerHandle, handle);
            }
        }

        private bool DisposeCrawlerIfCurrent(IOneFourOneJavCrawlerSessionHandle? expectedHandle)
        {
            IOneFourOneJavCrawlerSessionHandle? handle;
            lock (_crawlerStateLock)
            {
                handle = _crawlerHandle;
                if (expectedHandle is not null && !ReferenceEquals(handle, expectedHandle))
                {
                    return false;
                }

                _crawlerSessionProvider.SetSession(null);
                _crawlerSession = null;
                _crawlerHandle = null;
            }

            if (handle is not null)
            {
                try
                {
                    handle.Dispose();
                }
                catch (Exception)
                {
                    // ignored: driver process already gone or already disposed.
                }
            }

            return true;
        }


        internal ScanPreparationSeed CaptureScanPreparationSeed(LibraryMutationLease startupLease)
        {
            _dispatcher.VerifyAccess();
            EnsureCurrentLease(startupLease);
            return new ScanPreparationSeed(
                _library.Targets.ToArray(),
                _library.Videos.ToArray(),
                _videoIndex.ToArray());
        }

        internal ScanApplyPlan PrepareScanApplyPlan(
            LibraryScanResult result,
            ScanPreparationSeed seed,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targets = seed.TargetReferences.Select(CloneTarget).ToArray();
            var videos = seed.VideoReferences.Select(CloneEntry).ToArray();
            var itemIdentities = seed.ItemIdentitiesByNormalizedPath.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            var existing = new Dictionary<string, ExistingScanItemSnapshot>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = LibraryPathHelper.NormalizeLibraryPath(entry.Path);
                if (itemIdentities.TryGetValue(normalized, out var item))
                {
                    existing[normalized] = new ExistingScanItemSnapshot(normalized, entry, item);
                }
            }
            var preparation = new ScanPreparationSnapshot(
                Array.AsReadOnly(targets),
                Array.AsReadOnly(videos),
                new System.Collections.ObjectModel.ReadOnlyDictionary<string, ExistingScanItemSnapshot>(existing));

            var snapshots = result.Snapshots.ToDictionary(
                snapshot => LibraryPathHelper.NormalizeLibraryPath(snapshot.LibraryPath),
                StringComparer.OrdinalIgnoreCase);
            var finalVideos = new List<VideoEntry>(preparation.Videos.Count + result.NewFiles.Count);
            var operations = new List<ScanApplyOperation>(preparation.Videos.Count + result.NewFiles.Count);
            foreach (var entry in preparation.Videos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = LibraryPathHelper.NormalizeLibraryPath(entry.Path);
                if (!preparation.ExistingByPath.TryGetValue(normalized, out var existingItem))
                {
                    continue;
                }

                if (snapshots.TryGetValue(normalized, out var snapshot))
                {
                    var createdUtc = snapshot.CreatedUtc != default ? snapshot.CreatedUtc : entry.CreatedUtc;
                    var updatedEntry = entry with
                    {
                        SizeBytes = snapshot.SizeBytes,
                        LastModifiedUtc = snapshot.LastWriteUtc,
                        CreatedUtc = createdUtc
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
                    finalVideos.Add(entry);
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
                var entry = CreateEntryFromSnapshot(snapshot);
                var item = MapVideo(entry);
                finalVideos.Add(CloneEntry(entry));
                operations.Add(new AddScanItem(entry, item, entry.Path));
                metadataPaths.Add(entry.Path);
            }

            var scanTimestamp = DateTime.UtcNow;
            var finalLibrary = new LibraryData
            {
                Version = 1,
                Targets = preparation.Targets
                    .Select(target => CloneTarget(target) with { LastScanUtc = scanTimestamp })
                    .ToList(),
                Videos = finalVideos.Select(CloneEntry).ToList()
            };
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
                scanTimestamp,
                result.NewFiles.Count,
                result.MissingEntries.Count,
                result.UpdatedEntries.Count);
        }

        private static TargetFolder CloneTarget(TargetFolder target) => target with
        {
            IncludePatterns = target.IncludePatterns.ToArray(),
            ExcludePatterns = target.ExcludePatterns.ToArray()
        };

        private static VideoEntry CloneEntry(VideoEntry entry) => entry with
        {
            Meta = entry.Meta with
            {
                Actors = entry.Meta.Actors.ToArray(),
                Tags = entry.Meta.Tags.ToArray()
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

        private async Task ProcessMetadataQueueAsync(LibraryMutationLease lease)
        {
            bool hasItems;
            lock (_metadataQueueLock)
            {
                hasItems = _pendingMetadata.Count > 0;
            }

            if (!hasItems)
            {
                return;
            }

            if (lease.Owner != LibraryMutationOwner.AutoMetadata)
            {
                throw new InvalidOperationException("Metadata queue processing requires an AutoMetadata lease.");
            }
            await _dispatcher.InvokeAsync(() => EnsureCurrentLease(lease));

            try
            {
                while (true)
                {
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

                    await ProcessMetadataForPathAsync(normalizedPath, lease).ConfigureAwait(false);
                }
            }
            finally
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    ReleaseLibraryMutation(lease);
                    StatusMessage = BuildLibrarySummary();
                });
            }
        }

        private async Task ProcessMetadataForPathAsync(
            string normalizedPath,
            LibraryMutationLease lease)
        {
            var entry = FindEntry(normalizedPath);
            if (entry is null)
            {
                return;
            }

            var displayName = Path.GetFileName(LibraryPathHelper.ResolveToAbsolute(normalizedPath));

            await _dispatcher.InvokeAsync(() =>
            {
                StatusMessage = $"Fetching metadata for {displayName}...";
            });

            var query = string.IsNullOrWhiteSpace(entry.Meta.Title)
                ? Path.GetFileNameWithoutExtension(normalizedPath)
                : entry.Meta.Title;

            try
            {
                var updatedEntry = await _webMetadataService.EnrichAsync(
                    entry,
                    query,
                    _lifetimeCts.Token).ConfigureAwait(false);
                if (updatedEntry is null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = $"No metadata found for {displayName}.";
                    });
                    return;
                }

                await _dispatcher.InvokeAsync(() => EnsureCurrentLease(lease));
                UpdateLibraryEntry(updatedEntry.Path, _ => updatedEntry);

                await _dispatcher.InvokeAsync(() =>
                {
                    ApplyMetadataToCollections(updatedEntry);
                    StatusMessage = $"Metadata updated for {displayName}.";
                });

                await _dispatcher.InvokeAsync(() => EnsureCurrentLease(lease));
                await _libraryStore.SaveAsync(_library, _lifetimeCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
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
            var normalized = LibraryPathHelper.NormalizeLibraryPath(updatedEntry.Path);
            var mapped = MapVideo(updatedEntry);

            if (_videoIndex.TryGetValue(normalized, out var existing))
            {
                var index = Videos.IndexOf(existing);
                if (index >= 0)
                {
                    RemoveThumbnailRuntimeState(existing);
                    Videos[index] = mapped;
                    RegisterVideo(mapped);
                }
                else
                {
                    RemoveThumbnailRuntimeState(existing);
                    Videos.Add(mapped);
                    RegisterVideo(mapped);
                }
            }
            else
            {
                Videos.Add(mapped);
                RegisterVideo(mapped);
            }

            if (ReferenceEquals(SelectedVideo, existing) || SelectedVideo?.LibraryPath == normalized)
            {
                SelectedVideo = mapped;
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
            lock (_metadataQueueLock)
            {
                if (_metadataProcessingTask is not null && !_metadataProcessingTask.IsCompleted)
                {
                    throw new InvalidOperationException("Metadata processing is already active.");
                }
                _metadataProcessingTask = Task.Run(() => ProcessMetadataQueueAsync(lease));
            }
        }

        private void ApplySort(SortField field, ListSortDirection direction, bool updateStatus = true)
        {
            var property = field switch
            {
                SortField.Title => nameof(VideoItem.Title),
                SortField.ReleaseDate => nameof(VideoItem.ReleaseDate),
                SortField.CreatedUtc => nameof(VideoItem.CreatedUtc),
                _ => nameof(VideoItem.Title)
            };

            FilteredVideos.SortDescriptions.Clear();
            FilteredVideos.SortDescriptions.Add(new SortDescription(property, direction));
            FilteredVideos.Refresh();

            if (updateStatus)
            {
                UpdateStatus();
            }
        }

        private void PlayRandomVideo()
        {
            var candidates = FilteredVideos.Cast<VideoItem>()
                .Where(v => v.Presence == VideoPresenceState.Available)
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

            var actors = result.Actors ?? Array.Empty<string>();
            var tags = result.Tags ?? Array.Empty<string>();
            var description = result.Description ?? string.Empty;
            var title = string.IsNullOrWhiteSpace(result.Title) ? item.Title : result.Title.Trim();
            var thumbnailPath = string.IsNullOrWhiteSpace(result.ThumbnailPath) ? item.ThumbnailPath : result.ThumbnailPath;
            var thumbnailUri = ResolveThumbnailPath(thumbnailPath);

            await _dispatcher.InvokeAsync(() =>
            {
                EnsureCurrentLease(lease);
                var oldThumbnailPath = item.ThumbnailPath;
                var realizedWidth = GetRealizedThumbnailWidth(item);
                item.Title = title;
                item.ReleaseDate = result.ReleaseDate;
                item.Actors = actors;
                item.Tags = tags;
                item.Description = description;
                item.ThumbnailPath = thumbnailPath;
                item.ThumbnailUri = thumbnailUri;

                if (!StringComparer.OrdinalIgnoreCase.Equals(oldThumbnailPath, thumbnailPath))
                {
                    ReleaseThumbnail(item);
                    if (realizedWidth > 0)
                    {
                        _ = RequestThumbnailAsync(item, realizedWidth);
                    }
                }
            });

            UpdateLibraryEntry(item.LibraryPath, entry =>
            {
                var updatedMeta = entry.Meta with
                {
                    Title = title,
                    Date = result.ReleaseDate,
                    Actors = actors,
                    Tags = tags,
                    Description = description,
                    Thumbnail = thumbnailPath
                };
                return entry with { Meta = updatedMeta };
            });

            await _dispatcher.InvokeAsync(() => EnsureCurrentLease(lease));
            await _libraryStore.SaveAsync(_library, _lifetimeCts.Token).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                RefreshActorList();
                FilteredVideos.Refresh();
                UpdateStatus();
                StatusMessage = $"Metadata updated for '{title}'.";
            });

            AppLogger.Info($"Metadata updated for '{title}'.");
        }

        private bool FilterVideo(object? obj)
        {
            if (obj is not VideoItem video)
            {
                return false;
            }

            var matchesActor = SelectedActor == AllActorsLabel || video.Actors.Contains(SelectedActor, StringComparer.OrdinalIgnoreCase);
            var matchesSearch = string.IsNullOrWhiteSpace(SearchQuery) ||
                                video.Title.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase) ||
                                video.Actors.Any(actor => actor.Contains(SearchQuery, StringComparison.OrdinalIgnoreCase));
            var matchesMetadata = !ShowMissingMetadataOnly || IsMetadataIncomplete(video);

            return matchesActor && matchesSearch && matchesMetadata;
        }

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

            if (!updateMessage || IsScanning || IsFetchingMetadata || IsCrawlerRunning)
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
                Title = entry.Meta.Title,
                ReleaseDate = entry.Meta.Date,
                Actors = entry.Meta.Actors,
                Tags = entry.Meta.Tags,
                Description = entry.Meta.Description,
                ThumbnailUri = ResolveThumbnailPath(entry.Meta.Thumbnail),
                ThumbnailPath = entry.Meta.Thumbnail
            };

            item.ReleaseThumbnail(_thumbnailImageLoader.FallbackSource);
            item.UpdateFileState(absolutePath, entry.SizeBytes, lastModified, VideoPresenceState.Available, createdUtc);
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
            DisposeCrawler();
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
            lock (_thumbnailRuntimeLock)
            {
                if (_thumbnailRuntime.Remove(item, out var state))
                {
                    slot = state.InFlight;
                }
            }

            slot?.Cancellation.Cancel();
            slot?.Cancellation.Dispose();
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
                "resources/noimage.jpg",
                Array.Empty<string>(),
                string.Empty);

            return new VideoEntry(snapshot.LibraryPath, meta, snapshot.SizeBytes, snapshot.LastWriteUtc, snapshot.CreatedUtc);
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
            var key = LibraryPathHelper.NormalizeLibraryPath(item.LibraryPath);
            _videoIndex[key] = item;
        }

        private VideoEntry? FindEntry(string libraryPath)
        {
            var normalized = LibraryPathHelper.NormalizeLibraryPath(libraryPath);
            return _library.Videos.FirstOrDefault(v =>
                LibraryPathHelper.NormalizeLibraryPath(v.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase));
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

        private void UpdateLibraryEntry(string path, Func<VideoEntry, VideoEntry> updater)
        {
            var normalized = LibraryPathHelper.NormalizeLibraryPath(path);
            for (var i = 0; i < _library.Videos.Count; i++)
            {
                var current = _library.Videos[i];
                if (LibraryPathHelper.NormalizeLibraryPath(current.Path).Equals(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _library.Videos[i] = updater(current);
                    return;
                }
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




