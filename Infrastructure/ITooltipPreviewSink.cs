using Airi.Services.VideoPreview;

namespace Airi.Infrastructure;

public interface ITooltipPreviewSink
{
    bool IsCurrent(Guid sessionId);

    ValueTask<DispatcherCallbackResult> SetPhaseAsync(
        Guid sessionId,
        TooltipPreviewPhase phase,
        CancellationToken cancellationToken);

    ValueTask<FramePresentationResult> ShowFrameAsync(
        Guid sessionId,
        int pixelWidth,
        int pixelHeight,
        VideoPreviewFrame frame,
        CancellationToken cancellationToken);
}

public readonly record struct DispatcherCallbackResult(
    bool Applied,
    long CompletedTimestamp,
    TimeSpan CallbackDuration);

public readonly record struct FramePresentationResult(
    bool Displayed,
    long DisplayedTimestamp,
    TimeSpan CallbackDuration);

public sealed record TooltipPreviewSession(
    Guid Id,
    VideoPreviewRequest Request,
    bool AnimationsEnabled,
    bool PreviewEligible,
    ITooltipPreviewSink Sink);
