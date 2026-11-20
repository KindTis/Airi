using System;
using System.IO;
using System.Collections.Concurrent;
using System.Windows.Media.Imaging;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Airi.Infrastructure
{
    public sealed class ThumbnailCache
    {
        private readonly string _cacheDirectory;
        private readonly ConcurrentDictionary<string, byte> _owned = new(StringComparer.OrdinalIgnoreCase);

        public ThumbnailCache(string? baseDirectory = null)
        {
            var root = baseDirectory ?? AppDomain.CurrentDomain.BaseDirectory;
            _cacheDirectory = Path.Combine(root, "cache");
            Directory.CreateDirectory(_cacheDirectory);
        }

        public async Task<string> SaveAsync(byte[] bytes, string extension, string key, CancellationToken cancellationToken = default)
        {
            if (bytes.Length == 0)
            {
                throw new ArgumentException("Thumbnail data must not be empty", nameof(bytes));
            }

            ValidateImage(bytes);
            var normalizedExtension = NormalizeExtension(extension);
            var safeKey = Sanitize(key);
            var fileName = $"{safeKey}_{Guid.NewGuid():N}{normalizedExtension}";
            var fullPath = Path.Combine(_cacheDirectory, fileName);

            var relative = LibraryPathHelper.NormalizeLibraryPath(Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, fullPath));
            _owned.TryAdd(relative, 0);
            try
            {
                await using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                return relative;
            }
            catch { DiscardOwned(relative); throw; }
        }

        public void AcceptOwned(string path) => _owned.TryRemove(path, out _);

        public void DiscardOwned(string path)
        {
            if (_owned.TryRemove(path, out _))
            {
                try { File.Delete(LibraryPathHelper.ResolveToAbsolute(path)); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void ValidateImage(byte[] bytes)
        {
            if (bytes.Length > 32 * 1024 * 1024) throw new InvalidDataException("이미지는 32MB 이하여야 합니다.");
            using var stream = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("이미지를 읽을 수 없습니다.");
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 || frame.PixelWidth > 32768 || frame.PixelHeight > 32768 ||
                (long)frame.PixelWidth * frame.PixelHeight > 100_000_000)
                throw new InvalidDataException("이미지 크기를 확인해 주세요.");
        }

        private static string NormalizeExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return ".jpg";
            }

            extension = extension.Trim();
            return extension.StartsWith('.') ? extension : "." + extension;
        }

        private static string Sanitize(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "thumb";
            }

            var builder = new StringBuilder(key.Length);
            foreach (var ch in key)
            {
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }

            return builder.ToString();
        }
    }
}
