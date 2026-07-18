using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class VideoPreviewLogFormatterTests
{
    [Fact]
    public void Format_DoesNotExposePathFileNameMetadataOrRawStderr()
    {
        var formatter = new VideoPreviewLogFormatter(new byte[32]);
        var text = formatter.FormatFailure(new VideoPreviewFailure(
            "C:\\Users\\alice\\Private Movie.mkv",
            ".mkv",
            "matroska",
            "hevc",
            "Decode",
            "ProcessFailed",
            1,
            TimeSpan.FromSeconds(2),
            TimeSpan.Zero,
            "C:\\Users\\alice\\Private Movie.mkv: title=Secret"));

        Assert.DoesNotContain("alice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Private Movie", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Secret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderrSummary", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("extension=.mkv", text);
        Assert.Contains("container=matroska", text);
        Assert.Contains("codec=hevc", text);
    }

    [Fact]
    public void Format_UsesSessionSaltedOpaqueSourceIdentifier()
    {
        var failure = new VideoPreviewFailure(
            "C:\\media\\sample.mp4", ".mp4", "mp4", "h264", "Probe", "InvalidMedia", null,
            TimeSpan.Zero, TimeSpan.Zero, null);

        var first = new VideoPreviewLogFormatter(new byte[32]).FormatFailure(failure);
        var salt = new byte[32];
        salt[0] = 1;
        var second = new VideoPreviewLogFormatter(salt).FormatFailure(failure);

        Assert.NotEqual(first, second);
        Assert.Matches("sourceId=[0-9a-f]{24}", first);
    }
}
