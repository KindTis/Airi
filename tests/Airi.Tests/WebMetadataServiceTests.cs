using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Web;
using Xunit;

namespace Airi.Tests
{
    public sealed class WebMetadataServiceTests
    {
        [Fact]
        public async Task EnrichAsync_ReturnsUpdatedEntryAndSavesThumbnail()
        {
            var cache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
            var service = new WebMetadataService(new[] { new StubSource() }, cache);

            var original = new VideoEntry(
                "./Videos/sample.mp4",
                new VideoMeta("Original Title", null, Array.Empty<string>(), string.Empty, Array.Empty<string>()),
                123,
                DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample Query", CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Stub Title", updated!.Meta.Title);
            Assert.NotEqual(original.Meta.Title, updated.Meta.Title);
            Assert.NotEmpty(updated.Meta.Actors);

            var absolutePath = LibraryPathHelper.ResolveToAbsolute(updated.Meta.Thumbnail);
            Assert.True(File.Exists(absolutePath));
        }

        [Fact]
        public async Task EnrichAsync_NoProviders_ReturnsNull()
        {
            var service = new WebMetadataService(Array.Empty<IWebVideoMetaSource>(), new ThumbnailCache());
            var original = new VideoEntry("./Videos/sample.mp4", new VideoMeta("Title", null, Array.Empty<string>(), string.Empty, Array.Empty<string>()), 0, DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample", CancellationToken.None);

            Assert.Null(updated);
        }

        private sealed class StubSource : IWebVideoMetaSource
        {
            public string Name => "Stub";

            public bool CanHandle(string query) => true;

            public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken)
            {
                var meta = new VideoMeta(
                    "Stub Title",
                    null,
                    new[] { "Actor One", "Actor Two" },
                    string.Empty,
                    Array.Empty<string>());
                var bytes = new byte[] { 1, 2, 3 };
                return Task.FromResult<WebVideoMetaResult?>(new WebVideoMetaResult(meta, bytes, ".jpg"));
            }
        }
    }
}
