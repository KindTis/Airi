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
    }
}
