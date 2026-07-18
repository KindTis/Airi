namespace Airi.Infrastructure;

public interface IVideoPreviewPerformanceRecorder
{
    void RecordSessionOpened(Guid sessionId, long openedTimestamp);
    void RecordPrepared(Guid sessionId, long completedTimestamp);
    void RecordPlaybackStarted(Guid sessionId, long startedTimestamp, TimeSpan plannedDuration);
    void RecordPhaseChanged(Guid sessionId, TooltipPreviewPhase phase, DispatcherCallbackResult callback);
    void RecordPresentation(Guid sessionId, FramePresentationResult presentation);
}
