using System;
using System.IO;
using Airi;
using Airi.Infrastructure;
using Airi.ViewModels;

namespace Airi.Tests
{
    public class MetadataEditorViewModelTests
    {
        [Fact]
        public void ResetThumbnail_UsesFallbackImage()
        {
            var item = new VideoItem
            {
                Title = "Sample",
                ThumbnailPath = "custom/thumbnail.jpg",
                ThumbnailUri = "file:///custom/thumbnail.jpg",
                Description = "desc",
                Actors = Array.Empty<string>(),
                Tags = Array.Empty<string>()
            };

            var viewModel = new MetadataEditorViewModel(item);

            viewModel.ResetThumbnail();

            var expectedThumbnailPath = LibraryPathHelper.NormalizeLibraryPath(@".\resources\noimage.jpg");
            Assert.Equal(expectedThumbnailPath, viewModel.ThumbnailPath);

            var fallbackAbsolute = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources", "noimage.jpg");
            var expectedPreview = File.Exists(fallbackAbsolute) ? new Uri(fallbackAbsolute).AbsoluteUri : string.Empty;
            Assert.Equal(expectedPreview, viewModel.ThumbnailPreviewUri);

            Assert.Equal("기본 이미지 사용 중", viewModel.ThumbnailDisplayName);
        }

        [Fact]
        public async Task UpdateThumbnailFromFileAsync_CopiesIntoCacheAndUpdatesPreviewState()
        {
            var root = Path.Combine(Path.GetTempPath(), "Airi.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "selected-thumbnail.jpg");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"),
                sourcePath);
            var viewModel = new MetadataEditorViewModel(new VideoItem
            {
                Title = "Sample",
                Description = string.Empty,
                Actors = Array.Empty<string>(),
                Tags = Array.Empty<string>()
            });
            string? cachedPath = null;

            try
            {
                var updated = await viewModel.UpdateThumbnailFromFileAsync(sourcePath);
                cachedPath = LibraryPathHelper.ResolveToAbsolute(viewModel.ThumbnailPath);

                Assert.True(updated);
                Assert.True(File.Exists(cachedPath));
                Assert.Equal(LibraryPathHelper.NormalizeLibraryPath(viewModel.ThumbnailPath), viewModel.ThumbnailPath);
                Assert.Equal(new Uri(cachedPath).AbsoluteUri, viewModel.ThumbnailPreviewUri);
                Assert.Equal("selected-thumbnail.jpg", viewModel.ThumbnailDisplayName);
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(cachedPath) && File.Exists(cachedPath))
                {
                    File.Delete(cachedPath);
                }
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
