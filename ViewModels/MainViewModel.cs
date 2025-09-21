using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        private readonly Random _random = new();
        private readonly LibraryStore _libraryStore;
        private readonly LibraryScanner _libraryScanner;
        private readonly WebMetadataService _webMetadataService;
        private readonly Dispatcher _dispatcher;
        private readonly Dictionary<string, VideoItem> _videoIndex = new(StringComparer.OrdinalIgnoreCase);

        private LibraryData _library;
        private string _searchQuery = string.Empty;
        private string _selectedActor = AllActorsLabel;
        private string _statusMessage = string.Empty;
        private VideoItem? _selectedVideo;
        private bool _isScanning;
        private bool _isFetchingMetadata;

        public ObservableCollection<VideoItem> Videos { get; }
        public ObservableCollection<string> Actors { get; }
        public ICollectionView FilteredVideos { get; }

        public RelayCommand SortByTitleCommand { get; }
        public RelayCommand RandomPlayCommand { get; }
        public RelayCommand FetchMetadataCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(LibraryStore libraryStore, LibraryScanner libraryScanner, WebMetadataService webMetadataService)
        {
            _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
            _webMetadataService = webMetadataService ?? throw new ArgumentNullException(nameof(webMetadataService));
            _dispatcher = Application.Current.Dispatcher;

            _library = _libraryStore.LoadAsync().GetAwaiter().GetResult();
            AppLogger.Info($"Library loaded. Videos: {_library.Videos.Count}.");

            Videos = new ObservableCollection<VideoItem>(_library.Videos.Select(MapVideo));
            foreach (var video in Videos)
            {
                RegisterVideo(video);
            }

            Actors = new ObservableCollection<string>(BuildActorList(Videos));

            FilteredVideos = CollectionViewSource.GetDefaultView(Videos);
            FilteredVideos.Filter = FilterVideo;

            SortByTitleCommand = new RelayCommand(_ => ApplyTitleSort());
            RandomPlayCommand = new RelayCommand(
                _ => PickRandomVideo(),
                _ => FilteredVideos.Cast<VideoItem>().Any(v => v.Presence == VideoPresenceState.Available));
            FetchMetadataCommand = new RelayCommand(async _ => await FetchSelectedMetadataAsync().ConfigureAwait(false), _ => CanFetchMetadata());

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

        public VideoItem? SelectedVideo
        {
            get => _selectedVideo;
            set
            {
                if (SetProperty(ref _selectedVideo, value))
                {
                    FetchMetadataCommand.RaiseCanExecuteChanged();
                }
            }
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
                    FetchMetadataCommand.RaiseCanExecuteChanged();
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
                    UpdateStatus(updateMessage: false);
                    FetchMetadataCommand.RaiseCanExecuteChanged();
                }
            }
        }

        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            await RunInitialScanAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task RunInitialScanAsync(CancellationToken cancellationToken)
        {
            if (IsScanning)
            {
                return;
            }

            IsScanning = true;
            StatusMessage = "Scanning library...";
            AppLogger.Info("Initiating initial library scan.");

            try
            {
                var result = await _libraryScanner.ScanAsync(_library, cancellationToken).ConfigureAwait(false);

                await _dispatcher.InvokeAsync(() =>
                {
                    ApplyScanResult(result);
                    FilteredVideos.Refresh();
                });

                await _libraryStore.SaveAsync(_library).ConfigureAwait(false);

                StatusMessage = $"Scan complete: {result.NewFiles.Count} added, {result.MissingEntries.Count} missing, {result.UpdatedEntries.Count} updated.";
                AppLogger.Info(StatusMessage);
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Scan cancelled.";
                AppLogger.Info(StatusMessage);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Scan failed: {ex.Message}";
                AppLogger.Error(StatusMessage, ex);
            }
            finally
            {
                IsScanning = false;
                UpdateStatus(updateMessage: false);
            }
        }

        private async Task FetchSelectedMetadataAsync()
        {
            if (SelectedVideo is null)
            {
                return;
            }

            var entry = FindEntry(SelectedVideo.LibraryPath);
            if (entry is null)
            {
                AppLogger.Info($"Metadata fetch skipped. Entry not found for {SelectedVideo.LibraryPath}.");
                return;
            }

            var query = string.IsNullOrWhiteSpace(SearchQuery) ? SelectedVideo.Title : SearchQuery;
            if (string.IsNullOrWhiteSpace(query))
            {
                AppLogger.Info("Metadata enrichment skipped: query is empty.");
                return;
            }

            IsFetchingMetadata = true;
            StatusMessage = $"Fetching metadata for '{SelectedVideo.Title}'...";

            try
            {
                var updatedEntry = await _webMetadataService.EnrichAsync(entry, query, CancellationToken.None).ConfigureAwait(false);
                if (updatedEntry is null)
                {
                    StatusMessage = $"No metadata found for '{query}'.";
                    AppLogger.Info(StatusMessage);
                    return;
                }

                UpdateLibraryEntry(updatedEntry.Path, _ => updatedEntry);

                await _dispatcher.InvokeAsync(() =>
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
                    SelectedVideo = mapped;
                    RefreshActorList();
                    FilteredVideos.Refresh();
                });

                await _libraryStore.SaveAsync(_library).ConfigureAwait(false);
                StatusMessage = $"Metadata updated for '{SelectedVideo.Title}'.";
                AppLogger.Info(StatusMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StatusMessage = $"Metadata fetch failed: {ex.Message}";
                AppLogger.Error(StatusMessage, ex);
            }
            finally
            {
                IsFetchingMetadata = false;
                UpdateStatus(updateMessage: false);
            }
        }

        private bool CanFetchMetadata() => SelectedVideo is not null && !IsScanning && !IsFetchingMetadata;

        private void ApplyScanResult(LibraryScanResult result)
        {
            var snapshotMap = result.Snapshots.ToDictionary(s => LibraryPathHelper.NormalizeLibraryPath(s.LibraryPath), StringComparer.OrdinalIgnoreCase);

            foreach (var video in Videos)
            {
                var key = LibraryPathHelper.NormalizeLibraryPath(video.LibraryPath);
                if (snapshotMap.TryGetValue(key, out var snapshot))
                {
                    video.UpdateFileState(snapshot.AbsolutePath, snapshot.SizeBytes, snapshot.LastWriteUtc, VideoPresenceState.Available);
                }
                else
                {
                    var absolute = LibraryPathHelper.ResolveToAbsolute(video.LibraryPath);
                    video.UpdateFileState(absolute, video.SizeBytes, video.LastModifiedUtc, VideoPresenceState.Missing);
                    AppLogger.Info($"Marked missing: {video.LibraryPath}");
                }
            }

            foreach (var updated in result.UpdatedEntries)
            {
                UpdateLibraryEntry(updated.Entry.Path, _ => updated.Entry with
                {
                    SizeBytes = updated.Snapshot.SizeBytes,
                    LastModifiedUtc = updated.Snapshot.LastWriteUtc
                });

                if (_videoIndex.TryGetValue(LibraryPathHelper.NormalizeLibraryPath(updated.Entry.Path), out var video))
                {
                    video.UpdateFileState(updated.Snapshot.AbsolutePath, updated.Snapshot.SizeBytes, updated.Snapshot.LastWriteUtc, VideoPresenceState.Available);
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
            }

            var scanTimestamp = DateTime.UtcNow;
            _library.Targets = _library.Targets
                .Select(t => t with { LastScanUtc = scanTimestamp })
                .ToList();

            RefreshActorList();
            if (SelectedVideo is null && Videos.Count > 0)
            {
                SelectedVideo = Videos[0];
            }

            UpdateStatus(updateMessage: false);
        }

        private void ApplyTitleSort()
        {
            FilteredVideos.SortDescriptions.Clear();
            FilteredVideos.SortDescriptions.Add(new SortDescription(nameof(VideoItem.Title), ListSortDirection.Ascending));
            UpdateStatus();
        }

        private void PickRandomVideo()
        {
            var candidates = FilteredVideos.Cast<VideoItem>()
                .Where(v => v.Presence == VideoPresenceState.Available)
                .ToList();

            if (candidates.Count == 0)
            {
                return;
            }

            var choice = candidates[_random.Next(candidates.Count)];
            StatusMessage = $"Random pick: {choice.Title}";
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

            return matchesActor && matchesSearch;
        }

        private void UpdateStatus(bool updateMessage = true)
        {
            RandomPlayCommand.RaiseCanExecuteChanged();

            if (!updateMessage || IsScanning || IsFetchingMetadata)
            {
                return;
            }

            var visibleAvailable = FilteredVideos.Cast<VideoItem>().Count(v => v.Presence == VideoPresenceState.Available);
            var totalAvailable = Videos.Count(v => v.Presence == VideoPresenceState.Available);
            StatusMessage = $"Showing {visibleAvailable} of {totalAvailable} available videos";
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

            var item = new VideoItem
            {
                LibraryPath = libraryPath,
                Title = entry.Meta.Title,
                ReleaseDate = entry.Meta.Date,
                Actors = entry.Meta.Actors,
                ThumbnailUri = ResolveThumbnailPath(entry.Meta.Thumbnail)
            };

            item.UpdateFileState(absolutePath, entry.SizeBytes, lastModified, VideoPresenceState.Available);
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
                Array.Empty<string>());

            return new VideoEntry(snapshot.LibraryPath, meta, snapshot.SizeBytes, snapshot.LastWriteUtc);
        }

        private string ResolveThumbnailPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var candidate = LibraryPathHelper.ResolveToAbsolute(path);
                if (File.Exists(candidate))
                {
                    return new Uri(candidate).AbsoluteUri;
                }
            }

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



