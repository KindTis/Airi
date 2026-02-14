using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace Airi.ViewModels
{
    /// <summary>
    /// Loads persisted library data, exposes UI-facing collections, and coordinates scanning/diff and web metadata enrichment.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string AllActorsLabel = "All Actors";
        private const string CrawlerSeedUrl = "https://example.com/";
        private static readonly HttpClient CrawlerThumbnailHttpClient = new();
        private readonly Random _random = new();
        private readonly LibraryStore _libraryStore;
        private readonly LibraryScanner _libraryScanner;
        private readonly WebMetadataService _webMetadataService;
        private readonly Dispatcher _dispatcher;
        private readonly OneFourOneJavCrawler _oneFourOneJavCrawler;
        private readonly ThumbnailCache _thumbnailCache;
        private readonly Dictionary<string, VideoItem> _videoIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _metadataQueueLock = new();
        private readonly Queue<string> _pendingMetadata = new();
        private readonly HashSet<string> _metadataScheduled = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _thumbnailUriCache = new(StringComparer.OrdinalIgnoreCase);
        private Task _metadataProcessingTask = Task.CompletedTask;
        private readonly string _fallbackThumbnailUri;
        private bool _isInitialized;
        private readonly SemaphoreSlim _initializationGate = new(1, 1);
        private ChromeDriverService? _crawlerService;
        private IWebDriver? _crawlerDriver;
        private OneFourOneJavCrawler.CrawlerSession? _crawlerSession;
        private Task? _crawlerMonitorTask;
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
            OneFourOneJavCrawler oneFourOneJavCrawler,
            ThumbnailCache thumbnailCache)
        {
            _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
            _webMetadataService = webMetadataService ?? throw new ArgumentNullException(nameof(webMetadataService));
            _oneFourOneJavCrawler = oneFourOneJavCrawler ?? throw new ArgumentNullException(nameof(oneFourOneJavCrawler));
            _thumbnailCache = thumbnailCache ?? throw new ArgumentNullException(nameof(thumbnailCache));
            _dispatcher = Application.Current.Dispatcher;
            _library = new LibraryData();
            _fallbackThumbnailUri = GetFallbackThumbnailUri();

            Videos = new ObservableCollection<VideoItem>();
            Actors = new ObservableCollection<string>(BuildActorList(Videos));
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
            StartCrawlerCommand = new RelayCommand(async _ => await StartCrawlerAsync().ConfigureAwait(false), _ => !IsCrawlerRunning);

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

                await _dispatcher.InvokeAsync(() =>
                {
                    LoadLibraryData(loadedLibrary, mappedVideos);
                    StatusMessage = BuildLibrarySummary();
                });

                _isInitialized = true;
                _ = RunStartupScanAsync(cancellationToken);
            }
            finally
            {
                _initializationGate.Release();
            }
        }

        private void LoadLibraryData(LibraryData data, IEnumerable<VideoItem> mappedVideos)
        {
            _library = data ?? new LibraryData();

            Videos.Clear();
            _videoIndex.Clear();

            foreach (var item in mappedVideos)
            {
                RegisterVideo(item);
                Videos.Add(item);
            }

            RefreshActorList();
            FilteredVideos.Refresh();
            SelectedVideo = Videos.FirstOrDefault();
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

            if (!await EnsureCrawlerReadyAsync().ConfigureAwait(false))
            {
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                IsFetchingMetadata = true;
                StatusMessage = $"Crawler metadata fetch starting for {missing.Count} videos.";
            });

            AppLogger.Info($"[Crawler] Starting metadata fetch for {missing.Count} videos with missing metadata.");

            try
            {
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

                    var searchUrl = $"https://www.141jav.com/search/{Uri.EscapeDataString(query)}";
                    var navigated = await NavigateCrawlerToAsync(searchUrl).ConfigureAwait(false);
                    if (!navigated)
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"[{index}/{total}] Crawler navigation failed for {displayName}.";
                        });
                        continue;
                    }

                    var metadata = await TryGetCrawlerMetadataAsync().ConfigureAwait(false);
                    if (metadata is null)
                    {
                        AppLogger.Info($"[Crawler] No metadata returned for {displayName}.");
                        await _dispatcher.InvokeAsync(() =>
                        {
                            StatusMessage = $"[{index}/{total}] No crawler metadata for {displayName}.";
                        });
                        continue;
                    }

                    var thumbnailUrl = await TryGetCrawlerThumbnailUrlAsync().ConfigureAwait(false);
                    var updatedEntry = await ApplyCrawlerMetadataAsync(entry, metadata, thumbnailUrl, video).ConfigureAwait(false);
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
                    StatusMessage = BuildLibrarySummary();
                });

                AppLogger.Info("[Crawler] Metadata fetch completed.");
            }
        }

        private async Task<bool> EnsureCrawlerReadyAsync()
        {
            if (_crawlerDriver is not null && _crawlerSession is not null)
            {
                return true;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                StatusMessage = "Crawler is not running. Start the crawler to fetch metadata.";
                MessageBox.Show(
                    "Start the crawler before fetching metadata. Click the Start Crawler button and wait for the browser to open.",
                    "Crawler Not Ready",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            });

            return false;
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

        private static string DetermineThumbnailKey(VideoItem video, VideoEntry entry)
        {
            if (video is not null)
            {
                var normalized = LibraryPathHelper.NormalizeCode(video.Title);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    return normalized;
                }
            }

            var fallback = string.IsNullOrWhiteSpace(entry.Path)
                ? video?.LibraryPath ?? "thumb"
                : entry.Path;

            var fileStem = Path.GetFileNameWithoutExtension(fallback);
            var key = LibraryPathHelper.NormalizeCode(fileStem);
            return string.IsNullOrWhiteSpace(key) ? "thumb" : key;
        }

        private async Task<VideoEntry?> ApplyCrawlerMetadataAsync(
            VideoEntry entry,
            OneFourOneJavCrawler.CrawlerMetadata metadata,
            string? thumbnailUrl,
            VideoItem video)
        {
            if (metadata is null)
            {
                return null;
            }

            var updatedMeta = entry.Meta;
            var hasChanges = false;

            if (metadata.ReleaseDate is DateTime releaseDate)
            {
                updatedMeta = updatedMeta with { Date = DateOnly.FromDateTime(releaseDate.Date) };
                hasChanges = true;
            }

            if (metadata.Tags.Count > 0)
            {
                updatedMeta = updatedMeta with { Tags = metadata.Tags };
                hasChanges = true;
            }

            if (metadata.Actors.Count > 0)
            {
                updatedMeta = updatedMeta with { Actors = metadata.Actors };
                hasChanges = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Description))
            {
                updatedMeta = updatedMeta with { Description = metadata.Description };
                hasChanges = true;
            }

            if (!string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                var cacheKey = DetermineThumbnailKey(video, entry);
                var thumbnailPath = await DownloadCrawlerThumbnailAsync(thumbnailUrl, cacheKey).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(thumbnailPath))
                {
                    updatedMeta = updatedMeta with { Thumbnail = thumbnailPath };
                    hasChanges = true;
                }
            }

            return hasChanges ? entry with { Meta = updatedMeta } : null;
        }

        private async Task<string?> DownloadCrawlerThumbnailAsync(string imageUrl, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return null;
            }

            try
            {
                var bytes = await CrawlerThumbnailHttpClient.GetByteArrayAsync(imageUrl).ConfigureAwait(false);
                if (bytes.Length == 0)
                {
                    return null;
                }

                var extension = ".jpg";

                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri))
                {
                    var candidate = Path.GetExtension(uri.LocalPath);
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        extension = candidate;
                    }
                }
                else
                {
                    var candidate = Path.GetExtension(imageUrl);
                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        extension = candidate;
                    }
                }

                return await _thumbnailCache.SaveAsync(bytes, extension, cacheKey).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                AppLogger.Error($"[Crawler] Failed to download thumbnail from {imageUrl}.", ex);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                AppLogger.Error($"[Crawler] Thumbnail download timed out for {imageUrl}.", ex);
                return null;
            }
        }


        private async Task StartCrawlerAsync()
        {
            if (_crawlerDriver is not null)
            {
                StatusMessage = "Crawler already running. Close the browser window to start a new session.";
                return;
            }

            await _dispatcher.InvokeAsync(() =>
            {
                IsCrawlerRunning = true;
                StatusMessage = "Crawler starting...";
            });

            AppLogger.Info("Starting Selenium crawler.");

            try
            {
                var result = await Task.Run<(ChromeDriverService service, ChromeDriver driver, string summary)>(() =>
                {
                    var service = ChromeDriverService.CreateDefaultService();
                    service.HideCommandPromptWindow = true;

                    var options = new ChromeOptions();
                    options.AddArgument("--disable-gpu");
                    options.AddArgument("--no-sandbox");
                    options.AddArgument("--disable-dev-shm-usage");
                    options.AddArgument("--log-level=3");

                    var driver = new ChromeDriver(service, options);
                    try
                    {
                        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(30);
                        driver.Navigate().GoToUrl(CrawlerSeedUrl);

                        var title = driver.Title ?? string.Empty;
                        var heading = string.Empty;

                        try
                        {
                            heading = driver.FindElements(By.TagName("h1"))
                                .Select(element => element.Text)
                                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;
                        }
                        catch (NoSuchElementException)
                        {
                            // Intentionally ignored; not all pages include a heading.
                        }

                        var highlight = string.IsNullOrWhiteSpace(heading) ? title : heading;
                        var summary = string.IsNullOrWhiteSpace(highlight)
                            ? "Crawler opened the page. Close the browser window when you are finished."
                            : $"Crawler opened \"{highlight}\". Close the browser window when you are finished.";

                        AppLogger.Info($"Crawler visited {CrawlerSeedUrl} (title: {title}).");

                        return (service, driver, summary);
                    }
                    catch
                    {
                        driver.Quit();
                        driver.Dispose();
                        service.Dispose();
                        throw;
                    }
                }).ConfigureAwait(false);

                await _dispatcher.InvokeAsync(() =>
                {
                    _crawlerService = result.service;
                    _crawlerDriver = result.driver;
                    _crawlerSession = _oneFourOneJavCrawler.CreateSession(result.driver);
                    StatusMessage = result.summary;
                });

                StartCrawlerMonitor();
            }
            catch (WebDriverException ex)
            {
                DisposeCrawler();

                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Crawler failed: {ex.Message}";
                    IsCrawlerRunning = false;
                });

                AppLogger.Error("Crawler failed while using Selenium.", ex);
            }
            catch (Exception ex)
            {
                DisposeCrawler();

                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Crawler failed: {ex.Message}";
                    IsCrawlerRunning = false;
                });

                AppLogger.Error("Unexpected crawler failure.", ex);
            }
        }


        public async Task<string?> TryGetCrawlerThumbnailUrlAsync(CancellationToken cancellationToken = default)
        {
            var session = _crawlerSession;
            if (session is null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "Crawler is not running. Start the crawler first.";
                });
                return null;
            }

            return await session
                .TryGetThumbnailUrlAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<OneFourOneJavCrawler.CrawlerMetadata?> TryGetCrawlerMetadataAsync(CancellationToken cancellationToken = default)
        {
            var session = _crawlerSession;
            if (session is null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "Crawler is not running. Start the crawler first.";
                });
                return null;
            }

            return await session
                .TryGetMetadataAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        public async Task<bool> NavigateCrawlerToAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var driver = _crawlerDriver;
            if (driver is null)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = "Crawler is not running. Start the crawler first.";
                });
                return false;
            }

            try
            {
                await Task.Run(() => driver.Navigate().GoToUrl(url)).ConfigureAwait(false);

                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Crawler navigating to {url}.";
                });

                return true;
            }
            catch (WebDriverException ex)
            {
                AppLogger.Error($"Crawler navigation failed for {url}.", ex);

                await _dispatcher.InvokeAsync(() =>
                {
                    StatusMessage = $"Crawler failed to navigate: {ex.Message}";
                });

                DisposeCrawler();
                await _dispatcher.InvokeAsync(() => IsCrawlerRunning = false);
                return false;
            }
        }

        private void StartCrawlerMonitor()
        {
            if (_crawlerMonitorTask is { IsCompleted: false })
            {
                return;
            }

            _crawlerMonitorTask = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var driver = _crawlerDriver;
                        if (driver is null)
                        {
                            break;
                        }

                        try
                        {
                            if (driver.WindowHandles.Count == 0)
                            {
                                break;
                            }
                        }
                        catch (WebDriverException)
                        {
                            break;
                        }

                        await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    DisposeCrawler();

                    await _dispatcher.InvokeAsync(() =>
                    {
                        IsCrawlerRunning = false;
                        StatusMessage = BuildLibrarySummary();
                    });

                    _crawlerMonitorTask = null;
                }
            });
        }

        private void DisposeCrawler()
        {
            _crawlerSession = null;
            var driver = _crawlerDriver;
            _crawlerDriver = null;

            if (driver is not null)
            {
                try
                {
                    driver.Quit();
                }
                catch (WebDriverException)
                {
                    // ignored: driver process already gone.
                }

                try
                {
                    driver.Dispose();
                }
                catch (WebDriverException)
                {
                    // ignored: driver process already disposed.
                }
            }

            _crawlerService?.Dispose();
            _crawlerService = null;
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




