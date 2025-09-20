using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Domain;

namespace Airi.ViewModels
{
    /// <summary>
    /// Loads persisted library data and exposes UI-facing collections/commands.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string AllActorsLabel = "All Actors";
        private readonly Random _random = new();
        private readonly LibraryStore _libraryStore;
        private readonly string _baseDirectory;
        private readonly string _fallbackThumbnail;

        private string _searchQuery = string.Empty;
        private string _selectedActor = AllActorsLabel;
        private string _statusMessage = string.Empty;

        public ObservableCollection<VideoItem> Videos { get; }
        public ObservableCollection<string> Actors { get; }
        public ICollectionView FilteredVideos { get; }

        public RelayCommand SortByTitleCommand { get; }
        public RelayCommand RandomPlayCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(LibraryStore libraryStore)
        {
            _libraryStore = libraryStore ?? throw new ArgumentNullException(nameof(libraryStore));
            _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            _fallbackThumbnail = ComputeFallbackThumbnail();

            var library = _libraryStore.LoadAsync().GetAwaiter().GetResult();
            Videos = new ObservableCollection<VideoItem>(library.Videos.Select(MapVideo));
            Actors = new ObservableCollection<string>(BuildActorList(Videos));

            FilteredVideos = CollectionViewSource.GetDefaultView(Videos);
            FilteredVideos.Filter = FilterVideo;

            SortByTitleCommand = new RelayCommand(_ => ApplyTitleSort());
            RandomPlayCommand = new RelayCommand(_ => PickRandomVideo(), _ => FilteredVideos.Cast<VideoItem>().Any());

            UpdateStatus();
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
                if (SetProperty(ref _selectedActor, value))
                {
                    FilteredVideos.Refresh();
                    UpdateStatus();
                }
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => SetProperty(ref _statusMessage, value);
        }

        private void ApplyTitleSort()
        {
            FilteredVideos.SortDescriptions.Clear();
            FilteredVideos.SortDescriptions.Add(new SortDescription(nameof(VideoItem.Title), ListSortDirection.Ascending));
            UpdateStatus();
        }

        private void PickRandomVideo()
        {
            var snapshot = FilteredVideos.Cast<VideoItem>().ToList();
            if (snapshot.Count == 0)
            {
                return;
            }

            var choice = snapshot[_random.Next(snapshot.Count)];
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

        private void UpdateStatus()
        {
            var visibleCount = FilteredVideos.Cast<VideoItem>().Count();
            StatusMessage = $"Showing {visibleCount} videos";
            RandomPlayCommand.RaiseCanExecuteChanged();
        }

        private VideoItem MapVideo(VideoEntry entry)
        {
            var thumbnailUri = ResolveThumbnailPath(entry.Meta.Thumbnail);
            var sourcePath = ResolveVideoPath(entry.Path);

            return new VideoItem
            {
                Title = entry.Meta.Title,
                ReleaseDate = entry.Meta.Date,
                Actors = entry.Meta.Actors,
                ThumbnailUri = thumbnailUri,
                SourcePath = sourcePath
            };
        }

        private string ResolveVideoPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(path))
            {
                return path;
            }

            return Path.GetFullPath(Path.Combine(_baseDirectory, path));
        }

        private string ResolveThumbnailPath(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var candidate = Path.IsPathRooted(path)
                    ? path
                    : Path.GetFullPath(Path.Combine(_baseDirectory, path));

                if (File.Exists(candidate))
                {
                    return new Uri(candidate).AbsoluteUri;
                }
            }

            return _fallbackThumbnail;
        }

        private string ComputeFallbackThumbnail()
        {
            var fallbackPath = Path.Combine(_baseDirectory, "resources", "noimage.jpg");
            return File.Exists(fallbackPath) ? new Uri(fallbackPath).AbsoluteUri : string.Empty;
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

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
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
