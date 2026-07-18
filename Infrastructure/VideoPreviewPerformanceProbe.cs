using Airi.Services.VideoPreview;

namespace Airi.Infrastructure;

internal sealed record VideoPreviewPerformanceResult(
    bool Passed,
    IReadOnlyList<string> FailureReasons,
    TimeSpan MaximumDispatcherDuration,
    double AverageFramesPerSecond,
    TimeSpan MaximumFrameGap,
    int MaximumLiveProcessCount,
    int RemainingLiveProcessCount,
    Guid? SessionId,
    TimeSpan? PrepareDuration,
    TimeSpan? PlaybackDuration,
    TimeSpan? PlannedPlaybackDuration);

internal sealed class VideoPreviewPerformanceProbe :
    IVideoPreviewPerformanceRecorder,
    IMediaProcessObserver
{
    private static readonly TimeSpan MaximumDispatcher = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan MaximumGap = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumPlayback = TimeSpan.FromSeconds(10);

    private readonly object _sync = new();
    private readonly bool _enabled;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, SessionRecord> _sessions = new();
    private readonly List<TimeSpan> _directDispatcherCallbacks = new();
    private readonly List<TimeSpan> _directDisplayedFrames = new();
    private readonly HashSet<(int ProcessId, DateTime StartTimeUtc)> _liveProcesses = new();
    private int _maximumLiveProcessCount;

    private VideoPreviewPerformanceProbe(bool enabled, TimeProvider timeProvider)
    {
        _enabled = enabled;
        _timeProvider = timeProvider;
    }

    public static VideoPreviewPerformanceProbe Disabled { get; } =
        new(false, TimeProvider.System);

    public static VideoPreviewPerformanceProbe CreateEnabled(TimeProvider? timeProvider = null) =>
        new(true, timeProvider ?? TimeProvider.System);

    public bool HasCompletedSession
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Values.Any(session => session.PlaybackEndedTimestamp is not null);
            }
        }
    }

    public bool HasStartedPlayback
    {
        get
        {
            lock (_sync)
            {
                return _sessions.Values.Any(session => session.PlaybackStartedTimestamp is not null);
            }
        }
    }

    public int LiveProcessCount
    {
        get { lock (_sync) return _liveProcesses.Count; }
    }

    public void RecordDispatcherCallback(TimeSpan duration)
    {
        if (!_enabled) return;
        lock (_sync) _directDispatcherCallbacks.Add(duration);
    }

    public void RecordDisplayedFrame(TimeSpan elapsed)
    {
        if (!_enabled) return;
        lock (_sync) _directDisplayedFrames.Add(elapsed);
    }

    public void RecordSessionOpened(Guid sessionId, long openedTimestamp)
    {
        if (!_enabled) return;
        lock (_sync) _sessions[sessionId] = new SessionRecord(openedTimestamp);
    }

    public void RecordPrepared(Guid sessionId, long completedTimestamp)
    {
        if (!_enabled) return;
        lock (_sync) GetSession(sessionId).PreparedTimestamp = completedTimestamp;
    }

    public void RecordPlaybackStarted(
        Guid sessionId,
        long startedTimestamp,
        TimeSpan plannedDuration)
    {
        if (!_enabled) return;
        lock (_sync)
        {
            var session = GetSession(sessionId);
            session.PlaybackStartedTimestamp = startedTimestamp;
            session.PlannedPlaybackDuration = plannedDuration;
        }
    }

    public void RecordPhaseChanged(
        Guid sessionId,
        TooltipPreviewPhase phase,
        DispatcherCallbackResult callback)
    {
        if (!_enabled || !callback.Applied) return;
        lock (_sync)
        {
            var session = GetSession(sessionId);
            session.DispatcherCallbacks.Add(callback.CallbackDuration);
            if (phase is TooltipPreviewPhase.Cover or TooltipPreviewPhase.Closed &&
                session.PlaybackStartedTimestamp is not null)
            {
                session.PlaybackEndedTimestamp ??= callback.CompletedTimestamp;
            }
        }
    }

    public void RecordPresentation(Guid sessionId, FramePresentationResult presentation)
    {
        if (!_enabled || !presentation.Displayed) return;
        lock (_sync)
        {
            var session = GetSession(sessionId);
            session.DispatcherCallbacks.Add(presentation.CallbackDuration);
            session.DisplayedFrameTimestamps.Add(presentation.DisplayedTimestamp);
        }
    }

    public void Started(MediaProcessIdentity identity)
    {
        if (!_enabled) return;
        lock (_sync)
        {
            _liveProcesses.Add((identity.ProcessId, identity.StartTimeUtc));
            _maximumLiveProcessCount = Math.Max(_maximumLiveProcessCount, _liveProcesses.Count);
        }
    }

    public void Exited(MediaProcessIdentity identity, int? exitCode)
    {
        if (!_enabled) return;
        lock (_sync) _liveProcesses.Remove((identity.ProcessId, identity.StartTimeUtc));
    }

    public VideoPreviewPerformanceResult Complete(TimeSpan measurementDuration)
    {
        lock (_sync)
        {
            var selected = _sessions.OrderBy(pair => pair.Value.OpenedTimestamp).LastOrDefault();
            var session = _sessions.Count == 0 ? null : selected.Value;
            Guid? sessionId = _sessions.Count == 0 ? null : selected.Key;
            var callbacks = session is null
                ? _directDispatcherCallbacks.ToArray()
                : session.DispatcherCallbacks.ToArray();
            var frameOffsets = session is null
                ? _directDisplayedFrames.ToArray()
                : ToOffsets(session.DisplayedFrameTimestamps);
            TimeSpan? prepareDuration = session?.PreparedTimestamp is { } prepared
                ? Elapsed(session.OpenedTimestamp, prepared)
                : null;
            TimeSpan? playbackDuration = session?.PlaybackStartedTimestamp is { } started &&
                                   session.PlaybackEndedTimestamp is { } ended
                ? Elapsed(started, ended)
                : null;
            var evaluationDuration = playbackDuration ?? measurementDuration;
            var maximumDispatcher = callbacks.DefaultIfEmpty().Max();
            var maximumFrameGap = MaximumFrameGap(frameOffsets);
            var averageFramesPerSecond = evaluationDuration > TimeSpan.Zero
                ? frameOffsets.Length / evaluationDuration.TotalSeconds
                : 0d;
            var failures = new List<string>();

            if (maximumDispatcher > MaximumDispatcher)
            {
                failures.Add("dispatcher callback exceeded 100 ms");
            }
            if (averageFramesPerSecond < 12d)
            {
                failures.Add("average displayed frame rate was below 12 fps");
            }
            if (maximumFrameGap > MaximumGap)
            {
                failures.Add("displayed frame gap exceeded 250 ms");
            }
            if (_maximumLiveProcessCount > 1)
            {
                failures.Add("more than one media process was live");
            }
            if (_liveProcesses.Count != 0)
            {
                failures.Add("media process remained live");
            }

            foreach (var recordedSession in _sessions.Values)
            {
                ValidateSession(recordedSession, failures);
            }

            return new VideoPreviewPerformanceResult(
                failures.Count == 0,
                failures,
                maximumDispatcher,
                averageFramesPerSecond,
                maximumFrameGap,
                _maximumLiveProcessCount,
                _liveProcesses.Count,
                sessionId,
                prepareDuration,
                playbackDuration,
                session?.PlannedPlaybackDuration);
        }
    }

    private void ValidateSession(SessionRecord session, List<string> failures)
    {
        if (session.PreparedTimestamp is null ||
            session.PlaybackStartedTimestamp is null ||
            session.PlaybackEndedTimestamp is null ||
            session.PlannedPlaybackDuration is null)
        {
            failures.Add("preview session was incomplete");
            return;
        }

        var ordered = session.OpenedTimestamp <= session.PreparedTimestamp &&
                      session.PreparedTimestamp <= session.PlaybackStartedTimestamp &&
                      session.PlaybackStartedTimestamp <= session.PlaybackEndedTimestamp &&
                      session.DisplayedFrameTimestamps.All(timestamp =>
                          timestamp >= session.PlaybackStartedTimestamp &&
                          timestamp <= session.PlaybackEndedTimestamp);
        if (!ordered)
        {
            failures.Add("performance events used inconsistent timestamp domains");
        }

        var resolution = TimeSpan.FromSeconds(1d / _timeProvider.TimestampFrequency);
        var playbackDuration = Elapsed(
            session.PlaybackStartedTimestamp.Value,
            session.PlaybackEndedTimestamp.Value);
        if (session.PlannedPlaybackDuration > MaximumPlayback ||
            playbackDuration > session.PlannedPlaybackDuration + resolution)
        {
            failures.Add("playback exceeded its planned duration");
        }
    }

    private SessionRecord GetSession(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            session = new SessionRecord(long.MinValue);
            _sessions.Add(sessionId, session);
        }
        return session;
    }

    private TimeSpan Elapsed(long start, long end) =>
        end >= start ? _timeProvider.GetElapsedTime(start, end) : TimeSpan.MinValue;

    private TimeSpan[] ToOffsets(IReadOnlyList<long> timestamps)
    {
        if (timestamps.Count == 0) return Array.Empty<TimeSpan>();
        var origin = timestamps[0];
        return timestamps.Select(timestamp => Elapsed(origin, timestamp)).ToArray();
    }

    private static TimeSpan MaximumFrameGap(IReadOnlyList<TimeSpan> frames)
    {
        var maximum = TimeSpan.Zero;
        for (var index = 1; index < frames.Count; index++)
        {
            var gap = frames[index] - frames[index - 1];
            if (gap > maximum) maximum = gap;
        }
        return maximum;
    }

    private sealed class SessionRecord
    {
        public SessionRecord(long openedTimestamp) => OpenedTimestamp = openedTimestamp;

        public long OpenedTimestamp { get; }
        public long? PreparedTimestamp { get; set; }
        public long? PlaybackStartedTimestamp { get; set; }
        public long? PlaybackEndedTimestamp { get; set; }
        public TimeSpan? PlannedPlaybackDuration { get; set; }
        public List<TimeSpan> DispatcherCallbacks { get; } = new();
        public List<long> DisplayedFrameTimestamps { get; } = new();
    }
}
