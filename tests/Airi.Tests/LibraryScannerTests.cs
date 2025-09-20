using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Xunit;

namespace Airi.Tests
{
    public sealed class LibraryScannerTests : IAsyncLifetime
    {
        private readonly string _root;
        private readonly LibraryScanner _scanner;

        public LibraryScannerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "AiriScannerTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_root);
            _scanner = new LibraryScanner(new FileSystemScanner());
        }

        [Fact]
        public async Task ScanAsync_DetectsNewFiles()
        {
            var filePath = Path.Combine(_root, "new-video.mp4");
            await File.WriteAllTextAsync(filePath, "dummy");

            var library = CreateLibrary();

            var result = await _scanner.ScanAsync(library, CancellationToken.None);

            Assert.Single(result.NewFiles);
            Assert.Equal(Path.GetFullPath(filePath), result.NewFiles[0].AbsolutePath);
        }

        [Fact]
        public async Task ScanAsync_FlagsMissingEntries()
        {
            var entry = new VideoEntry(
                Path.Combine(_root, "missing.mp4"),
                new VideoMeta("Missing", null, Array.Empty<string>(), string.Empty, Array.Empty<string>()));

            var library = CreateLibrary();
            library.Videos.Add(entry);

            var result = await _scanner.ScanAsync(library, CancellationToken.None);

            Assert.Single(result.MissingEntries);
            Assert.Equal(
                LibraryPathHelper.NormalizeLibraryPath(entry.Path),
                LibraryPathHelper.NormalizeLibraryPath(result.MissingEntries[0].Path));
        }

        [Fact]
        public async Task ScanAsync_DetectsModifiedFiles()
        {
            var filePath = Path.Combine(_root, "update.mp4");
            await File.WriteAllTextAsync(filePath, "content");
            var info = new FileInfo(filePath);

            var entry = new VideoEntry(
                filePath,
                new VideoMeta("Update", null, Array.Empty<string>(), string.Empty, Array.Empty<string>()),
                SizeBytes: 0,
                LastModifiedUtc: DateTime.UtcNow.AddDays(-1));

            var library = CreateLibrary();
            library.Videos.Add(entry);

            var result = await _scanner.ScanAsync(library, CancellationToken.None);

            Assert.Single(result.UpdatedEntries);
            Assert.Equal(
                LibraryPathHelper.NormalizeLibraryPath(entry.Path),
                LibraryPathHelper.NormalizeLibraryPath(result.UpdatedEntries[0].Entry.Path));
            Assert.Equal(info.Length, result.UpdatedEntries[0].Snapshot.SizeBytes);
        }

        private LibraryData CreateLibrary()
        {
            return new LibraryData
            {
                Targets = new List<TargetFolder>
                {
                    new(
                        _root,
                        new[] { "*.mp4" },
                        Array.Empty<string>(),
                        null)
                },
                Videos = new List<VideoEntry>()
            };
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
