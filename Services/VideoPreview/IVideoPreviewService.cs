namespace Airi.Services.VideoPreview;

public interface IVideoPreviewService
{
    Task<IPreparedVideoPreview> PrepareAsync(VideoPreviewRequest request, CancellationToken cancellationToken);
}

public interface IPreparedVideoPreview : IAsyncDisposable
{
    int PixelWidth { get; }
    int PixelHeight { get; }
    int FrameRate { get; }
    TimeSpan PlaybackDuration { get; }
    IAsyncEnumerable<VideoPreviewFrame> ReadFramesAsync(CancellationToken cancellationToken);
}
