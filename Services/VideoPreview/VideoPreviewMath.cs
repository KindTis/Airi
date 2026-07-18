namespace Airi.Services.VideoPreview;

public static class VideoPreviewMath
{
    public static bool TryCreatePlan(
        VideoPreviewRequest request,
        VideoProbeResult probe,
        out VideoPreviewPlan plan)
    {
        plan = default!;
        if (probe.Duration <= TimeSpan.Zero ||
            probe.PixelWidth <= 0 ||
            probe.PixelHeight <= 0 ||
            request.MaxPixelWidth <= 0 ||
            request.MaxPixelHeight <= 0 ||
            request.FrameRate <= 0 ||
            request.MaxDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var start = TimeSpan.FromTicks((long)(probe.Duration.Ticks * 0.20d));
        var playback = probe.Duration - start;
        if (playback <= TimeSpan.Zero)
        {
            return false;
        }

        if (playback > request.MaxDuration)
        {
            playback = request.MaxDuration;
        }

        var scale = Math.Min(
            1d,
            Math.Min(
                request.MaxPixelWidth / (double)probe.PixelWidth,
                request.MaxPixelHeight / (double)probe.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(probe.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(probe.PixelHeight * scale));
        var frameByteCount = checked(width * height * 4);
        plan = new VideoPreviewPlan(start, playback, width, height, request.FrameRate, frameByteCount);
        return true;
    }
}
