using System.Globalization;
using System.IO;
using System.Text.Json;
using FFMpegCore;

namespace Airi.Services.VideoPreview;

internal sealed class FfmpegCorePreviewBackend : IFfmpegPreviewBackend
{
    private const int ProbeOutputLimit = 1024 * 1024;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly IMediaProcessRunner _processRunner;

    public FfmpegCorePreviewBackend(string binaryFolder)
        : this(binaryFolder, new MediaProcessRunner())
    {
    }

    internal FfmpegCorePreviewBackend(string binaryFolder, IMediaProcessRunner processRunner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(binaryFolder);
        _ffmpegPath = Path.GetFullPath(Path.Combine(binaryFolder, "ffmpeg.exe"));
        _ffprobePath = Path.GetFullPath(Path.Combine(binaryFolder, "ffprobe.exe"));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public async Task<VideoProbeResult> ProbeAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var output = new CappedOutputCollector(ProbeOutputLimit);
        var request = MediaProcessRequest.Create(
            _ffprobePath,
            new[]
            {
                "-v", "error",
                "-print_format", "json",
                "-show_format",
                "-show_streams",
                sourcePath
            });

        MediaProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(request, output.ReadAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ProbeOutputTooLargeException ex)
        {
            throw Unavailable(
                "FFprobe output exceeded the allowed size.",
                "Probe",
                "ProbeOutputTooLarge",
                null,
                ex);
        }
        catch (Exception ex)
        {
            throw Unavailable("FFprobe execution failed.", "Probe", "RunnerFailed", null, ex);
        }

        if (result.ExitCode != 0)
        {
            throw Unavailable("FFprobe process failed.", "Probe", "ProcessFailed", result.ExitCode);
        }
        if (output.Overflowed)
        {
            throw Unavailable("FFprobe output exceeded the allowed size.", "Probe", "ProbeOutputTooLarge", result.ExitCode);
        }

        try
        {
            using var document = JsonDocument.Parse(output.Bytes);
            var root = document.RootElement;
            var format = root.GetProperty("format");
            var durationText = format.GetProperty("duration").GetString();
            if (!double.TryParse(durationText, NumberStyles.Float, CultureInfo.InvariantCulture, out var durationSeconds) ||
                durationSeconds <= 0)
            {
                throw new FormatException("Invalid duration.");
            }

            JsonElement videoStream = default;
            var foundVideo = false;
            foreach (var stream in root.GetProperty("streams").EnumerateArray())
            {
                if (stream.TryGetProperty("codec_type", out var type) && type.GetString() == "video")
                {
                    videoStream = stream;
                    foundVideo = true;
                    break;
                }
            }
            if (!foundVideo) throw new FormatException("No video stream.");

            return new VideoProbeResult(
                TimeSpan.FromSeconds(durationSeconds),
                videoStream.GetProperty("width").GetInt32(),
                videoStream.GetProperty("height").GetInt32(),
                format.GetProperty("format_name").GetString() ?? "unknown",
                videoStream.GetProperty("codec_name").GetString() ?? "unknown");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or FormatException or InvalidOperationException)
        {
            throw Unavailable("FFprobe returned invalid media information.", "Probe", "InvalidProbeOutput", result.ExitCode, ex);
        }
    }

    public async Task RunAsync(
        string sourcePath,
        VideoPreviewPlan plan,
        Func<Stream, CancellationToken, Task> readOutputAsync,
        CancellationToken cancellationToken)
    {
        var seconds = static (TimeSpan value) =>
            value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        var processor = FFMpegArguments
            .FromFileInput(sourcePath, true, options =>
                options.WithCustomArgument($"-ss {seconds(plan.Start)}"))
            .OutputToUrl("pipe:1", options => options
                .WithCustomArgument($"-t {seconds(plan.PlaybackDuration)}")
                .WithCustomArgument("-an -sn -dn")
                .WithCustomArgument($"-vf scale={plan.PixelWidth}:{plan.PixelHeight},fps={plan.FrameRate}")
                .WithCustomArgument("-pix_fmt bgra -f rawvideo"));

        MediaProcessResult result;
        try
        {
            result = await _processRunner.RunAsync(
                MediaProcessRequest.Ffmpeg(_ffmpegPath, processor.Arguments),
                readOutputAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw Unavailable("FFmpeg execution failed.", "Decode", "RunnerFailed", null, ex);
        }

        if (result.ExitCode != 0)
        {
            throw Unavailable("FFmpeg process failed.", "Decode", "ProcessFailed", result.ExitCode);
        }
    }

    private static VideoPreviewUnavailableException Unavailable(
        string message,
        string stage,
        string result,
        int? exitCode,
        Exception? innerException = null) =>
        new(message, stage, result, exitCode, innerException);

    private sealed class CappedOutputCollector
    {
        private readonly int _limit;
        private readonly MemoryStream _captured = new();

        public CappedOutputCollector(int limit) => _limit = limit;

        public bool Overflowed { get; private set; }
        public byte[] Bytes => _captured.ToArray();

        public async Task ReadAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) return;
                var remaining = _limit - (int)_captured.Length;
                if (remaining > 0)
                {
                    await _captured.WriteAsync(buffer.AsMemory(0, Math.Min(remaining, read)), cancellationToken)
                        .ConfigureAwait(false);
                }
                if (read > remaining)
                {
                    Overflowed = true;
                    throw new ProbeOutputTooLargeException();
                }
            }
        }
    }

    private sealed class ProbeOutputTooLargeException : Exception { }
}
