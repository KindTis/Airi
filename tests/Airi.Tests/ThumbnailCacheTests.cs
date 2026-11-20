using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Airi.Infrastructure;
using Xunit;

namespace Airi.Tests
{
    public sealed class ThumbnailCacheTests : IAsyncLifetime
    {
        private readonly string _baseDirectoryName;
        private readonly string _baseDirectory;
        private readonly ThumbnailCache _cache;

        public ThumbnailCacheTests()
        {
            _baseDirectoryName = $"AiriThumbnailCacheTests_{Guid.NewGuid():N}";
            _baseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _baseDirectoryName);
            Directory.CreateDirectory(_baseDirectory);
            _cache = new ThumbnailCache(_baseDirectory);
        }

        [Fact]
        public async Task SaveAsync_EmptyBytes_ThrowsArgumentException()
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _cache.SaveAsync(Array.Empty<byte>(), ".jpg", "sample", CancellationToken.None));
        }

        [Fact]
        public async Task SaveAsync_PersistsFileWithSanitizedKey()
        {
            var relativePath = await _cache.SaveAsync(ImageBytes(), "png", "Sample Key/01", CancellationToken.None);

            var expectedPrefix = $"./{_baseDirectoryName}/cache/Sample_Key_01_";
            Assert.StartsWith(expectedPrefix, relativePath, StringComparison.Ordinal);
            Assert.EndsWith(".png", relativePath.ToLowerInvariant());

            var absolutePath = LibraryPathHelper.ResolveToAbsolute(relativePath);
            Assert.True(File.Exists(absolutePath));
        }

        [Fact]
        public async Task SaveAsync_EmptyExtension_UsesJpg()
        {
            var relativePath = await _cache.SaveAsync(ImageBytes(), string.Empty, "thumb", CancellationToken.None);

            Assert.EndsWith(".jpg", relativePath.ToLowerInvariant());
        }

        private static byte[] ImageBytes() => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"));

        [Fact]
        public async Task OwnedCandidates_AreUniqueAndOnlyTheirCreatorCanDiscardThem()
        {
            var first = await _cache.SaveAsync(ImageBytes(), ".jpg", "same-key");
            var second = await _cache.SaveAsync(ImageBytes(), ".jpg", "same-key");
            Assert.NotEqual(first, second);
            var other = new ThumbnailCache(_baseDirectory);
            other.DiscardOwned(first);
            Assert.True(File.Exists(LibraryPathHelper.ResolveToAbsolute(first)));
            _cache.DiscardOwned(first);
            Assert.False(File.Exists(LibraryPathHelper.ResolveToAbsolute(first)));
            _cache.AcceptOwned(second);
            _cache.DiscardOwned(second);
            Assert.True(File.Exists(LibraryPathHelper.ResolveToAbsolute(second)));
        }

        [Fact]
        public async Task InvalidCandidate_DoesNotCreateCacheFile()
        {
            await Assert.ThrowsAnyAsync<Exception>(() => _cache.SaveAsync(new byte[] { 1, 2, 3 }, ".png", "invalid"));
            Assert.Empty(Directory.GetFiles(Path.Combine(_baseDirectory, "cache")));
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            if (Directory.Exists(_baseDirectory))
            {
                Directory.Delete(_baseDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
