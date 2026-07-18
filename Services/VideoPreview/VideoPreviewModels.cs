namespace Airi.Services.VideoPreview;

public sealed record VideoPreviewRequest(
    string SourcePath,
    int MaxPixelWidth,
    int MaxPixelHeight,
    int FrameRate,
    TimeSpan MaxDuration);

public sealed record VideoProbeResult(
    TimeSpan Duration,
    int PixelWidth,
    int PixelHeight,
    string Container,
    string Codec);

public sealed record VideoPreviewPlan(
    TimeSpan Start,
    TimeSpan PlaybackDuration,
    int PixelWidth,
    int PixelHeight,
    int FrameRate,
    int FrameByteCount);

public sealed record VideoPreviewFrame(byte[] BgraPixels, TimeSpan PresentationTimestamp);

public sealed class VideoPreviewUnavailableException : Exception
{
    public VideoPreviewUnavailableException(string message) : base(message) { }
    public VideoPreviewUnavailableException(string message, Exception innerException) : base(message, innerException) { }

    internal VideoPreviewUnavailableException(
        string message,
        string stage,
        string result,
        int? exitCode,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Stage = stage;
        Result = result;
        ExitCode = exitCode;
    }

    internal string Stage { get; } = "Prepare";
    internal string Result { get; } = "Unavailable";
    internal int? ExitCode { get; }
}
