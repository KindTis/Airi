using System.IO;

namespace Airi.Services.VideoPreview;

internal interface IMediaProcessRunner
{
    Task<MediaProcessResult> RunAsync(
        MediaProcessRequest request,
        Func<Stream, CancellationToken, Task> readOutputAsync,
        CancellationToken cancellationToken);
}

internal interface IMediaProcessObserver
{
    void Started(MediaProcessIdentity identity);
    void Exited(MediaProcessIdentity identity, int? exitCode);
}

internal sealed record MediaProcessIdentity(
    string ExecutablePath,
    int ProcessId,
    DateTime StartTimeUtc);

internal sealed record MediaProcessResult(
    int ExitCode,
    int CapturedStderrByteCount,
    bool StderrOverflowed);

internal sealed record MediaProcessRequest(
    string FileName,
    string? RawArguments,
    IReadOnlyList<string> ArgumentList)
{
    public static MediaProcessRequest Create(string fileName, IEnumerable<string> arguments) =>
        new(fileName, null, arguments.ToArray());

    public static MediaProcessRequest Ffmpeg(string fileName, string arguments) =>
        new(fileName, arguments, Array.Empty<string>());
}

internal sealed class NullMediaProcessObserver : IMediaProcessObserver
{
    public static readonly NullMediaProcessObserver Instance = new();

    private NullMediaProcessObserver() { }

    public void Started(MediaProcessIdentity identity) { }
    public void Exited(MediaProcessIdentity identity, int? exitCode) { }
}
