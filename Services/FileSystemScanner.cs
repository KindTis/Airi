using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Services
{
    public sealed class FileSystemScanner
    {
        private readonly string _baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

        public Task<IReadOnlyList<FileSnapshot>> ScanAsync(IEnumerable<TargetFolder> targets, CancellationToken cancellationToken)
        {
            return Task.Run(() => ScanInternal(targets, cancellationToken), cancellationToken);
        }

        private IReadOnlyList<FileSnapshot> ScanInternal(IEnumerable<TargetFolder> targets, CancellationToken cancellationToken)
        {
            var results = new List<FileSnapshot>();

            if (targets == null)
            {
                AppLogger.Info("No target folders configured for scan.");
                return results;
            }

            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (target is null)
                {
                    continue;
                }

                var rootPath = ResolveRootPath(target.Root);
                if (!Directory.Exists(rootPath))
                {
                    AppLogger.Info($"Skipping target '{target.Root}' – directory not found ({rootPath}).");
                    continue;
                }

                AppLogger.Info($"Scanning target '{target.Root}' (resolved path: {rootPath}).");

                IEnumerable<string> includePatterns = (target.IncludePatterns?.Count ?? 0) == 0
                    ? new[] { "*" }
                    : target.IncludePatterns ?? Array.Empty<string>();
                IEnumerable<string> excludePatterns = target.ExcludePatterns ?? Array.Empty<string>();

                foreach (var file in Directory.EnumerateFiles(rootPath, "*", new EnumerationOptions
                         {
                             RecurseSubdirectories = true,
                             IgnoreInaccessible = true,
                             AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                         }))
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    var fileName = Path.GetFileName(file);

                    if (!includePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName, true)))
                    {
                        continue;
                    }

                    if (excludePatterns.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, fileName, true)))
                    {
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(rootPath, file);
                    var libraryPath = LibraryPathHelper.Combine(target.Root, relativePath);
                    var info = new FileInfo(file);

                    AppLogger.Info($"Discovered file: {libraryPath}");
                    results.Add(new FileSnapshot(
                        libraryPath,
                        info.FullName,
                        info.Length,
                        info.LastWriteTimeUtc));
                }
            }

            return results;
        }

        private string ResolveRootPath(string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return _baseDirectory;
            }

            if (Path.IsPathRooted(root))
            {
                return Path.GetFullPath(root);
            }

            var trimmed = root.StartsWith("./", StringComparison.Ordinal)
                ? root.Substring(2)
                : root;

            return Path.GetFullPath(Path.Combine(_baseDirectory, trimmed));
        }
    }
}
