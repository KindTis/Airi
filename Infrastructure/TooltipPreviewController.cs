using Airi.Services.VideoPreview;

namespace Airi.Infrastructure;

public sealed class TooltipPreviewController : IAsyncDisposable
{
    private static readonly TimeSpan MinimumCover = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PrepareDeadline = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaximumFrameGap = TimeSpan.FromMilliseconds(250);

    private readonly IVideoPreviewService _service;
    private readonly TimeProvider _timeProvider;
    private readonly IVideoPreviewPerformanceRecorder? _performanceRecorder;
    private readonly object _sync = new();
    private readonly HashSet<Guid> _seenSessions = new();
    private ActiveSession? _active;
    private bool _disposed;

    public TooltipPreviewController(
        IVideoPreviewService service,
        TimeProvider timeProvider,
        IVideoPreviewPerformanceRecorder? performanceRecorder = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _performanceRecorder = performanceRecorder;
    }

    public TimeProvider Clock => _timeProvider;

    public Task RunAsync(TooltipPreviewSession session, CancellationToken cancellationToken)
    {
        var openedTimestamp = _timeProvider.GetTimestamp();
        _performanceRecorder?.RecordSessionOpened(session.Id, openedTimestamp);
        ActiveSession active;
        ActiveSession? previous;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_seenSessions.Add(session.Id)) return Task.CompletedTask;
            active = new ActiveSession(session, openedTimestamp, cancellationToken);
            previous = _active;
            _active = active;
            previous?.Cancel();
        }

        _ = RunAndCompleteAsync(active, previous);
        return active.Completion;
    }

    public async Task StopAsync(Guid sessionId)
    {
        ActiveSession? active;
        lock (_sync)
        {
            active = _active?.Session.Id == sessionId ? _active : null;
            active?.Cancel();
        }
        if (active is not null)
        {
            await active.Completion.ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        ActiveSession? active;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active;
            active?.Cancel();
        }
        if (active is not null)
        {
            await active.Completion.ConfigureAwait(false);
        }
    }

    private async Task RunAndCompleteAsync(ActiveSession active, ActiveSession? previous)
    {
        try
        {
            if (previous is not null)
            {
                await previous.Completion.ConfigureAwait(false);
            }
            active.Token.ThrowIfCancellationRequested();
            await RunCoreAsync(active).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (active.Token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await TrySetCoverAsync(active).ConfigureAwait(false);
        }
        finally
        {
            active.Dispose();
            lock (_sync)
            {
                if (ReferenceEquals(_active, active)) _active = null;
            }
            active.Complete();
        }
    }

    private async Task RunCoreAsync(ActiveSession active)
    {
        var session = active.Session;
        if (!session.AnimationsEnabled ||
            !session.PreviewEligible ||
            !session.Sink.IsCurrent(session.Id))
        {
            await SetPhaseAsync(session, TooltipPreviewPhase.Cover, active.Token).ConfigureAwait(false);
            return;
        }

        await SetPhaseAsync(session, TooltipPreviewPhase.Preparing, active.Token).ConfigureAwait(false);
        var elapsed = _timeProvider.GetElapsedTime(active.OpenedTimestamp);
        var remainingPrepare = PrepareDeadline - elapsed;
        if (remainingPrepare <= TimeSpan.Zero)
        {
            await SetPhaseAsync(session, TooltipPreviewPhase.Cover, active.Token).ConfigureAwait(false);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(active.Token);
        var prepareTask = CapturePrepareOutcome(
            _service.PrepareAsync(session.Request, deadline.Token));
        var timeoutTask = Task.Delay(remainingPrepare, _timeProvider, active.Token);
        await Task.WhenAny(prepareTask, timeoutTask).ConfigureAwait(false);
        if (!prepareTask.IsCompleted)
        {
            deadline.Cancel();
        }

        var outcome = await prepareTask.ConfigureAwait(false);
        var readyElapsed = _timeProvider.GetElapsedTime(
            active.OpenedTimestamp,
            outcome.CompletedTimestamp);
        if (outcome.Prepared is null || readyElapsed > PrepareDeadline)
        {
            if (outcome.Prepared is not null)
            {
                await outcome.Prepared.DisposeAsync().ConfigureAwait(false);
            }
            await SetPhaseAsync(session, TooltipPreviewPhase.Cover, active.Token).ConfigureAwait(false);
            return;
        }

        await using var prepared = outcome.Prepared;
        _performanceRecorder?.RecordPrepared(session.Id, outcome.CompletedTimestamp);
        elapsed = _timeProvider.GetElapsedTime(active.OpenedTimestamp);
        if (elapsed < MinimumCover)
        {
            await Task.Delay(MinimumCover - elapsed, _timeProvider, active.Token).ConfigureAwait(false);
        }

        var playing = await SetPhaseAsync(session, TooltipPreviewPhase.Playing, active.Token).ConfigureAwait(false);
        if (!playing.Applied) return;
        var playbackStartedTimestamp = _timeProvider.GetTimestamp();
        _performanceRecorder?.RecordPlaybackStarted(
            session.Id,
            playbackStartedTimestamp,
            prepared.PlaybackDuration);
        await PlayAsync(session, prepared, playbackStartedTimestamp, active.Token).ConfigureAwait(false);
        await SetPhaseAsync(session, TooltipPreviewPhase.Cover, active.Token).ConfigureAwait(false);
    }

    private async Task PlayAsync(
        TooltipPreviewSession session,
        IPreparedVideoPreview prepared,
        long playbackStartedTimestamp,
        CancellationToken cancellationToken)
    {
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var frames = prepared.ReadFramesAsync(playback.Token)
            .GetAsyncEnumerator(playback.Token);
        var playbackEnd = Add(playbackStartedTimestamp, prepared.PlaybackDuration);
        long? lastDisplayed = null;

        while (!playback.IsCancellationRequested)
        {
            var deadline = lastDisplayed is { } displayed
                ? Math.Min(playbackEnd, Add(displayed, MaximumFrameGap))
                : playbackEnd;
            if (_timeProvider.GetTimestamp() >= deadline) return;

            using var wait = CancellationTokenSource.CreateLinkedTokenSource(playback.Token);
            var moveNext = frames.MoveNextAsync().AsTask();
            var deadlineTask = DelayUntilAsync(deadline, wait.Token);
            var completed = await Task.WhenAny(moveNext, deadlineTask).ConfigureAwait(false);
            if (completed != moveNext)
            {
                playback.Cancel();
                try { await moveNext.ConfigureAwait(false); } catch (OperationCanceledException) { }
                return;
            }
            wait.Cancel();
            if (!await moveNext.ConfigureAwait(false)) return;

            var frame = frames.Current;
            var target = Add(playbackStartedTimestamp, frame.PresentationTimestamp);
            if (target >= playbackEnd) return;
            var now = _timeProvider.GetTimestamp();
            if (lastDisplayed is not null && now > target)
            {
                continue;
            }
            if (target > now)
            {
                var displayDeadline = lastDisplayed is { } prior
                    ? Math.Min(playbackEnd, Add(prior, MaximumFrameGap))
                    : playbackEnd;
                if (target > displayDeadline)
                {
                    await DelayUntilAsync(displayDeadline, playback.Token).ConfigureAwait(false);
                    return;
                }
                await DelayUntilAsync(target, playback.Token).ConfigureAwait(false);
            }

            if (!session.Sink.IsCurrent(session.Id)) return;
            var presentation = await session.Sink.ShowFrameAsync(
                session.Id,
                prepared.PixelWidth,
                prepared.PixelHeight,
                frame,
                playback.Token).ConfigureAwait(false);
            if (!presentation.Displayed) return;
            _performanceRecorder?.RecordPresentation(session.Id, presentation);
            if (lastDisplayed is { } previous &&
                _timeProvider.GetElapsedTime(previous, presentation.DisplayedTimestamp) > MaximumFrameGap)
            {
                return;
            }
            lastDisplayed = presentation.DisplayedTimestamp;
        }
    }

    private Task<PrepareOutcome> CapturePrepareOutcome(Task<IPreparedVideoPreview> task) =>
        task.ContinueWith(
            completed => completed.Status == TaskStatus.RanToCompletion
                ? new PrepareOutcome(completed.Result, _timeProvider.GetTimestamp())
                : new PrepareOutcome(null, _timeProvider.GetTimestamp()),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private async ValueTask<DispatcherCallbackResult> SetPhaseAsync(
        TooltipPreviewSession session,
        TooltipPreviewPhase phase,
        CancellationToken cancellationToken)
    {
        if (!session.Sink.IsCurrent(session.Id)) return default;
        var result = await session.Sink.SetPhaseAsync(session.Id, phase, cancellationToken)
            .ConfigureAwait(false);
        if (result.Applied)
        {
            _performanceRecorder?.RecordPhaseChanged(session.Id, phase, result);
        }
        return result;
    }

    private async Task TrySetCoverAsync(ActiveSession active)
    {
        if (active.Token.IsCancellationRequested) return;
        try
        {
            await SetPhaseAsync(active.Session, TooltipPreviewPhase.Cover, active.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DelayUntilAsync(long timestamp, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetTimestamp();
        if (timestamp <= now) return;
        await Task.Delay(_timeProvider.GetElapsedTime(now, timestamp), _timeProvider, cancellationToken)
            .ConfigureAwait(false);
    }

    private long Add(long timestamp, TimeSpan duration) =>
        checked(timestamp + (long)Math.Round(duration.TotalSeconds * _timeProvider.TimestampFrequency));

    private sealed record PrepareOutcome(
        IPreparedVideoPreview? Prepared,
        long CompletedTimestamp);

    private sealed class ActiveSession : IDisposable
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly TaskCompletionSource _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ActiveSession(
            TooltipPreviewSession session,
            long openedTimestamp,
            CancellationToken cancellationToken)
        {
            Session = session;
            OpenedTimestamp = openedTimestamp;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        }

        public TooltipPreviewSession Session { get; }
        public long OpenedTimestamp { get; }
        public CancellationToken Token => _cancellation.Token;
        public Task Completion => _completion.Task;
        public void Cancel() => _cancellation.Cancel();
        public void Complete() => _completion.TrySetResult();
        public void Dispose() => _cancellation.Dispose();
    }
}
