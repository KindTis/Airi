using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using Airi.Infrastructure;

namespace Airi.ViewModels
{
    /// <summary>
    /// Provides placeholder data and simple interactions so the app can run offline while the full pipeline is implemented.
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private const string AllActorsLabel = "All Actors";
        private readonly Random _random = new();
        private string _searchQuery = string.Empty;
        private string _selectedActor = AllActorsLabel;
        private string _statusMessage = string.Empty;

        public ObservableCollection<VideoItem> Videos { get; }
        public ObservableCollection<string> Actors { get; }
        public ICollectionView FilteredVideos { get; }

        public RelayCommand SortByTitleCommand { get; }
        public RelayCommand RandomPlayCommand { get; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel()
        {
            Videos = new ObservableCollection<VideoItem>(CreateStubVideos());
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

        private static IEnumerable<VideoItem> CreateStubVideos()
        {
            var baseDirectory = AppDomain.CurrentDomain.BaseDirectory;
            var thumbnail = Path.Combine(baseDirectory, "resources", "noimage.jpg");
            var thumbnailUri = File.Exists(thumbnail)
                ? new Uri(thumbnail).AbsoluteUri
                : string.Empty;

            return new[]
            {
                new VideoItem
                {
                    Title = "Forest Gump",
                    ReleaseDate = new DateOnly(1994, 7, 6),
                    Actors = new[] { "Tom Hanks" },
                    ThumbnailUri = thumbnailUri,
                    SourcePath = "C:/Videos/forest-gump.mp4"
                },
                new VideoItem
                {
                    Title = "The Devil Wears Prada",
                    ReleaseDate = new DateOnly(2006, 6, 30),
                    Actors = new[] { "Meryl Streep" },
                    ThumbnailUri = thumbnailUri,
                    SourcePath = "C:/Videos/devil-wears-prada.mp4"
                },
                new VideoItem
                {
                    Title = "Inception",
                    ReleaseDate = new DateOnly(2010, 7, 16),
                    Actors = new[] { "Leonardo DiCaprio", "Tom Hardy" },
                    ThumbnailUri = thumbnailUri,
                    SourcePath = "C:/Videos/inception.mp4"
                },
                new VideoItem
                {
                    Title = "Black Widow",
                    ReleaseDate = new DateOnly(2021, 7, 9),
                    Actors = new[] { "Scarlett Johansson", "Florence Pugh" },
                    ThumbnailUri = thumbnailUri,
                    SourcePath = "C:/Videos/black-widow.mp4"
                },
                new VideoItem
                {
                    Title = "Fight Club",
                    ReleaseDate = new DateOnly(1999, 10, 15),
                    Actors = new[] { "Brad Pitt", "Edward Norton" },
                    ThumbnailUri = thumbnailUri,
                    SourcePath = "C:/Videos/fight-club.mp4"
                }
            };
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
