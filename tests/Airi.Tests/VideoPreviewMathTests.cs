using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class VideoPreviewMathTests
{
    private static readonly VideoPreviewRequest Request = new("C:\\media\\sample.mp4", 480, 350, 15, TimeSpan.FromSeconds(10));

    [Theory]
    [InlineData(100, 20, 10)]
    [InlineData(8, 1.6, 6.4)]
    public void TryCreatePlan_UsesTwentyPercentAndRemainingDuration(double seconds, double start, double playback)
    {
        var ok = VideoPreviewMath.TryCreatePlan(
            Request,
            new VideoProbeResult(TimeSpan.FromSeconds(seconds), 1920, 1080, "mp4", "h264"),
            out var plan);

        Assert.True(ok);
        Assert.Equal(TimeSpan.FromSeconds(start), plan.Start);
        Assert.Equal(TimeSpan.FromSeconds(playback), plan.PlaybackDuration);
        Assert.Equal(480, plan.PixelWidth);
        Assert.Equal(270, plan.PixelHeight);
        Assert.Equal(480 * 270 * 4, plan.FrameByteCount);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void TryCreatePlan_RejectsNonPositiveDuration(double seconds)
    {
        Assert.False(VideoPreviewMath.TryCreatePlan(
            Request,
            new VideoProbeResult(TimeSpan.FromSeconds(seconds), 1920, 1080, "mp4", "h264"),
            out _));
    }

    [Fact]
    public void TryCreatePlan_PreservesPortraitAspectInsideBounds()
    {
        Assert.True(VideoPreviewMath.TryCreatePlan(
            Request,
            new VideoProbeResult(TimeSpan.FromSeconds(20), 1080, 1920, "matroska", "hevc"),
            out var plan));
        Assert.Equal(197, plan.PixelWidth);
        Assert.Equal(350, plan.PixelHeight);
        Assert.True(plan.PixelWidth <= 480 && plan.PixelHeight <= 350);
    }
}
