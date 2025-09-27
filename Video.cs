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

        public string LibraryPath { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public DateOnly? ReleaseDate { get; init; }
        public IReadOnlyList<string> Actors { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
        public string ThumbnailUri { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;

        public string SourcePath
        {
            get => _sourcePath;
            private set => SetField(ref _sourcePath, value);
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

