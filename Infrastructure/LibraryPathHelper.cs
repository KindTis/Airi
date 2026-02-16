using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Airi.Infrastructure
{
    public static class LibraryPathHelper
    {
        private static readonly string BaseDirectory = AppDomain.CurrentDomain.BaseDirectory;
        public static string NormalizeFileName(string fileName) => NormalizeCode(fileName);

        public static string NormalizeCode(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();

            var fc2Match = Regex.Match(trimmed, @"^FC2[-_\s]*PPV[-_\s]*(\d+)", RegexOptions.IgnoreCase);
            if (fc2Match.Success)
            {
                return $"FC2PPV{fc2Match.Groups[1].Value}";
            }

            var genericMatch = Regex.Match(trimmed, @"^([A-Za-z]{3,})[-_\s]*(\d+)", RegexOptions.IgnoreCase);
            if (genericMatch.Success)
            {
                return genericMatch.Groups[1].Value.ToUpperInvariant() + genericMatch.Groups[2].Value;
            }

            var letters = Regex.Replace(trimmed, "[^A-Za-z]", string.Empty).ToUpperInvariant();
            var digits = Regex.Replace(trimmed, "[^0-9]", string.Empty);

            return digits.Length > 0 ? letters + digits : letters;
        }
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




