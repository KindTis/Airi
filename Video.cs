using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Airi
{
    public enum VideoPresenceState
    {
        Available,
        Missing
    }

    /// <summary>
    /// UI-facing representation of a catalogue entry, including presence state tracking.
    /// </summary>
    public class VideoItem : INotifyPropertyChanged
    {
        private string _sourcePath = string.Empty;
        private long _sizeBytes;
        private DateTime _lastModifiedUtc;
        private VideoPresenceState _presence = VideoPresenceState.Available;
        private string _title = string.Empty;
        private DateOnly? _releaseDate;
        private IReadOnlyList<string> _actors = Array.Empty<string>();
        private IReadOnlyList<string> _tags = Array.Empty<string>();
        private string _thumbnailUri = string.Empty;
        private string _thumbnailPath = string.Empty;
        private string _description = string.Empty;

        public string LibraryPath { get; init; } = string.Empty;

        public string Title
        {
            get => _title;
            set => SetField(ref _title, value ?? string.Empty);
        }

        public DateOnly? ReleaseDate
        {
            get => _releaseDate;
            set
            {
                if (SetField(ref _releaseDate, value))
                {
                    OnPropertyChanged(nameof(ReleaseLabel));
                }
            }
        }

        public IReadOnlyList<string> Actors
        {
            get => _actors;
            set
            {
                var sanitized = value ?? Array.Empty<string>();
                if (SetField(ref _actors, sanitized))
                {
                    OnPropertyChanged(nameof(ActorsLabel));
                }
            }
        }

        public IReadOnlyList<string> Tags
        {
            get => _tags;
            set
            {
                var sanitized = value ?? Array.Empty<string>();
                if (SetField(ref _tags, sanitized))
                {
                    OnPropertyChanged(nameof(TagsLabel));
                }
            }
        }

        public string ThumbnailUri
        {
            get => _thumbnailUri;
            set => SetField(ref _thumbnailUri, value ?? string.Empty);
        }

        public string ThumbnailPath
        {
            get => _thumbnailPath;
            set => SetField(ref _thumbnailPath, value ?? string.Empty);
        }

        public string Description
        {
            get => _description;
            set => SetField(ref _description, value ?? string.Empty);
        }

        public string SourcePath
        {
            get => _sourcePath;
            private set => SetField(ref _sourcePath, value ?? string.Empty);
        }

        public long SizeBytes
        {
            get => _sizeBytes;
            private set => SetField(ref _sizeBytes, value);
        }

        public DateTime LastModifiedUtc
        {
            get => _lastModifiedUtc;
            private set => SetField(ref _lastModifiedUtc, value);
        }

        public VideoPresenceState Presence
        {
            get => _presence;
            set
            {
                if (SetField(ref _presence, value))
                {
                    OnPropertyChanged(nameof(IsMissing));
                }
            }
        }

        public bool IsMissing => Presence == VideoPresenceState.Missing;

        public string ActorsLabel => Actors.Count == 0 ? string.Empty : string.Join(", ", Actors);
        public string TagsLabel => Tags.Count == 0 ? string.Empty : string.Join(", ", Tags);
        public string ReleaseLabel => ReleaseDate?.ToString("yyyy-MM-dd") ?? "Date TBD";

        public event PropertyChangedEventHandler? PropertyChanged;

        public void UpdateFileState(string sourcePath, long sizeBytes, DateTime lastModifiedUtc, VideoPresenceState presence)
        {
            SourcePath = sourcePath;
            SizeBytes = sizeBytes;
            LastModifiedUtc = lastModifiedUtc;
            Presence = presence;
        }

        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }
}
