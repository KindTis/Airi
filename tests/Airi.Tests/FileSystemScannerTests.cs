using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Xunit;

namespace Airi.Tests
{
    public sealed class FileSystemScannerTests : IAsyncLifetime
    {
        private readonly string _tempRoot;
        private readonly string _relativeRootName;
        private readonly string _relativeRootAbsolute;
        private readonly FileSystemScanner _scanner = new();

        public FileSystemScannerTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "AiriFileSystemScannerTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempRoot);

            _relativeRootName = $"AiriScannerRelative_{Guid.NewGuid():N}";
            _relativeRootAbsolute = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _relativeRootName);
            Directory.CreateDirectory(_relativeRootAbsolute);
        }

        [Fact]
        public async Task ScanAsync_AppliesIncludeAndExcludePatterns()
        {
            var keepPath = Path.Combine(_tempRoot, "keep.mp4");
            await File.WriteAllTextAsync(keepPath, "content");
            await File.WriteAllTextAsync(Path.Combine(_tempRoot, "skip.mkv"), "content");
            await File.WriteAllTextAsync(Path.Combine(_tempRoot, "ignore.txt"), "content");

            var target = new TargetFolder(
                _tempRoot,
                new[] { "*.mp4", "*.mkv" },
                new[] { "skip*" },
                null);

            var snapshots = await _scanner.ScanAsync(new[] { target }, CancellationToken.None);

            var snapshot = Assert.Single(snapshots);
            Assert.Equal(
                LibraryPathHelper.NormalizeLibraryPath(LibraryPathHelper.Combine(_tempRoot, "keep.mp4")),
                LibraryPathHelper.NormalizeLibraryPath(snapshot.LibraryPath));
            Assert.Equal(Path.GetFullPath(keepPath), snapshot.AbsolutePath);
        }

        [Fact]
        public async Task ScanAsync_WithRelativeRoot_ReturnsNormalizedLibraryPath()
        {
            var relativeRoot = $"./{_relativeRootName}";
            var nestedDirectory = Path.Combine(_relativeRootAbsolute, "nested");
            Directory.CreateDirectory(nestedDirectory);
            var filePath = Path.Combine(nestedDirectory, "movie.mp4");
            await File.WriteAllTextAsync(filePath, "content");

            var target = new TargetFolder(
                relativeRoot,
                new[] { "*.mp4" },
                Array.Empty<string>(),
                null);

            var snapshots = await _scanner.ScanAsync(new[] { target }, CancellationToken.None);

            var snapshot = Assert.Single(snapshots);
            var expectedLibraryPath = LibraryPathHelper.Combine(relativeRoot, Path.Combine("nested", "movie.mp4"));

            Assert.Equal(
                LibraryPathHelper.NormalizeLibraryPath(expectedLibraryPath),
                LibraryPathHelper.NormalizeLibraryPath(snapshot.LibraryPath));
            Assert.Equal(Path.GetFullPath(filePath), snapshot.AbsolutePath);
        }

        [Fact]
        public async Task ScanAsync_WhenTargetDirectoryMissing_ReturnsEmpty()
        {
            var missingPath = Path.Combine(_tempRoot, "missing");
            var target = new TargetFolder(
                missingPath,
                new[] { "*.mp4" },
                Array.Empty<string>(),
                null);

            var snapshots = await _scanner.ScanAsync(new[] { target }, CancellationToken.None);

            Assert.Empty(snapshots);
        }

        [Fact]
        public async Task ScanAsync_WhenCancelled_ThrowsOperationCanceledException()
        {
            var target = new TargetFolder(
                _tempRoot,
                new[] { "*" },
                Array.Empty<string>(),
                null);

            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => _scanner.ScanAsync(new[] { target }, cts.Token));
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }

            if (Directory.Exists(_relativeRootAbsolute))
            {
                Directory.Delete(_relativeRootAbsolute, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
