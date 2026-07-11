using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Infrastructure;

namespace Airi.Tests;

internal sealed class TestThumbnailImageLoader : IThumbnailImageLoader
{
    public TestThumbnailImageLoader()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[4],
            4);
        bitmap.Freeze();
        FallbackSource = bitmap;
    }

    public ImageSource FallbackSource { get; }

    public Task<ThumbnailImageResult> LoadAsync(
        string thumbnailPath,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ThumbnailImageResult(FallbackSource, true));
    }
}
