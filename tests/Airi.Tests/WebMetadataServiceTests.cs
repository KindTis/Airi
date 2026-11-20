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
                new VideoMeta("Original Title", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                123,
                DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample Query", CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Original Title", updated!.Meta.Title);
            Assert.NotEmpty(updated.Meta.Actors);

            var absolutePath = LibraryPathHelper.ResolveToAbsolute(updated.Meta.Thumbnail);
            Assert.True(File.Exists(absolutePath));
        }

        [Fact]
        public async Task EnrichAsync_PreservesExistingDescriptionWithoutRetranslation()
        {
            var cache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
            var translation = new StubTranslationService();
            var service = new WebMetadataService(new[] { new StubSourceWithDescription() }, cache, translation, "EN");

            var original = new VideoEntry(
                "./Videos/sample.mp4",
                new VideoMeta(
                    "Original Title",
                    null,
                    Array.Empty<string>(),
                    string.Empty,
                    Array.Empty<string>(),
                    "원본 설명"),
                123,
                DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample Query", CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("원본 설명", updated!.Meta.Description);
            Assert.False(translation.Invoked);
        }

        [Fact]
        public async Task TranslateResultDescriptionAsync_TranslatesDescriptionAndPreservesThumbnail_WhenTranslatorEnabled()
        {
            var cache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
            var translation = new StubTranslationService();
            var service = new WebMetadataService(Array.Empty<IWebVideoMetaSource>(), cache, translation, "EN");
            var thumbnailBytes = new byte[] { 1, 2, 3 };
            var result = new WebVideoMetaResult(
                new VideoMeta(
                    "Title",
                    new DateOnly(2024, 1, 2),
                    new[] { "Actor" },
                    string.Empty,
                    new[] { "Tag" },
                    "원본 설명"),
                thumbnailBytes,
                ".png");

            var translated = await service.TranslateResultDescriptionAsync(result, CancellationToken.None);

            Assert.Equal("Translated Description", translated.Meta.Description);
            Assert.Equal("Title", translated.Meta.Title);
            Assert.Equal(new DateOnly(2024, 1, 2), translated.Meta.Date);
            Assert.Equal(new[] { "Actor" }, translated.Meta.Actors);
            Assert.Equal(new[] { "Tag" }, translated.Meta.Tags);
            Assert.Same(thumbnailBytes, translated.ThumbnailBytes);
            Assert.Equal(".png", translated.ThumbnailExtension);
            Assert.True(translation.Invoked);
        }

        [Fact]
        public async Task TranslateResultDescriptionAsync_WhenTranslatorDisabled_ReturnsOriginalDescription()
        {
            var cache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
            var service = new WebMetadataService(Array.Empty<IWebVideoMetaSource>(), cache);
            var result = new WebVideoMetaResult(
                new VideoMeta(
                    "Title",
                    null,
                    Array.Empty<string>(),
                    string.Empty,
                    Array.Empty<string>(),
                    "원본 설명"),
                null,
                null);

            var translated = await service.TranslateResultDescriptionAsync(result, CancellationToken.None);

            Assert.Equal("원본 설명", translated.Meta.Description);
        }

        [Fact]
        public async Task EnrichAsync_NoProviders_ReturnsNull()
        {
            var service = new WebMetadataService(Array.Empty<IWebVideoMetaSource>(), new ThumbnailCache());
            var original = new VideoEntry("./Videos/sample.mp4", new VideoMeta("Title", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty), 0, DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample", CancellationToken.None);

            Assert.Null(updated);
        }

        [Fact]
        public async Task EnrichAsync_FirstSourceReturnsPartialResult_DoesNotCallFallback()
        {
            var cache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
            var first = new CountingSource(new WebVideoMetaResult(
                new VideoMeta(string.Empty, new DateOnly(2024, 1, 2), Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                null,
                null));
            var fallback = new CountingSource(new WebVideoMetaResult(
                new VideoMeta("Fallback", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                null,
                null));
            var service = new WebMetadataService(new[] { first, fallback }, cache);
            var original = new VideoEntry(
                "./Videos/sample.mp4",
                new VideoMeta("Original Title", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                123,
                DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample Query", CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal(new DateOnly(2024, 1, 2), updated!.Meta.Date);
            Assert.Equal(1, first.CallCount);
            Assert.Equal(0, fallback.CallCount);
        }

        [Fact]
        public async Task EnrichAsync_FirstSourceReturnsNull_CallsNextSource()
        {
            var cache = new ThumbnailCache(AppDomain.CurrentDomain.BaseDirectory);
            var first = new CountingSource(null);
            var fallback = new CountingSource(new WebVideoMetaResult(
                new VideoMeta("Fallback", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                null,
                null));
            var service = new WebMetadataService(new[] { first, fallback }, cache);
            var original = new VideoEntry(
                "./Videos/sample.mp4",
                new VideoMeta("Original Title", null, Array.Empty<string>(), string.Empty, Array.Empty<string>(), string.Empty),
                123,
                DateTime.UtcNow);

            var updated = await service.EnrichAsync(original, "Sample Query", CancellationToken.None);

            Assert.NotNull(updated);
            Assert.Equal("Original Title", updated!.Meta.Title);
            Assert.Equal(1, first.CallCount);
            Assert.Equal(1, fallback.CallCount);
        }

        [Fact]
        public void MergeMeta_ManualEmptyAndExistingValuesRemainProtected()
        {
            var original = new VideoMeta("", null, Array.Empty<string>(), "old-cover.jpg", Array.Empty<string>(), "existing")
            {
                ManualFieldLocks = new[] { MetadataField.Title, MetadataField.Date, MetadataField.Tags },
                Studio = null
            };
            var incoming = new VideoMeta("new", new DateOnly(2024, 1, 2), new[] { "Actor" }, "new-cover.jpg", new[] { "Tag" }, "new")
            { Studio = "Confirmed studio", Genres = new[] { "Confirmed genre" } };
            var merged = WebMetadataService.MergeMeta(original, incoming);
            Assert.Equal("", merged.Title);
            Assert.Null(merged.Date);
            Assert.Empty(merged.Tags);
            Assert.Equal("old-cover.jpg", merged.Thumbnail);
            Assert.Equal("existing", merged.Description);
            Assert.Equal(incoming.Actors, merged.Actors);
            Assert.Equal("Confirmed studio", merged.Studio);
            Assert.Equal(incoming.Genres, merged.Genres);
            Assert.Equal(original.ManualFieldLocks, merged.ManualFieldLocks);
        }

        [Fact]
        public void MergeMeta_BlankRawDateCanBeFilledWithoutLosingTheParsedCandidate()
        {
            var original = new VideoMeta("title", null, [], "", [], "") { RawDate = "" };
            var incoming = original with { Date = new DateOnly(2024, 2, 3), RawDate = null };
            var result = WebMetadataService.MergeMeta(original, incoming);
            Assert.Equal(incoming.Date, result.Date); Assert.Null(result.RawDate);
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
                    Array.Empty<string>(),
                    string.Empty);
                var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=");
                return Task.FromResult<WebVideoMetaResult?>(new WebVideoMetaResult(meta, bytes, ".jpg"));
            }
        }

        private sealed class StubSourceWithDescription : IWebVideoMetaSource
        {
            public string Name => "StubWithDescription";

            public bool CanHandle(string query) => true;

            public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken)
            {
                var meta = new VideoMeta(
                    "Stub Title",
                    null,
                    new[] { "Actor One", "Actor Two" },
                    string.Empty,
                    Array.Empty<string>(),
                    "원본 설명");
                return Task.FromResult<WebVideoMetaResult?>(new WebVideoMetaResult(meta, Array.Empty<byte>(), ".jpg"));
            }
        }

        private sealed class CountingSource : IWebVideoMetaSource
        {
            private readonly WebVideoMetaResult? _result;

            public CountingSource(WebVideoMetaResult? result)
            {
                _result = result;
            }

            public int CallCount { get; private set; }

            public string Name => "Counting";

            public bool CanHandle(string query) => true;

            public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken)
            {
                CallCount++;
                return Task.FromResult(_result);
            }
        }

        private sealed class StubTranslationService : ITextTranslationService
        {
            public bool Invoked { get; private set; }

            public bool IsEnabled => true;

            public Task<string?> TranslateAsync(string text, string? sourceLanguageCode, string targetLanguageCode, CancellationToken cancellationToken)
            {
                Invoked = true;
                return Task.FromResult<string?>("Translated Description");
            }
        }
    }
}
