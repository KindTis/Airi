using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using Airi.Infrastructure;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class FfmpegVideoPreviewIntegrationTests
{
    [Fact]
    public async Task ProbeAndRequest_UseDurationAndTwentyPercentStart()
    {
        var runner = new RecordingMediaProcessRunner(new MediaProcessRunner());
        var backend = new FfmpegCorePreviewBackend(BinaryFolder, runner);
        var probe = await backend.ProbeAsync(FixturePath("h264.mp4"), CancellationToken.None);
        var service = new FfmpegVideoPreviewService(backend);

        await using var preview = await service.PrepareAsync(
            CreateRequest("h264.mp4"),
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.InRange(probe.Duration, TimeSpan.FromSeconds(11.99), TimeSpan.FromSeconds(12.01));
        var request = Assert.Single(runner.Requests, IsFfmpeg);
        var arguments = Assert.IsType<string>(request.RawArguments);
        Assert.Contains("-ss 2.4", arguments, StringComparison.Ordinal);
        Assert.Contains("-t 9.6", arguments, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("h264.mp4", "h264")]
    [InlineData("hevc.mkv", "hevc")]
    public async Task GuaranteedFixture_PreparesThreeBgraFramesWithinFiveSeconds(
        string fileName,
        string codec)
    {
        var service = CreateService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var preview = await service.PrepareAsync(CreateRequest(fileName), timeout.Token);
        var frames = await ReadFramesAsync(preview, 3, timeout.Token);

        Assert.Equal(3, frames.Count);
        Assert.All(frames, frame =>
            Assert.Equal(preview.PixelWidth * preview.PixelHeight * 4, frame.BgraPixels.Length));
        var probe = await new FfmpegCorePreviewBackend(BinaryFolder)
            .ProbeAsync(FixturePath(fileName), timeout.Token);
        Assert.Contains(codec, probe.Codec, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FfmpegRequest_ProducesRawVideoWithoutAudio()
    {
        var runner = new RecordingMediaProcessRunner(new MediaProcessRunner());
        var service = new FfmpegVideoPreviewService(
            new FfmpegCorePreviewBackend(BinaryFolder, runner));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await using var preview = await service.PrepareAsync(CreateRequest("h264.mp4"), timeout.Token);
        var frames = await ReadFramesAsync(preview, 3, timeout.Token);
        var request = Assert.Single(runner.Requests, IsFfmpeg);
        var arguments = Assert.IsType<string>(request.RawArguments);

        Assert.Contains("-an -sn -dn", arguments, StringComparison.Ordinal);
        Assert.Contains("-pix_fmt bgra", arguments, StringComparison.Ordinal);
        Assert.Contains("-f rawvideo", arguments, StringComparison.Ordinal);
        Assert.Contains("pipe:1", arguments, StringComparison.Ordinal);
        Assert.All(frames, frame =>
            Assert.Equal(preview.PixelWidth * preview.PixelHeight * 4, frame.BgraPixels.Length));
    }

    [Fact]
    public async Task Cancellation_ExitsObservedFfmpegPidBeforeCompletion()
    {
        var observer = new RecordingProcessObserver();
        var service = CreateService(observer);
        using var cancellation = new CancellationTokenSource();
        var prepare = service.PrepareAsync(CreateRequest("h264.mp4"), cancellation.Token);
        var identity = await observer.WaitForStartedAsync("ffmpeg.exe", 1, TimeSpan.FromSeconds(5));
        var preview = await prepare.WaitAsync(TimeSpan.FromSeconds(5));

        cancellation.Cancel();
        var dispose = preview.DisposeAsync().AsTask();
        await observer.WaitForExitAsync(identity, TimeSpan.FromSeconds(5));
        Assert.False(dispose.IsFaulted);
        await dispose;

        Assert.False(IsRunning(identity));
        Assert.True(observer.StartIndex(identity) < observer.ExitIndex(identity));
        Assert.Equal(0, observer.LiveProcessCount);
    }

    [Fact]
    public async Task CorruptFixture_ControllerFallsBackWithoutUiException()
    {
        var service = CreateService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<VideoPreviewUnavailableException>(() =>
            service.PrepareAsync(CreateRequest("corrupt.mp4"), timeout.Token));
        var sink = new RecordingPreviewSink(displayFrames: false);
        await using var controller = new TooltipPreviewController(service, TimeProvider.System);

        var run = controller.RunAsync(
            CreateSession("corrupt.mp4", sink),
            CancellationToken.None);

        await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(TooltipPreviewPhase.Cover, sink.Phases);
        Assert.Equal(TooltipPreviewPhase.Cover, sink.Phases[^1]);
    }

    [Fact]
    public Task H264Fixture_PreparesWithinFiveSeconds() =>
        AssertPreparesFrameWithinFiveSecondsAsync("h264.mp4", "h264");

    [Fact]
    public Task HevcFixture_PreparesWithinFiveSeconds() =>
        AssertPreparesFrameWithinFiveSecondsAsync("hevc.mkv", "hevc");

    [Fact]
    public async Task RapidReplacement_ObservedNativeProcessMaximumIsOne()
    {
        var observer = new RecordingProcessObserver();
        var service = CreateService(observer);
        await using var controller = new TooltipPreviewController(service, TimeProvider.System);
        var first = controller.RunAsync(
            CreateSession("h264.mp4", new RecordingPreviewSink(displayFrames: false)),
            CancellationToken.None);
        await observer.WaitForStartedAsync("ffmpeg.exe", 1, TimeSpan.FromSeconds(5));

        var second = controller.RunAsync(
            CreateSession("hevc.mkv", new RecordingPreviewSink(displayFrames: false)),
            CancellationToken.None);

        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await observer.WaitForStartedAsync("ffmpeg.exe", 2, TimeSpan.FromSeconds(5));
        await second.WaitAsync(TimeSpan.FromSeconds(8));

        Assert.Equal(1, observer.MaximumLiveProcessCount);
        Assert.Equal(0, observer.LiveProcessCount);
    }

    [Fact]
    public async Task SensitiveFailure_LogContainsOnlyAllowlistedFields()
    {
        var capture = await CaptureSensitiveFailureAsync();
        var forbidden = new[]
        {
            "AIRI_USER_SENTINEL",
            "AIRI_FOLDER_SENTINEL",
            "AIRI_FILENAME_SENTINEL",
            "AIRI_SENTINEL_TITLE",
            "AIRI_SENTINEL_TAG",
            InjectedFfmpegFailureRunner.RawStderr
        };

        Assert.All(forbidden, value =>
            Assert.DoesNotContain(value, capture.Message, StringComparison.OrdinalIgnoreCase));
        Assert.Null(capture.Exception);
        var keys = capture.Message
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)[0])
            .ToArray();
        Assert.Equal(
            new[]
            {
                "sourceId", "extension", "container", "codec", "stage",
                "result", "exitCode", "prepareMs", "playbackMs"
            },
            keys);
    }

    [Fact]
    public async Task SensitiveFailure_OmitsStderrSummaryEntirely()
    {
        var capture = await CaptureSensitiveFailureAsync();

        Assert.DoesNotContain("stderrSummary", capture.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stage=Decode", capture.Message, StringComparison.Ordinal);
        Assert.Contains("result=ProcessFailed", capture.Message, StringComparison.Ordinal);
        Assert.Contains("exitCode=23", capture.Message, StringComparison.Ordinal);
        Assert.Null(capture.Exception);
    }

    private static async Task AssertPreparesFrameWithinFiveSecondsAsync(
        string fileName,
        string codec)
    {
        var service = CreateService();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var preview = await service.PrepareAsync(CreateRequest(fileName), timeout.Token);
        var frames = await ReadFramesAsync(preview, 1, timeout.Token);
        var probe = await new FfmpegCorePreviewBackend(BinaryFolder)
            .ProbeAsync(FixturePath(fileName), timeout.Token);

        Assert.Single(frames);
        Assert.Equal(codec, probe.Codec, ignoreCase: true);
    }

    private static async Task<LogCapture> CaptureSensitiveFailureAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "AIRI_USER_SENTINEL",
            "AIRI_FOLDER_SENTINEL",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "AIRI_FILENAME_SENTINEL.mp4");
        File.Copy(FixturePath("sensitive-failure.mp4"), source);
        var runner = new InjectedFfmpegFailureRunner(new MediaProcessRunner());
        var service = new FfmpegVideoPreviewService(
            new FfmpegCorePreviewBackend(BinaryFolder, runner),
            new VideoPreviewLogFormatter(Enumerable.Range(0, 32).Select(value => (byte)value).ToArray()));
        var observed = new TaskCompletionSource<LogCapture>(TaskCreationOptions.RunContinuationsAsynchronously);
        AppLogger.TestObserver = (level, message, exception) =>
        {
            if (level == "ERROR" && message.Contains("sourceId=", StringComparison.Ordinal))
            {
                observed.TrySetResult(new LogCapture(message, exception));
            }
        };

        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<VideoPreviewUnavailableException>(() =>
                service.PrepareAsync(
                    new VideoPreviewRequest(source, 480, 350, 15, TimeSpan.FromSeconds(10)),
                    timeout.Token));
            return await observed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            AppLogger.TestObserver = null;
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<List<VideoPreviewFrame>> ReadFramesAsync(
        IPreparedVideoPreview preview,
        int count,
        CancellationToken cancellationToken)
    {
        var frames = new List<VideoPreviewFrame>();
        await foreach (var frame in preview.ReadFramesAsync(cancellationToken))
        {
            frames.Add(frame);
            if (frames.Count == count) break;
        }
        return frames;
    }

    private static FfmpegVideoPreviewService CreateService(IMediaProcessObserver? observer = null) =>
        new(new FfmpegCorePreviewBackend(BinaryFolder, new MediaProcessRunner(observer)));

    private static VideoPreviewRequest CreateRequest(string fileName) =>
        new(FixturePath(fileName), 480, 350, 15, TimeSpan.FromSeconds(10));

    private static TooltipPreviewSession CreateSession(
        string fileName,
        ITooltipPreviewSink sink) =>
        new(Guid.NewGuid(), CreateRequest(fileName), true, true, sink);

    private static bool IsFfmpeg(MediaProcessRequest request) =>
        Path.GetFileName(request.FileName).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase);

    private static string BinaryFolder => Path.Combine(
        AppContext.BaseDirectory,
        "resources",
        "ffmpeg",
        "win-x64");

    private static string FixturePath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "VideoPreview",
        fileName);

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

    private sealed record LogCapture(string Message, Exception? Exception);

    private sealed class RecordingMediaProcessRunner : IMediaProcessRunner
    {
        private readonly IMediaProcessRunner _inner;

        public RecordingMediaProcessRunner(IMediaProcessRunner inner) => _inner = inner;

        public ConcurrentQueue<MediaProcessRequest> Requests { get; } = new();

        public Task<MediaProcessResult> RunAsync(
            MediaProcessRequest request,
            Func<Stream, CancellationToken, Task> readOutputAsync,
            CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            return _inner.RunAsync(request, readOutputAsync, cancellationToken);
        }
    }

    private sealed class InjectedFfmpegFailureRunner : IMediaProcessRunner
    {
        public const string RawStderr = "AIRI_RAW_STDERR_SENTINEL secret-path payload";
        private readonly IMediaProcessRunner _inner;

        public InjectedFfmpegFailureRunner(IMediaProcessRunner inner) => _inner = inner;

        public async Task<MediaProcessResult> RunAsync(
            MediaProcessRequest request,
            Func<Stream, CancellationToken, Task> readOutputAsync,
            CancellationToken cancellationToken)
        {
            if (!IsFfmpeg(request))
            {
                return await _inner.RunAsync(request, readOutputAsync, cancellationToken);
            }

            return new MediaProcessResult(23, Encoding.UTF8.GetByteCount(RawStderr), false);
        }
    }

    private sealed class RecordingProcessObserver : IMediaProcessObserver
    {
        private readonly object _sync = new();
        private readonly List<ProcessEvent> _events = new();
        private readonly HashSet<(int Id, DateTime Start)> _live = new();
        private readonly SemaphoreSlim _changed = new(0);

        public int MaximumLiveProcessCount { get; private set; }
        public int LiveProcessCount
        {
            get { lock (_sync) return _live.Count; }
        }

        public void Started(MediaProcessIdentity identity)
        {
            lock (_sync)
            {
                _events.Add(new ProcessEvent(true, identity));
                _live.Add((identity.ProcessId, identity.StartTimeUtc));
                MaximumLiveProcessCount = Math.Max(MaximumLiveProcessCount, _live.Count);
            }
            _changed.Release();
        }

        public void Exited(MediaProcessIdentity identity, int? exitCode)
        {
            lock (_sync)
            {
                _events.Add(new ProcessEvent(false, identity));
                _live.Remove((identity.ProcessId, identity.StartTimeUtc));
            }
            _changed.Release();
        }

        public Task<MediaProcessIdentity> WaitForStartedAsync(
            string executableName,
            int ordinal,
            TimeSpan timeout) => WaitAsync(
                events => events
                    .Where(item => item.Started &&
                        Path.GetFileName(item.Identity.ExecutablePath).Equals(
                            executableName,
                            StringComparison.OrdinalIgnoreCase))
                    .Select(item => item.Identity)
                    .ElementAtOrDefault(ordinal - 1),
                timeout);

        public Task<MediaProcessIdentity> WaitForExitAsync(
            MediaProcessIdentity identity,
            TimeSpan timeout) => WaitAsync(
                events => events.Any(item =>
                    !item.Started && SameIdentity(item.Identity, identity))
                        ? identity
                        : null,
                timeout);

        public int StartIndex(MediaProcessIdentity identity)
        {
            lock (_sync)
            {
                return _events.FindIndex(item => item.Started && SameIdentity(item.Identity, identity));
            }
        }

        public int ExitIndex(MediaProcessIdentity identity)
        {
            lock (_sync)
            {
                return _events.FindIndex(item => !item.Started && SameIdentity(item.Identity, identity));
            }
        }

        private async Task<MediaProcessIdentity> WaitAsync(
            Func<IReadOnlyList<ProcessEvent>, MediaProcessIdentity?> select,
            TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                MediaProcessIdentity? match;
                lock (_sync)
                {
                    match = select(_events);
                }
                if (match is not null) return match;
                await _changed.WaitAsync(cancellation.Token);
            }
        }

        private static bool SameIdentity(MediaProcessIdentity left, MediaProcessIdentity right) =>
            left.ProcessId == right.ProcessId && left.StartTimeUtc == right.StartTimeUtc;

        private sealed record ProcessEvent(bool Started, MediaProcessIdentity Identity);
    }

    private sealed class RecordingPreviewSink : ITooltipPreviewSink
    {
        private readonly bool _displayFrames;

        public RecordingPreviewSink(bool displayFrames) => _displayFrames = displayFrames;

        public List<TooltipPreviewPhase> Phases { get; } = new();
        public bool IsCurrent(Guid sessionId) => true;

        public ValueTask<DispatcherCallbackResult> SetPhaseAsync(
            Guid sessionId,
            TooltipPreviewPhase phase,
            CancellationToken cancellationToken)
        {
            Phases.Add(phase);
            return ValueTask.FromResult(new DispatcherCallbackResult(
                true,
                TimeProvider.System.GetTimestamp(),
                TimeSpan.Zero));
        }

        public ValueTask<FramePresentationResult> ShowFrameAsync(
            Guid sessionId,
            int pixelWidth,
            int pixelHeight,
            VideoPreviewFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FramePresentationResult(
                _displayFrames,
                TimeProvider.System.GetTimestamp(),
                TimeSpan.Zero));
    }
}
