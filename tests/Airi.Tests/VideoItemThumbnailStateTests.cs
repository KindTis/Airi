using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VideoItemThumbnailStateTests
{
    [Fact]
    public void ThumbnailHelpers_ApplyOnlySupportedStateCombinations()
    {
        WpfTestHost.Run(() =>
        {
            var fallback = CreateFrozenBitmap(1, 1);
            var decoded = CreateFrozenBitmap(32, 20);
            var item = new VideoItem();

            item.ReleaseThumbnail(fallback);
            Assert.Equal(ThumbnailLoadState.NotRequested, item.ThumbnailLoadState);
            Assert.Same(fallback, item.ThumbnailSource);

            item.BeginThumbnailLoad(fallback);
            Assert.Equal(ThumbnailLoadState.Loading, item.ThumbnailLoadState);
            Assert.Same(fallback, item.ThumbnailSource);

            item.CompleteThumbnailLoad(decoded);
            Assert.Equal(ThumbnailLoadState.Loaded, item.ThumbnailLoadState);
            Assert.Same(decoded, item.ThumbnailSource);

            item.FailThumbnailLoad(fallback);
            Assert.Equal(ThumbnailLoadState.Failed, item.ThumbnailLoadState);
            Assert.Same(fallback, item.ThumbnailSource);
        });
    }

    [Fact]
    public void ThumbnailRuntimeState_IsNotPartOfPersistenceModel()
    {
        Assert.Null(typeof(Airi.Domain.VideoEntry).GetProperty(nameof(VideoItem.ThumbnailSource)));
        Assert.Null(typeof(Airi.Domain.VideoEntry).GetProperty(nameof(VideoItem.ThumbnailLoadState)));
    }

    private static ImageSource CreateFrozenBitmap(int width, int height)
    {
        var pixels = Enumerable.Repeat((byte)0xff, width * height * 4).ToArray();
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
