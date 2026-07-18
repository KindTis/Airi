using System.IO;
using System.Text;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class FfmpegVideoPreviewServiceTests
{
    private static readonly VideoPreviewRequest Request =
        new("C:\\secret\\one.mp4", 4, 4, 15, TimeSpan.FromSeconds(10));

    [Fact]
    public async Task PrepareAsync_SecondSessionWaitsUntilFirstPreviewIsDisposed()
    {
        var backend = new RecordingFfmpegBackend(BackendBehavior.SuccessHold, BackendBehavior.SuccessHold);
        var service = new FfmpegVideoPreviewService(backend);
        await using var first = await service.PrepareAsync(Request, CancellationToken.None);

        var secondTask = service.PrepareAsync(
            Request with { SourcePath = "C:\\secret\\two.mkv" },
            CancellationToken.None);
        Assert.False(secondTask.IsCompleted);

        await first.DisposeAsync();
        await using var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, backend.MaximumConcurrentRunCount);
    }

    [Theory]
    [InlineData(BackendBehavior.EndBeforeReady)]
    [InlineData(BackendBehavior.ReaderFault)]
    public async Task PrepareAsync_FailureBeforeReadyReleasesGateAndAllowsNextPrepare(BackendBehavior behavior)
    {
        var backend = new RecordingFfmpegBackend(behavior, BackendBehavior.SuccessHold);
        var service = new FfmpegVideoPreviewService(backend);

        await Assert.ThrowsAnyAsync<Exception>(() => service.PrepareAsync(Request, CancellationToken.None));
        await using var next = await service.PrepareAsync(Request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, backend.MaximumConcurrentRunCount);
        Assert.Equal(2, backend.RunCount);
    }

    [Fact]
    public async Task PrepareAsync_CancellationBeforeReadyReleasesGateAndAllowsNextPrepare()
    {
        var backend = new RecordingFfmpegBackend(BackendBehavior.BlockUntilCanceled, BackendBehavior.SuccessHold);
        var service = new FfmpegVideoPreviewService(backend);
        using var cancellation = new CancellationTokenSource();

        var prepare = service.PrepareAsync(Request, cancellation.Token);
        await backend.RunStarted.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await prepare);

        await using var next = await service.PrepareAsync(Request, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, backend.MaximumConcurrentRunCount);
    }

    [Fact]
    public async Task Backend_UsesFfmpegCoreArgumentsAndIndependentProbePathArgument()
    {
        var runner = new RecordingMediaProcessRunner();
        var backend = new FfmpegCorePreviewBackend("C:\\bundle", runner);
        const string source = "C:\\secret folder\\movie.mp4";

        var probe = await backend.ProbeAsync(source, CancellationToken.None);
        var plan = new VideoPreviewPlan(
            TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), 480, 270, 15, 480 * 270 * 4);
        await backend.RunAsync(source, plan, DrainAsync, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(100), probe.Duration);
        Assert.Equal("h264", probe.Codec);
        Assert.Equal(2, runner.Requests.Count);
        Assert.Contains("-show_format", runner.Requests[0].ArgumentList);
        Assert.Contains(source, runner.Requests[0].ArgumentList);
        var arguments = Assert.IsType<string>(runner.Requests[1].RawArguments);
        Assert.Contains("-ss 20", arguments);
        Assert.Contains("-t 10", arguments);
        Assert.Contains("-an -sn -dn", arguments);
        Assert.Contains("scale=480:270,fps=15", arguments);
        Assert.Contains("-pix_fmt bgra -f rawvideo", arguments);
        Assert.Contains("pipe:1", arguments);
    }

    private static Task DrainAsync(Stream stream, CancellationToken cancellationToken) =>
        stream.CopyToAsync(Stream.Null, cancellationToken);

    public enum BackendBehavior
    {
        SuccessHold,
        EndBeforeReady,
        ReaderFault,
        BlockUntilCanceled
    }

    private sealed class RecordingFfmpegBackend : IFfmpegPreviewBackend
    {
        private readonly Queue<BackendBehavior> _behaviors;
        private int _activeRuns;

        public RecordingFfmpegBackend(params BackendBehavior[] behaviors) =>
            _behaviors = new Queue<BackendBehavior>(behaviors);

        public TaskCompletionSource RunStartedSource { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task RunStarted => RunStartedSource.Task;
        public int RunCount { get; private set; }
        public int MaximumConcurrentRunCount { get; private set; }

        public Task<VideoProbeResult> ProbeAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromResult(new VideoProbeResult(TimeSpan.FromSeconds(10), 4, 4, "mp4", "h264"));

        public async Task RunAsync(
            string sourcePath,
            VideoPreviewPlan plan,
            Func<Stream, CancellationToken, Task> readOutputAsync,
            CancellationToken cancellationToken)
        {
            RunCount++;
            var active = Interlocked.Increment(ref _activeRuns);
            MaximumConcurrentRunCount = Math.Max(MaximumConcurrentRunCount, active);
            RunStartedSource.TrySetResult();
            var behavior = _behaviors.Dequeue();
            try
            {
                switch (behavior)
                {
                    case BackendBehavior.SuccessHold:
                        await using (var stream = new MemoryStream(new byte[plan.FrameByteCount * 3]))
                        {
                            await readOutputAsync(stream, cancellationToken);
                        }
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        break;
                    case BackendBehavior.EndBeforeReady:
                        await using (var stream = new MemoryStream(new byte[plan.FrameByteCount * 2]))
                        {
                            await readOutputAsync(stream, cancellationToken);
                        }
                        break;
                    case BackendBehavior.ReaderFault:
                        await using (var stream = new ThrowingReadStream())
                        {
                            await readOutputAsync(stream, cancellationToken);
                        }
                        break;
                    case BackendBehavior.BlockUntilCanceled:
                        await using (var stream = new BlockingReadStream())
                        {
                            await readOutputAsync(stream, cancellationToken);
                        }
                        break;
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeRuns);
            }
        }
    }

    private sealed class RecordingMediaProcessRunner : IMediaProcessRunner
    {
        public List<MediaProcessRequest> Requests { get; } = new();

        public async Task<MediaProcessResult> RunAsync(
            MediaProcessRequest request,
            Func<Stream, CancellationToken, Task> readOutputAsync,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var bytes = Requests.Count == 1
                ? Encoding.UTF8.GetBytes("{\"format\":{\"duration\":\"100\",\"format_name\":\"mov,mp4\"},\"streams\":[{\"codec_type\":\"video\",\"width\":1920,\"height\":1080,\"codec_name\":\"h264\"}]}")
                : Array.Empty<byte>();
            await using var stream = new MemoryStream(bytes);
            await readOutputAsync(stream, cancellationToken);
            return new MediaProcessResult(0, 0, false);
        }
    }

    private class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("reader failed");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("reader failed"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class BlockingReadStream : ThrowingReadStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
