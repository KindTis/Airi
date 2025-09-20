using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Services;
using Xunit;

namespace Airi.Tests
{
    public sealed class LibraryStoreTests : IAsyncLifetime
    {
        private readonly string _tempDirectory;
        private readonly string _libraryPath;

        public LibraryStoreTests()
        {
            _tempDirectory = Path.Combine(Path.GetTempPath(), "AiriTests", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempDirectory);
            _libraryPath = Path.Combine(_tempDirectory, "videos.json");
        }

        [Fact]
        public async Task LoadAsync_CreatesDefaultWhenMissing()
        {
            var store = new LibraryStore(_libraryPath);

            var library = await store.LoadAsync();

            Assert.True(File.Exists(_libraryPath));
            Assert.NotNull(library);
            Assert.NotEmpty(library.Videos);
            Assert.Contains(library.Videos, v => v.Meta.Title == "Inception");
        }

        [Fact]
        public async Task SaveAsync_PersistsChanges()
        {
            var store = new LibraryStore(_libraryPath);
            var library = await store.LoadAsync();

            library.Videos.Add(new VideoEntry("./Videos/test.mp4",
                new VideoMeta("Test Title", DateOnly.FromDateTime(DateTime.Today), Array.Empty<string>(), "resources/noimage.jpg", Array.Empty<string>())));

            await store.SaveAsync(library);

            var reloaded = await store.LoadAsync();
            Assert.Contains(reloaded.Videos, v => v.Meta.Title == "Test Title");
        }

        public Task InitializeAsync() => Task.CompletedTask;

        public Task DisposeAsync()
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }

            return Task.CompletedTask;
        }
    }
}
