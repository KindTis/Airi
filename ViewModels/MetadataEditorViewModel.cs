using Airi;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Airi.ViewModels
{
    public sealed class MetadataEditorViewModel : INotifyPropertyChanged
    {
        private string _title = string.Empty;
        private DateTime? _releaseDate;
        private string _description = string.Empty;
        private string _actorsText = string.Empty;
        private string _tagsText = string.Empty;

        public MetadataEditorViewModel(VideoItem item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            _title = item.Title;
            _releaseDate = item.ReleaseDate?.ToDateTime(TimeOnly.MinValue);
            _description = item.Description;
            _actorsText = string.Join(Environment.NewLine, item.Actors);
            _tagsText = string.Join(Environment.NewLine, item.Tags);
        }

        public string Title
        {
            get => _title;
            set => SetProperty(ref _title, value ?? string.Empty);
        }

        public DateTime? ReleaseDate
        {
            get => _releaseDate;
            set => SetProperty(ref _releaseDate, value);
        }

        public string Description
        {
            get => _description;
            set => SetProperty(ref _description, value ?? string.Empty);
        }

        public string ActorsText
        {
            get => _actorsText;
            set => SetProperty(ref _actorsText, value ?? string.Empty);
        }

        public string TagsText
        {
            get => _tagsText;
            set => SetProperty(ref _tagsText, value ?? string.Empty);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool TryBuildResult(out MetadataEditResult result, out string? error)
        {
            error = null;

            var title = (Title ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(title))
            {
                error = "제목을 입력해주세요.";
                result = default;
                return false;
            }

            DateOnly? releaseDate = null;
            if (ReleaseDate is DateTime dt)
            {
                releaseDate = DateOnly.FromDateTime(dt.Date);
            }

            var description = (Description ?? string.Empty).Trim();
            var actors = SplitList(ActorsText);
            var tags = SplitList(TagsText);

            result = new MetadataEditResult(title, releaseDate, actors, tags, description);
            return true;
        }

        private static IReadOnlyList<string> SplitList(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return Array.Empty<string>();
            }

            var tokens = source
                .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(token => token.Trim())
                .Where(token => token.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return tokens.Length == 0 ? Array.Empty<string>() : tokens;
        }

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    public readonly record struct MetadataEditResult(
        string Title,
        DateOnly? ReleaseDate,
        IReadOnlyList<string> Actors,
        IReadOnlyList<string> Tags,
        string Description);
}

