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

    public sealed class LibraryScanner : ILibraryScanner
    {
        private readonly FileSystemScanner _fileSystemScanner;

        public LibraryScanner(FileSystemScanner fileSystemScanner)
        {
            _fileSystemScanner = fileSystemScanner ?? throw new ArgumentNullException(nameof(fileSystemScanner));
        }

        public async Task<LibraryScanResult> ScanAsync(
            LibraryData library,
            IReadOnlyList<TargetFolder> targets,
            CancellationToken cancellationToken)
        {
            if (library is null)
            {
                throw new ArgumentNullException(nameof(library));
            }
            if (targets is null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            AppLogger.Info("Starting library scan.");
            var snapshots = await _fileSystemScanner.ScanAsync(targets, cancellationToken).ConfigureAwait(false);
            var snapshotMap = snapshots.ToDictionary(s => LibraryPathHelper.NormalizeLibraryPath(s.LibraryPath), StringComparer.OrdinalIgnoreCase);
            var existing = library.Videos.Where(entry => entry is not null).ToArray();
            var existingPaths = existing.Select(entry => LibraryPathHelper.NormalizeLibraryPath(entry.Path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newFiles = snapshotMap.Where(pair => !existingPaths.Contains(pair.Key)).Select(pair => pair.Value).ToList();
            var missingEntries = existing.Where(entry => !library.IsInRecycleBin(entry.Id) &&
                !snapshotMap.ContainsKey(LibraryPathHelper.NormalizeLibraryPath(entry.Path))).ToList();
            var updatedEntries = existing.Where(entry => !library.IsInRecycleBin(entry.Id) &&
                snapshotMap.TryGetValue(LibraryPathHelper.NormalizeLibraryPath(entry.Path), out var snapshot) &&
                (entry.SizeBytes != snapshot.SizeBytes || entry.LastModifiedUtc != snapshot.LastWriteUtc))
                .Select(entry => new UpdatedFile(entry, snapshotMap[LibraryPathHelper.NormalizeLibraryPath(entry.Path)])).ToList();

            AppLogger.Info($"Scan completed. New: {newFiles.Count}, Missing: {missingEntries.Count}, Updated: {updatedEntries.Count}.");

            return new LibraryScanResult(snapshots, newFiles, missingEntries, updatedEntries);
        }
    }
}
