using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;

namespace Airi.Services
{
    public sealed record UpdatedFile(VideoEntry Entry, FileSnapshot Snapshot);

    public sealed record LibraryScanResult(
        IReadOnlyList<FileSnapshot> Snapshots,
        IReadOnlyList<FileSnapshot> NewFiles,
        IReadOnlyList<VideoEntry> MissingEntries,
        IReadOnlyList<UpdatedFile> UpdatedEntries);

    public sealed class LibraryScanner
    {
        private readonly FileSystemScanner _fileSystemScanner;

        public LibraryScanner(FileSystemScanner fileSystemScanner)
        {
            _fileSystemScanner = fileSystemScanner ?? throw new ArgumentNullException(nameof(fileSystemScanner));
        }

        public async Task<LibraryScanResult> ScanAsync(LibraryData library, CancellationToken cancellationToken)
        {
            if (library is null)
            {
                throw new ArgumentNullException(nameof(library));
            }

            AppLogger.Info("Starting library scan.");
            var snapshots = await _fileSystemScanner.ScanAsync(library.Targets, cancellationToken).ConfigureAwait(false);
            var snapshotMap = snapshots.ToDictionary(s => LibraryPathHelper.NormalizeLibraryPath(s.LibraryPath), StringComparer.OrdinalIgnoreCase);
            var existingMap = library.Videos
                .Where(v => v is not null)
                .ToDictionary(v => LibraryPathHelper.NormalizeLibraryPath(v.Path), StringComparer.OrdinalIgnoreCase);

            var newFiles = snapshotMap
                .Where(kvp => !existingMap.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            var missingEntries = existingMap
                .Where(kvp => !snapshotMap.ContainsKey(kvp.Key))
                .Select(kvp => kvp.Value)
                .ToList();

            var updatedEntries = snapshotMap
                .Where(kvp => existingMap.TryGetValue(kvp.Key, out var entry) &&
                              (entry.SizeBytes != kvp.Value.SizeBytes || entry.LastModifiedUtc != kvp.Value.LastWriteUtc))
                .Select(kvp => new UpdatedFile(existingMap[kvp.Key], kvp.Value))
                .ToList();

            AppLogger.Info($"Scan completed. New: {newFiles.Count}, Missing: {missingEntries.Count}, Updated: {updatedEntries.Count}.");

            return new LibraryScanResult(snapshots, newFiles, missingEntries, updatedEntries);
        }
    }
}
