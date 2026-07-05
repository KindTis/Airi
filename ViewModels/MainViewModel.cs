using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
    /// <summary>
    /// Loads persisted library data, exposes UI-facing collections, and coordinates scanning/diff and web metadata enrichment.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string AllActorsLabel = "All Actors";
        private const int InitialLoadBatchSize = 40;
        private readonly Random _random = new();
        private readonly LibraryStore _libraryStore;
        private readonly LibraryScanner _libraryScanner;
        private readonly WebMetadataService _webMetadataService;
        private readonly Dispatcher _dispatcher;
        private readonly CrawlerSessionProvider _crawlerSessionProvider;
        private readonly OneFourOneJavMetaSource _oneFourOneJavSource;
        private readonly IOneFourOneJavCrawlerSessionFactory _crawlerSessionFactory;
        private readonly Dictionary<string, VideoItem> _videoIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _metadataQueueLock = new();
        private readonly Queue<string> _pendingMetadata = new();
        private readonly HashSet<string> _metadataScheduled = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _thumbnailUriCache = new(StringComparer.OrdinalIgnoreCase);
        private Task _metadataProcessingTask = Task.CompletedTask;
        private readonly string _fallbackThumbnailUri;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
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
        private bool _isScanning;
        private bool _isFetchingMetadata;
        private bool _isCrawlerRunning;
        private bool _canUseCommandBar = true;
        private SortOption _selectedSortOption;
        private bool _showMissingMetadataOnly;
        private bool _isInitialLoading = true;

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
            LibraryStore libraryStore,
            LibraryScanner libraryScanner,
            WebMetadataService webMetadataService,
            CrawlerSessionProvider crawlerSessionProvider,
            OneFourOneJavMetaSource oneFourOneJavSource,
            IOneFourOneJavCrawlerSessionFactory crawlerSessionFactory)
        {
            _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
            _webMetadataService = webMetadataService ?? throw new ArgumentNullException(nameof(webMetadataService));
            _crawlerSessionProvider = crawlerSessionProvider ?? throw new ArgumentNullException(nameof(crawlerSessionProvider));
            _oneFourOneJavSource = oneFourOneJavSource ?? throw new ArgumentNullException(nameof(oneFourOneJavSource));
            _crawlerSessionFactory = crawlerSessionFactory ?? throw new ArgumentNullException(nameof(crawlerSessionFactory));
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
            FetchMetadataCommand = new RelayCommand(async _ => await FetchMissingMetadataWithCrawlerAsync().ConfigureAwait(false));
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

        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                if (SetProperty(ref _isScanning, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowVideoSkeleton)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowActorSkeleton)));
                    UpdateStatus(updateMessage: false);
                }
            }
        }

        public bool IsFetchingMetadata
        {
            get => _isFetchingMetadata;
            private set
            {
                if (SetProperty(ref _isFetchingMetadata, value))
                {
                    CanUseCommandBar = !value;
                    UpdateStatus(updateMessage: false);
                }
            }
        }

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

        public bool CanUseCommandBar
        {
            get => _canUseCommandBar;
            private set => SetProperty(ref _canUseCommandBar, value);
        }

        public bool IsInitialLoading
        {
            get => _isInitialLoading;
            private set
            {
                if (SetProperty(ref _isInitialLoading, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowVideoSkeleton)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowActorSkeleton)));
                }
            }
        }

        public bool ShowVideoSkeleton => IsInitialLoading || (IsScanning && Videos.Count == 0);
        public bool ShowActorSkeleton => IsInitialLoading || (IsScanning && Actors.Count <= 1);

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            if (_isInitialized)
            {
                return;
            }

            await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_isInitialized)
                {
                    return;
                }

                await _dispatcher.InvokeAsync(() => StatusMessage = "Loading library...");
                var loadedLibrary = await _libraryStore.LoadAsync().ConfigureAwait(false);
                var mappedVideos = loadedLibrary.Videos.Select(MapVideo).ToList();
                AppLogger.Info($"Library loaded. Videos: {loadedLibrary.Videos.Count}.");

                await LoadLibraryDataAsync(loadedLibrary, mappedVideos, cancellationToken).ConfigureAwait(false);

                _isInitialized = true;
                _ = RunStartupScanAsync(cancellationToken);
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private async Task LoadLibraryDataAsync(LibraryData data, IReadOnlyList<VideoItem> mappedVideos, CancellationToken cancellationToken)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _library = data ?? new LibraryData();
                Videos.Clear();
                _videoIndex.Clear();
                RefreshActorList();
                FilteredVideos.Refresh();
            });

            for (var index = 0; index < mappedVideos.Count; index += InitialLoadBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var currentIndex = index;
                var batchCount = Math.Min(InitialLoadBatchSize, mappedVideos.Count - currentIndex);
                await _dispatcher.InvokeAsync(() =>
                {
                    for (var offset = 0; offset < batchCount; offset++)
                    {
                        var item = mappedVideos[currentIndex + offset];
                        RegisterVideo(item);
                        Videos.Add(item);
                    }
                }, DispatcherPriority.Background).Task.ConfigureAwait(false);

                await Task.Delay(1, cancellationToken).ConfigureAwait(false);
            }

            await _dispatcher.InvokeAsync(() =>
            {
                RefreshActorList();
                FilteredVideos.Refresh();
                SelectedVideo = Videos.FirstOrDefault();
                IsInitialLoading = false;
                StatusMessage = BuildLibrarySummary();
            });
        }

        private async Task RunStartupScanAsync(CancellationToken cancellationToken)
        {
            if (IsScanning)
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                IsScanning = true;
                StatusMessage = "Scanning library...";
            });
            AppLogger.Info("Initiating initial library scan.");

            try
            {
                var result = await _libraryScanner.ScanAsync(_library, cancellationToken).ConfigureAwait(false);

                await _dispatcher.InvokeAsync(() =>
                {
                    ApplyScanResult(result);
                });

                await _libraryStore.SaveAsync(_library).ConfigureAwait(false);

                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Scan complete: {result.NewFiles.Count} added, {result.MissingEntries.Count} missing, {result.UpdatedEntries.Count} updated.";
                    AppLogger.Info(StatusMessage);
                });
            }
            catch (OperationCanceledException)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "Scan cancelled.";
                    AppLogger.Info(StatusMessage);
                });
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Scan failed: {ex.Message}";
                    AppLogger.Error(StatusMessage, ex);
                });
            }
            finally
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsScanning = false;
                    IsInitialLoading = false;

                    if (!IsFetchingMetadata)
                    {
                        StatusMessage = BuildLibrarySummary();
                    }

                    UpdateStatus(updateMessage: false);
                });
            }
        }

        private async Task FetchMissingMetadataWithCrawlerAsync()
        {
            if (IsFetchingMetadata)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "Metadata fetch already in progress.";
                });
                return;
            }

            var missing = Videos.Where(IsMetadataIncomplete).ToList();
            if (missing.Count == 0)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "No videos require crawler metadata.";
                });
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                IsFetchingMetadata = true;
                StatusMessage = $"Crawler metadata fetch starting for {missing.Count} videos.";
            });

            AppLogger.Info($"[Crawler] Starting metadata fetch for {missing.Count} videos with missing metadata.");

            var preserveStatusMessage = false;
            try
            {
                if (!await EnsureCrawlerReadyAsync().ConfigureAwait(false))
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

                    var updatedEntry = await _webMetadataService.EnrichAsync(entry, query, CancellationToken.None).ConfigureAwait(false);
                    if (updatedEntry is null)
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"[{index}/{total}] No new metadata for {displayName}.";
                        });
                        continue;
                    }

                    UpdateLibraryEntry(updatedEntry.Path, _ => updatedEntry);

                    await _dispatcher.InvokeAsync(() =>
                    {
                        ApplyMetadataToCollections(updatedEntry);
                        StatusMessage = $"[{index}/{total}] Metadata updated for {displayName}.";
                    });

                    await _libraryStore.SaveAsync(_library).ConfigureAwait(false);
                }
            }
            finally
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsFetchingMetadata = false;
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


        private void ApplyScanResult(LibraryScanResult result)
        {
            var snapshotMap = result.Snapshots.ToDictionary(s => LibraryPathHelper.NormalizeLibraryPath(s.LibraryPath), StringComparer.OrdinalIgnoreCase);

            foreach (var video in Videos)
            {
                var key = LibraryPathHelper.NormalizeLibraryPath(video.LibraryPath);
                if (snapshotMap.TryGetValue(key, out var snapshot))
                {
                    video.UpdateFileState(snapshot.AbsolutePath, snapshot.SizeBytes, snapshot.LastWriteUtc, VideoPresenceState.Available, snapshot.CreatedUtc);
                }
                else
                {
                    var absolute = LibraryPathHelper.ResolveToAbsolute(video.LibraryPath);
                    video.UpdateFileState(absolute, video.SizeBytes, video.LastModifiedUtc, VideoPresenceState.Missing, video.CreatedUtc);
                    AppLogger.Info($"Marked missing: {video.LibraryPath}");
                }
            }

            foreach (var updated in result.UpdatedEntries)
            {
                UpdateLibraryEntry(updated.Entry.Path, current => current with
                {
                    SizeBytes = updated.Snapshot.SizeBytes,
                    LastModifiedUtc = updated.Snapshot.LastWriteUtc,
                    CreatedUtc = updated.Snapshot.CreatedUtc != default ? updated.Snapshot.CreatedUtc : current.CreatedUtc
                });

                if (_videoIndex.TryGetValue(LibraryPathHelper.NormalizeLibraryPath(updated.Entry.Path), out var video))
                {
                    video.UpdateFileState(updated.Snapshot.AbsolutePath, updated.Snapshot.SizeBytes, updated.Snapshot.LastWriteUtc, VideoPresenceState.Available, updated.Snapshot.CreatedUtc);
                }
            }

            foreach (var newFile in result.NewFiles)
            {
                var entry = CreateEntryFromSnapshot(newFile);
                _library.Videos.Add(entry);
                var item = MapVideo(entry);
                RegisterVideo(item);
                Videos.Add(item);
                AppLogger.Info($"Added new library entry: {entry.Path}");
                EnqueueMetadataForProcessing(entry.Path);
            }

            var scanTimestamp = DateTime.UtcNow;
            _library.Targets = _library.Targets
                .Select(t => t with { LastScanUtc = scanTimestamp })
                .ToList();

            RefreshActorList();
            UpdateStatus(updateMessage: false);
            RequestMetadataProcessing();
        }

        private async Task ProcessMetadataQueueAsync()
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

            await _dispatcher.InvokeAsync(() => IsFetchingMetadata = true);

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

                    await ProcessMetadataForPathAsync(normalizedPath).ConfigureAwait(false);
                }
            }
            finally
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    IsFetchingMetadata = false;
                    StatusMessage = BuildLibrarySummary();
                });
            }
        }

        private async Task ProcessMetadataForPathAsync(string normalizedPath)
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
                var updatedEntry = await _webMetadataService.EnrichAsync(entry, query, CancellationToken.None).ConfigureAwait(false);
                if (updatedEntry is null)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        StatusMessage = $"No metadata found for {displayName}.";
                    });
                    return;
                }

                UpdateLibraryEntry(updatedEntry.Path, _ => updatedEntry);

                await _dispatcher.InvokeAsync(() =>
                {
                    ApplyMetadataToCollections(updatedEntry);
                    StatusMessage = $"Metadata updated for {displayName}.";
                });

                await _libraryStore.SaveAsync(_library).ConfigureAwait(false);
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
                    Videos[index] = mapped;
                }
                else
                {
                    Videos.Add(mapped);
                }
            }
            else
            {
                Videos.Add(mapped);
            }

            RegisterVideo(mapped);

            if (SelectedVideo?.LibraryPath == normalized)
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

        private void RequestMetadataProcessing()
        {
            lock (_metadataQueueLock)
            {
                if (_pendingMetadata.Count == 0)
                {
                    return;
                }

                if (_metadataProcessingTask is not null && !_metadataProcessingTask.IsCompleted)
                {
                    return;
                }

                _metadataProcessingTask = Task.Run(ProcessMetadataQueueAsync);
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
            if (item is null)
            {
                return;
            }

            var actors = result.Actors ?? Array.Empty<string>();
            var tags = result.Tags ?? Array.Empty<string>();
            var description = result.Description ?? string.Empty;
            var title = string.IsNullOrWhiteSpace(result.Title) ? item.Title : result.Title.Trim();
            var thumbnailPath = string.IsNullOrWhiteSpace(result.ThumbnailPath) ? item.ThumbnailPath : result.ThumbnailPath;
            var thumbnailUri = ResolveThumbnailPath(thumbnailPath);

            await _dispatcher.InvokeAsync(() =>
            {
                item.Title = title;
                item.ReleaseDate = result.ReleaseDate;
                item.Actors = actors;
                item.Tags = tags;
                item.Description = description;
                item.ThumbnailPath = thumbnailPath;
                item.ThumbnailUri = thumbnailUri;
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

            await _libraryStore.SaveAsync(_library).ConfigureAwait(false);

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

            item.UpdateFileState(absolutePath, entry.SizeBytes, lastModified, VideoPresenceState.Available, createdUtc);
            return item;
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




