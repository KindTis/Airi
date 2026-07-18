using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Airi.Infrastructure;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class TooltipPreviewControllerTests
{
    [Fact]
    public async Task ReadyAtOneSecond_WaitsUntilTwoSecondsAndPlaysOnce()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        fixture.Service.CompletePrepare();
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        await fixture.DrainAsync();
        Assert.Empty(fixture.Sink.Frames);

        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.WaitForFirstFrameAsync();
        Assert.Single(fixture.Sink.Frames);
        fixture.Service.Prepared.CompleteFrames();
        await run;

        Assert.Equal(
            new[] { TooltipPreviewPhase.Preparing, TooltipPreviewPhase.Playing, TooltipPreviewPhase.Cover },
            fixture.Sink.Phases);
        Assert.Equal(1, fixture.Service.PrepareCallCount);
    }

    [Fact]
    public async Task ReadyAtThreeSeconds_PlaysImmediately()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(3));
        fixture.Service.CompletePrepare();
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        await fixture.WaitForFirstFrameAsync();

        Assert.Single(fixture.Sink.Frames);
        fixture.Service.Prepared.CompleteFrames();
        await run;
    }

    [Fact]
    public async Task NotReadyAtFiveSeconds_CancelsPreparationAndKeepsCover()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await run;

        Assert.True(fixture.Service.PrepareToken.IsCancellationRequested);
        Assert.Empty(fixture.Sink.Frames);
        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
    }

    [Fact]
    public async Task ReadyExactlyAtFiveSeconds_IsAccepted()
    {
        var fixture = new ControllerFixture
        {
            AutoCompleteAfter = TimeSpan.FromSeconds(5)
        };
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.WaitForFirstFrameAsync();

        Assert.Single(fixture.Sink.Frames);
        fixture.Service.Prepared.CompleteFrames();
        await run;
    }

    [Fact]
    public async Task ReadyAfterFiveSeconds_IsDisposedAndNotPlayed()
    {
        var fixture = new ControllerFixture
        {
            AutoCompleteAfter = TimeSpan.FromTicks(TimeSpan.FromSeconds(5).Ticks + 1),
            IgnorePreparationCancellation = true
        };
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.DrainAsync();
        fixture.Time.Advance(TimeSpan.FromTicks(1));
        await run;

        Assert.Equal(1, fixture.Service.Prepared.DisposeCount);
        Assert.Empty(fixture.Sink.Frames);
        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
    }

    [Fact]
    public async Task AnimationsDisabled_DoesNotCallPreviewService()
    {
        var fixture = new ControllerFixture();
        await fixture.RunAsync(animationsEnabled: false);

        Assert.Equal(0, fixture.Service.PrepareCallCount);
        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
    }

    [Fact]
    public async Task IneligibleReplacement_CancelsAndDisposesPlayingSessionWithoutNewServiceCall()
    {
        var fixture = new ControllerFixture();
        var first = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        await fixture.WaitForFirstFrameAsync();

        var second = fixture.RunAsync(previewEligible: false, sessionId: Guid.NewGuid());
        await Task.WhenAll(first, second);

        Assert.True(fixture.Service.PrepareToken.IsCancellationRequested);
        Assert.Equal(1, fixture.Service.Prepared.DisposeCount);
        Assert.Equal(1, fixture.Service.PrepareCallCount);
    }

    [Fact]
    public async Task StopDuringPreparation_CancelsService()
    {
        var fixture = new ControllerFixture();
        var sessionId = Guid.NewGuid();
        var run = fixture.RunAsync(sessionId: sessionId);
        await fixture.DrainAsync();

        await fixture.Controller.StopAsync(sessionId);
        await run;

        Assert.True(fixture.Service.PrepareToken.IsCancellationRequested);
    }

    [Fact]
    public async Task StaleSink_DoesNotPublishFrames()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Sink.Current = false;
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        fixture.Service.Prepared.CompleteFrames();
        await run;

        Assert.Empty(fixture.Sink.Frames);
    }

    [Fact]
    public async Task DisplayGapOverTwoHundredFiftyMilliseconds_FallsBackToCover()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        await fixture.WaitForFirstFrameAsync();
        Assert.Single(fixture.Sink.Frames);

        fixture.Time.Advance(TimeSpan.FromMilliseconds(251));
        await run;

        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
    }

    [Fact]
    public async Task DispatcherCallbackOverOneHundredMilliseconds_DoesNotStopRuntimePlayback()
    {
        var fixture = new ControllerFixture();
        fixture.Sink.ReportedCallbackDuration = TimeSpan.FromMilliseconds(101);
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        fixture.Service.Prepared.CompleteFrames();
        await run;

        Assert.Single(fixture.Sink.Frames);
        Assert.Contains(TooltipPreviewPhase.Playing, fixture.Sink.Phases);
    }

    [Fact]
    public async Task Recorder_ReceivesSessionPreparedPlaybackPhaseAndDisplayedFrameInOrder()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        fixture.Service.Prepared.CompleteFrames();
        await run;

        Assert.Equal(
            new[] { "opened", "phase:Preparing", "prepared", "phase:Playing", "playback", "frame", "phase:Cover" },
            fixture.Recorder.Events);
    }

    [Fact]
    public async Task SameSessionId_DoesNotRetryPreview()
    {
        var fixture = new ControllerFixture();
        var sessionId = Guid.NewGuid();

        await fixture.RunAsync(animationsEnabled: false, sessionId: sessionId);
        await fixture.RunAsync(sessionId: sessionId);

        Assert.Equal(0, fixture.Service.PrepareCallCount);
    }

    [Fact]
    public async Task PreparationFailure_FallsBackToCover()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.FailPrepare();
        await run;

        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
        Assert.Empty(fixture.Sink.Frames);
    }

    [Fact]
    public async Task PlaybackFailure_FallsBackToCover()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        fixture.Service.Prepared.FailFrames();
        await run;

        Assert.Single(fixture.Sink.Frames);
        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
    }

    [Fact]
    public async Task FramePtsAndMaximumDuration_UsePlaybackStartedTimestamp()
    {
        var fixture = new ControllerFixture();
        var run = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        fixture.Service.CompletePrepare();
        for (var index = 0; index <= 50; index++)
        {
            fixture.Service.Prepared.AddFrame(TimeSpan.FromMilliseconds(index * 200));
        }
        fixture.Service.Prepared.CompleteFrames();

        await fixture.Time.WaitForTimerDueAtAsync(TimeSpan.FromSeconds(2))
            .WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Time.Advance(TimeSpan.FromSeconds(1));
        await fixture.WaitForFirstFrameAsync();
        Assert.Single(fixture.Sink.Frames);
        for (var index = 1; index <= 50; index++)
        {
            fixture.Time.Advance(TimeSpan.FromMilliseconds(200));
            await fixture.DrainAsync();
        }
        await run;

        Assert.Equal(50, fixture.Sink.Frames.Count);
        Assert.Equal(TimeSpan.FromSeconds(12), fixture.Time.GetUtcNow() - DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public async Task PreviousDisposeDelay_IsIncludedInNewSessionFiveSecondDeadline()
    {
        var fixture = new ControllerFixture();
        fixture.Service.Prepared.BlockDispose = true;
        var first = fixture.RunAsync();
        await fixture.DrainAsync();
        fixture.Service.CompletePrepare();
        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.Service.Prepared.AddFrame(TimeSpan.Zero);
        await fixture.WaitForFirstFrameAsync();

        var second = fixture.RunAsync(sessionId: Guid.NewGuid());
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        fixture.Service.Prepared.ReleaseDispose();
        await Task.WhenAll(first, second);

        Assert.Equal(1, fixture.Service.PrepareCallCount);
        Assert.Equal(TooltipPreviewPhase.Cover, fixture.Sink.Phases[^1]);
    }

    private sealed class ControllerFixture
    {
        private TimeSpan? _autoCompleteAfter;
        private bool _ignorePreparationCancellation;

        public ControllerFixture()
        {
            Sink = new RecordingSink(Time);
            Service = new FakePreviewService(Time);
            Controller = new TooltipPreviewController(Service, Time, Recorder);
        }

        public ManualTimeProvider Time { get; } = new();
        public FakePreviewService Service { get; }
        public RecordingSink Sink { get; }
        public RecordingPerformanceRecorder Recorder { get; } = new();
        public TooltipPreviewController Controller { get; }

        public TimeSpan? AutoCompleteAfter
        {
            get => _autoCompleteAfter;
            init
            {
                _autoCompleteAfter = value;
                Service.AutoCompleteAfter = value;
            }
        }

        public bool IgnorePreparationCancellation
        {
            get => _ignorePreparationCancellation;
            init
            {
                _ignorePreparationCancellation = value;
                Service.IgnoreCancellation = value;
            }
        }

        public Task RunAsync(
            bool animationsEnabled = true,
            bool previewEligible = true,
            Guid? sessionId = null) =>
            Controller.RunAsync(
                new TooltipPreviewSession(
                    sessionId ?? Guid.NewGuid(),
                    new VideoPreviewRequest("C:\\media\\sample.mp4", 480, 350, 15, TimeSpan.FromSeconds(10)),
                    animationsEnabled,
                    previewEligible,
                    Sink),
                CancellationToken.None);

        public async Task DrainAsync()
        {
            await Task.Delay(1);
            for (var index = 0; index < 16; index++) await Task.Yield();
        }

        public Task WaitForFirstFrameAsync() =>
            Sink.FirstFrame.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class FakePreviewService : IVideoPreviewService
    {
        private readonly ManualTimeProvider _time;
        private readonly TaskCompletionSource<IPreparedVideoPreview> _prepare =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakePreviewService(ManualTimeProvider time) => _time = time;

        public FakePreparedPreview Prepared { get; } = new();
        public int PrepareCallCount { get; private set; }
        public CancellationToken PrepareToken { get; private set; }
        public TimeSpan? AutoCompleteAfter { get; set; }
        public bool IgnoreCancellation { get; set; }

        public Task<IPreparedVideoPreview> PrepareAsync(
            VideoPreviewRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            PrepareToken = cancellationToken;
            if (!IgnoreCancellation)
            {
                cancellationToken.Register(() => _prepare.TrySetCanceled(cancellationToken));
            }
            if (AutoCompleteAfter is { } delay)
            {
                _time.Schedule(delay, CompletePrepare);
            }
            return _prepare.Task;
        }

        public void CompletePrepare() => _prepare.TrySetResult(Prepared);
        public void FailPrepare() => _prepare.TrySetException(new VideoPreviewUnavailableException("failed"));
    }

    private sealed class FakePreparedPreview : IPreparedVideoPreview
    {
        private readonly Channel<VideoPreviewFrame> _frames = Channel.CreateUnbounded<VideoPreviewFrame>();
        private readonly TaskCompletionSource _disposeReleased =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PixelWidth => 4;
        public int PixelHeight => 4;
        public int FrameRate => 15;
        public TimeSpan PlaybackDuration => TimeSpan.FromSeconds(10);
        public int DisposeCount { get; private set; }
        public bool BlockDispose { get; set; }

        public void AddFrame(TimeSpan timestamp) =>
            _frames.Writer.TryWrite(new VideoPreviewFrame(new byte[4 * 4 * 4], timestamp));

        public void CompleteFrames() => _frames.Writer.TryComplete();
        public void FailFrames() => _frames.Writer.TryComplete(new IOException("frame failed"));
        public void ReleaseDispose() => _disposeReleased.TrySetResult();

        public async IAsyncEnumerable<VideoPreviewFrame> ReadFramesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(cancellationToken))
            {
                yield return frame;
            }
        }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            _frames.Writer.TryComplete();
            if (BlockDispose)
            {
                await _disposeReleased.Task;
            }
        }
    }

    private sealed class RecordingSink : ITooltipPreviewSink
    {
        private readonly TimeProvider _time;
        private readonly TaskCompletionSource _firstFrame =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingSink(TimeProvider time) => _time = time;

        public bool Current { get; set; } = true;
        public TimeSpan ReportedCallbackDuration { get; set; }
        public List<TooltipPreviewPhase> Phases { get; } = new();
        public List<VideoPreviewFrame> Frames { get; } = new();
        public Task FirstFrame => _firstFrame.Task;

        public bool IsCurrent(Guid sessionId) => Current;

        public ValueTask<DispatcherCallbackResult> SetPhaseAsync(
            Guid sessionId,
            TooltipPreviewPhase phase,
            CancellationToken cancellationToken)
        {
            if (!Current) return ValueTask.FromResult(default(DispatcherCallbackResult));
            Phases.Add(phase);
            return ValueTask.FromResult(new DispatcherCallbackResult(true, _time.GetTimestamp(), ReportedCallbackDuration));
        }

        public ValueTask<FramePresentationResult> ShowFrameAsync(
            Guid sessionId,
            int pixelWidth,
            int pixelHeight,
            VideoPreviewFrame frame,
            CancellationToken cancellationToken)
        {
            if (!Current) return ValueTask.FromResult(default(FramePresentationResult));
            Frames.Add(frame);
            _firstFrame.TrySetResult();
            return ValueTask.FromResult(new FramePresentationResult(true, _time.GetTimestamp(), ReportedCallbackDuration));
        }
    }

    private sealed class RecordingPerformanceRecorder : IVideoPreviewPerformanceRecorder
    {
        public List<string> Events { get; } = new();
        public void RecordSessionOpened(Guid sessionId, long openedTimestamp) => Events.Add("opened");
        public void RecordPrepared(Guid sessionId, long completedTimestamp) => Events.Add("prepared");
        public void RecordPlaybackStarted(Guid sessionId, long startedTimestamp, TimeSpan plannedDuration) => Events.Add("playback");
        public void RecordPhaseChanged(Guid sessionId, TooltipPreviewPhase phase, DispatcherCallbackResult callback) => Events.Add($"phase:{phase}");
        public void RecordPresentation(Guid sessionId, FramePresentationResult presentation) => Events.Add("frame");
    }
}
