using System;
using System.IO;

namespace Airi.Infrastructure
{
    public static class LibraryPathHelper
    {
        private static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        public static string NormalizeLibraryPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var trimmed = path.Trim();
            var normalized = trimmed.Replace('\\', '/');

            if (Path.IsPathRooted(trimmed) || normalized.StartsWith("./", StringComparison.Ordinal) || normalized.StartsWith("../", StringComparison.Ordinal))
            {
                return normalized;
            }

            return normalized.StartsWith('/') ? normalized : "./" + normalized.TrimStart('/');
        }

        public static string Combine(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return NormalizeLibraryPath(relative);
            }

            if (string.IsNullOrWhiteSpace(relative))
            {
                return NormalizeLibraryPath(root);
            }

            var combined = Path.Combine(root, relative);
            return NormalizeLibraryPath(combined);
        }

        public static string ResolveToAbsolute(string libraryPath)
        {
            if (string.IsNullOrWhiteSpace(libraryPath))
            {
                return string.Empty;
            }

            if (Path.IsPathRooted(libraryPath))
            {
                return Path.GetFullPath(libraryPath);
            }

            var trimmed = libraryPath.StartsWith("./", StringComparison.Ordinal)
                ? libraryPath.Substring(2)
                : libraryPath;

            return Path.GetFullPath(Path.Combine(BaseDirectory, trimmed));
        }
    }
}
