using System.Globalization;
using System.IO;
using System.Runtime.ExceptionServices;
using Airi.Services.VideoPreview;

namespace Airi.Services;

internal sealed record VideoThumbnailCandidate(TimeSpan Timestamp, string FilePath);

internal sealed class VideoThumbnailExtractor
{
    private const int CandidateCount = 5;
    private const int MaximumConcurrentProcesses = 2;
    private readonly string _ffmpegPath;
    private readonly FfmpegCorePreviewBackend _previewBackend;
    private readonly IMediaProcessRunner _processRunner;

    public VideoThumbnailExtractor(string binaryFolder)
        : this(binaryFolder, new MediaProcessRunner())
    {
    }

    internal VideoThumbnailExtractor(string binaryFolder, IMediaProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryFolder);
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _ffmpegPath = Path.GetFullPath(Path.Combine(binaryFolder, "ffmpeg.exe"));
        _previewBackend = new FfmpegCorePreviewBackend(binaryFolder, processRunner);
    }

    internal static IReadOnlyList<TimeSpan> SelectTimestamps(TimeSpan duration, Random random)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        ArgumentNullException.ThrowIfNull(random);

        var usableStart = duration.TotalSeconds * 0.05;
        var segmentLength = duration.TotalSeconds * 0.90 / CandidateCount;
        return Enumerable.Range(0, CandidateCount)
            .Select(index => TimeSpan.FromSeconds(
                usableStart + (segmentLength * index) + (segmentLength * random.NextDouble())))
            .ToArray();
    }

    public async Task<IReadOnlyList<VideoThumbnailCandidate>> ExtractAsync(
        string sourcePath,
        string outputDirectory,
        Random random,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(random);
        cancellationToken.ThrowIfCancellationRequested();

        var probe = await _previewBackend.ProbeAsync(sourcePath, cancellationToken)
            .ConfigureAwait(false);
        var timestamps = SelectTimestamps(probe.Duration, random);
        Directory.CreateDirectory(outputDirectory);

        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processGate = new SemaphoreSlim(
            MaximumConcurrentProcesses,
            MaximumConcurrentProcesses);
        var firstFailure = new TaskCompletionSource<Exception>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = timestamps
            .Select((timestamp, index) => ExtractCandidateAsync(index, timestamp))
            .ToArray();

        try
        {
            var candidates = await Task.WhenAll(tasks).ConfigureAwait(false);
            return candidates.OrderBy(candidate => candidate.Timestamp).ToArray();
        }
        catch
        {
            if (firstFailure.Task.IsCompletedSuccessfully)
            {
                ExceptionDispatchInfo.Capture(firstFailure.Task.Result).Throw();
            }

            throw;
        }

        async Task<VideoThumbnailCandidate> ExtractCandidateAsync(int index, TimeSpan timestamp)
        {
            var enteredGate = false;
            try
            {
                await processGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
                enteredGate = true;
                linkedCancellation.Token.ThrowIfCancellationRequested();

                var outputPath = Path.Combine(outputDirectory, $"candidate-{index + 1:D2}.jpg");
                var seconds = timestamp.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
                var request = MediaProcessRequest.Create(
                    _ffmpegPath,
                    new[]
                    {
                        "-hide_banner", "-loglevel", "error",
                        "-ss", seconds,
                        "-i", sourcePath,
                        "-frames:v", "1",
                        "-an", "-sn", "-dn",
                        "-vf", "scale=w='min(1280,iw)':h=-2",
                        "-y", outputPath
                    });
                var result = await _processRunner.RunAsync(
                    request,
                    static (stream, token) => stream.CopyToAsync(Stream.Null, token),
                    linkedCancellation.Token).ConfigureAwait(false);

                if (result.ExitCode != 0)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg process failed with exit code {result.ExitCode}.");
                }
                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    throw new InvalidDataException("FFmpeg did not create a non-empty JPEG output.");
                }

                return new VideoThumbnailCandidate(timestamp, outputPath);
            }
            catch (OperationCanceledException)
                when (linkedCancellation.IsCancellationRequested &&
                      !cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                firstFailure.TrySetResult(ex);
                linkedCancellation.Cancel();
                throw;
            }
            finally
            {
                if (enteredGate)
                {
                    processGate.Release();
                }
            }
        }
    }
}
