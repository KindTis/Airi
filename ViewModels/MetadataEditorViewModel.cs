using Airi;
using Airi.Infrastructure;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Airi.ViewModels
{
    public sealed class MetadataEditorViewModel : INotifyPropertyChanged
    {
        private readonly ThumbnailCache _thumbnailCache;
        private readonly string _thumbnailKey;
        private readonly string _initialThumbnailPath;
        private readonly string _initialThumbnailPreviewUri;
        private readonly string _fallbackThumbnailPath;
        private readonly string _fallbackThumbnailPreviewUri;

        private string _title = string.Empty;
        private DateTime? _releaseDate;
        private string _description = string.Empty;
        private string _actorsText = string.Empty;
        private string _tagsText = string.Empty;
        private string _thumbnailPath = string.Empty;
        private string _thumbnailPreviewUri = string.Empty;
        private string _thumbnailDisplayName = string.Empty;

        public MetadataEditorViewModel(VideoItem item)
        {
            if (item is null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            _thumbnailCache = new ThumbnailCache();
            _thumbnailKey = DetermineThumbnailKey(item);
            _fallbackThumbnailPath = LibraryPathHelper.NormalizeLibraryPath(@".\resources\noimage.jpg");
            _fallbackThumbnailPreviewUri = GetFallbackPreviewUri();

            _title = item.Title;
            _releaseDate = item.ReleaseDate?.ToDateTime(TimeOnly.MinValue);
            _description = item.Description;
            _actorsText = string.Join(Environment.NewLine, item.Actors);
            _tagsText = string.Join(Environment.NewLine, item.Tags);

            _initialThumbnailPath = item.ThumbnailPath ?? string.Empty;
            _initialThumbnailPreviewUri = string.IsNullOrWhiteSpace(item.ThumbnailUri)
                ? _fallbackThumbnailPreviewUri
                : item.ThumbnailUri;

            ThumbnailPath = _initialThumbnailPath;
            ThumbnailPreviewUri = _initialThumbnailPreviewUri;
            ThumbnailDisplayName = BuildDisplayName(_initialThumbnailPath);
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

        public string ThumbnailPath
        {
            get => _thumbnailPath;
            private set => SetProperty(ref _thumbnailPath, value ?? string.Empty);
        }

        public string ThumbnailPreviewUri
        {
            get => _thumbnailPreviewUri;
            private set => SetProperty(ref _thumbnailPreviewUri, value ?? string.Empty);
        }

        public string ThumbnailDisplayName
        {
            get => _thumbnailDisplayName;
            private set => SetProperty(ref _thumbnailDisplayName, value ?? string.Empty);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public async Task<bool> UpdateThumbnailFromBytesAsync(byte[] bytes, string? extension = null, string? displayName = null, CancellationToken cancellationToken = default)
        {
            if (bytes is null || bytes.Length == 0)
            {
                return false;
            }

            var relativePath = await _thumbnailCache.SaveAsync(bytes, extension ?? string.Empty, _thumbnailKey, cancellationToken).ConfigureAwait(false);
            var absolutePath = LibraryPathHelper.ResolveToAbsolute(relativePath);
            var previewUri = File.Exists(absolutePath) ? new Uri(absolutePath).AbsoluteUri : ThumbnailPreviewUri;

            ThumbnailPath = relativePath;
            ThumbnailPreviewUri = previewUri;
            ThumbnailDisplayName = string.IsNullOrWhiteSpace(displayName)
                ? BuildDisplayName(relativePath)
                : displayName;

            return true;
        }

        public async Task<bool> UpdateThumbnailFromFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return false;
            }

            var extension = Path.GetExtension(filePath);
            var relativePath = await _thumbnailCache.SaveAsync(bytes, extension, _thumbnailKey, cancellationToken).ConfigureAwait(false);
            var absolutePath = LibraryPathHelper.ResolveToAbsolute(relativePath);
            var previewUri = File.Exists(absolutePath) ? new Uri(absolutePath).AbsoluteUri : ThumbnailPreviewUri;

            ThumbnailPath = relativePath;
            ThumbnailPreviewUri = previewUri;
            ThumbnailDisplayName = Path.GetFileName(filePath);

            return true;
        }

        public void ResetThumbnail()
        {
            ThumbnailPath = _fallbackThumbnailPath;
            ThumbnailPreviewUri = _fallbackThumbnailPreviewUri;
            ThumbnailDisplayName = BuildDisplayName(string.Empty);
        }

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

            result = new MetadataEditResult(title, releaseDate, actors, tags, description, ThumbnailPath);
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

        private static string DetermineThumbnailKey(VideoItem item)
        {
            var normalized = LibraryPathHelper.NormalizeCode(item.Title);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                return normalized;
            }

            var fileName = Path.GetFileNameWithoutExtension(item.LibraryPath);
            return string.IsNullOrWhiteSpace(fileName) ? "thumb" : fileName;
        }

        private static string BuildDisplayName(string path)
        {
            return string.IsNullOrWhiteSpace(path) ? "\uAE30\uBCF8 \uC774\uBBF8\uC9C0 \uC0AC\uC6A9 \uC911" : Path.GetFileName(path);
        }


        private static string GetFallbackPreviewUri()
        {
            var fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "noimage.jpg");
            return File.Exists(fallbackPath) ? new Uri(fallbackPath).AbsoluteUri : string.Empty;
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
        string Description,
        string ThumbnailPath);
}
