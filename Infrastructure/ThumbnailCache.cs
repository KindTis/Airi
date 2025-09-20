using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Airi.Infrastructure
{
    public sealed class ThumbnailCache
    {
        private readonly string _cacheDirectory;

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

            var normalizedExtension = NormalizeExtension(extension);
            var safeKey = Sanitize(key);
            var fileName = $"{safeKey}_{DateTime.UtcNow:yyyyMMdd_HHmmssfff}{normalizedExtension}";
            var fullPath = Path.Combine(_cacheDirectory, fileName);

            await File.WriteAllBytesAsync(fullPath, bytes, cancellationToken).ConfigureAwait(false);

            var relative = Path.GetRelativePath(AppDomain.CurrentDomain.BaseDirectory, fullPath);
            return LibraryPathHelper.NormalizeLibraryPath(relative);
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
