using System.IO;

namespace Airi.Services.VideoPreview;

internal interface IFfmpegPreviewBackend
{
    Task<VideoProbeResult> ProbeAsync(string sourcePath, CancellationToken cancellationToken);

    Task RunAsync(
        string sourcePath,
        VideoPreviewPlan plan,
        Func<Stream, CancellationToken, Task> readOutputAsync,
        CancellationToken cancellationToken);
}
