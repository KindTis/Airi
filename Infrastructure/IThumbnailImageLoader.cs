using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Airi.Infrastructure;

public sealed record ThumbnailImageResult(ImageSource Source, bool IsFallback);

public interface IThumbnailImageLoader
{
    ImageSource FallbackSource { get; }

    Task<ThumbnailImageResult> LoadAsync(
        string thumbnailPath,
        int decodePixelWidth,
        CancellationToken cancellationToken);
}
