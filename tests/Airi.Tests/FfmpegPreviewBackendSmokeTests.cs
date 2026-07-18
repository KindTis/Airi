using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class FfmpegPreviewBackendSmokeTests
{
    [Fact]
    public async Task ProbeOutputOverflow_ReturnsSafeFailureAndReleasesServiceGate()
    {
        var runner = new OverflowThenSuccessRunner();
        var service = new FfmpegVideoPreviewService(
            new FfmpegCorePreviewBackend(AppContext.BaseDirectory, runner));
        var request = new VideoPreviewRequest(
            "C:\\private\\video.mp4",
            4,
            4,
            15,
            TimeSpan.FromSeconds(10));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var exception = await Assert.ThrowsAsync<VideoPreviewUnavailableException>(
            () => service.PrepareAsync(request, timeout.Token));

        Assert.Equal("ProbeOutputTooLarge", exception.Result);
        Assert.False(timeout.IsCancellationRequested);
        await using var next = await service.PrepareAsync(request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(4, next.PixelWidth);
        Assert.Equal(3, runner.CallCount);
    }

    [Fact]
    public async Task H264Fixture_PreparesThreeBgraFramesWithinFiveSeconds()
    {
        var service = CreateService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var preview = await service.PrepareAsync(CreateRequest(), timeout.Token);
        var frames = new List<VideoPreviewFrame>();
        await foreach (var frame in preview.ReadFramesAsync(timeout.Token))
        {
            frames.Add(frame);
            if (frames.Count == 3) break;
        }

        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame =>
            Assert.Equal(preview.PixelWidth * preview.PixelHeight * 4, frame.BgraPixels.Length));
    }

    [Fact]
    public async Task Cancellation_ObservedProbePidExitsBeforePreparationCompletes()
    {
        var observer = new RecordingProcessObserver();
        var service = CreateService(observer);
        using var cancellation = new CancellationTokenSource();

        var prepare = service.PrepareAsync(CreateRequest(), cancellation.Token);
        var identity = await observer.ProbeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await observer.WaitForExitAsync(identity).WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await prepare);
        Assert.False(IsRunning(identity));
    }

    [Fact]
    public async Task Cancellation_BackpressuredFfmpegPidAndReaderExitBeforeDisposeCompletes()
    {
        var observer = new RecordingProcessObserver();
        var service = CreateService(observer);
        using var cancellation = new CancellationTokenSource();
        var prepare = service.PrepareAsync(CreateRequest(), cancellation.Token);
        var identity = await observer.FfmpegStarted.WaitAsync(TimeSpan.FromSeconds(5));
        var preview = await prepare.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        await preview.DisposeAsync();
        await observer.WaitForExitAsync(identity).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(IsRunning(identity));
        await using var next = await CreateService().PrepareAsync(CreateRequest(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static FfmpegVideoPreviewService CreateService(IMediaProcessObserver? observer = null)
    {
        var runner = new MediaProcessRunner(observer);
        return new FfmpegVideoPreviewService(new FfmpegCorePreviewBackend(BinaryFolder, runner));
    }

    private static VideoPreviewRequest CreateRequest() =>
        new(FixturePath, 480, 350, 15, TimeSpan.FromSeconds(10));

    private static string BinaryFolder => Path.Combine(
        AppContext.BaseDirectory,
        "resources",
        "ffmpeg",
        "win-x64");

    private static string FixturePath => Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "VideoPreview",
        "h264.mp4");

    private static bool IsRunning(MediaProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return process.StartTime.ToUniversalTime() == identity.StartTimeUtc && !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class RecordingProcessObserver : IMediaProcessObserver
    {
        private readonly TaskCompletionSource<MediaProcessIdentity> _probeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<MediaProcessIdentity> _ffmpegStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<(int Id, DateTime Start), TaskCompletionSource> _exits = new();

        public Task<MediaProcessIdentity> ProbeStarted => _probeStarted.Task;
        public Task<MediaProcessIdentity> FfmpegStarted => _ffmpegStarted.Task;

        public void Started(MediaProcessIdentity identity)
        {
            _exits.TryAdd(
                (identity.ProcessId, identity.StartTimeUtc),
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Path.GetFileName(identity.ExecutablePath).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
            {
                _probeStarted.TrySetResult(identity);
            }
            else if (Path.GetFileName(identity.ExecutablePath).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
            {
                _ffmpegStarted.TrySetResult(identity);
            }
        }

        public void Exited(MediaProcessIdentity identity, int? exitCode)
        {
            if (_exits.TryGetValue((identity.ProcessId, identity.StartTimeUtc), out var completion))
            {
                completion.TrySetResult();
            }
        }

        public Task WaitForExitAsync(MediaProcessIdentity identity) =>
            _exits[(identity.ProcessId, identity.StartTimeUtc)].Task;
    }

    private sealed class OverflowThenSuccessRunner : IMediaProcessRunner
    {
        private const string ProbeJson =
            "{\"format\":{\"duration\":\"10\",\"format_name\":\"matroska\"}," +
            "\"streams\":[{\"codec_type\":\"video\",\"width\":4,\"height\":4,\"codec_name\":\"h264\"}]}";

        public int CallCount { get; private set; }

        public async Task<MediaProcessResult> RunAsync(
            MediaProcessRequest request,
            Func<Stream, CancellationToken, Task> readOutputAsync,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (CallCount == 1)
            {
                await using var overflow = new BlockingOverflowStream((1024 * 1024) + 8192);
                await readOutputAsync(overflow, cancellationToken);
                return new MediaProcessResult(0, 0, false);
            }

            var bytes = CallCount == 2
                ? Encoding.UTF8.GetBytes(ProbeJson)
                : new byte[4 * 4 * 4 * 3];
            await using var output = new MemoryStream(bytes, writable: false);
            await readOutputAsync(output, cancellationToken);
            return new MediaProcessResult(0, 0, false);
        }
    }

    private sealed class BlockingOverflowStream : MemoryStream
    {
        public BlockingOverflowStream(int availableByteCount)
            : base(new byte[availableByteCount], writable: false) { }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (Position == Length)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
