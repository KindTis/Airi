namespace Airi.Services;

public enum VideoRecycleOutcome
{
    Succeeded,
    Cancelled,
    Failed,
    Unknown
}

public sealed record VideoRecycleResult(VideoRecycleOutcome Outcome, string? Reason = null, string? RecycledPath = null)
{
    // Opaque, versioned native evidence. Only a validated callback and file identity can issue it.
    public string? Receipt { get; init; }
    public bool MovementConfirmed { get; init; }
    public bool SideEffectsKnownAbsent { get; init; }
    internal string? Diagnostics { get; init; }
    // Observation from the validated target callback; it does not prove the operation completed.
    internal string? ObservedDestinationPath { get; init; }
}

public interface IVideoRecycleService
{
    Task<VideoRecycleResult> RecycleAsync(string absolutePath);

    Task<VideoRecycleResult> RestoreAsync(string receipt) => Task.FromResult(
        new VideoRecycleResult(VideoRecycleOutcome.Failed, "이 서비스는 정확한 복원을 지원하지 않습니다.") { SideEffectsKnownAbsent = true });

    Task<VideoRecycleInspection> InspectAsync(string absolutePath, string? receipt = null) => Task.FromResult(
        new VideoRecycleInspection(VideoFileEvidence.Unknown, VideoFileEvidence.Unknown, "이 서비스는 파일 동일성 검사를 지원하지 않습니다."));
}

public enum VideoFileEvidence
{
    Unknown,
    Missing,
    PresentUnverified,
    MatchesReceipt,
    Different
}

public sealed record VideoRecycleInspection(VideoFileEvidence SourceState, VideoFileEvidence RecycleState, string? Reason = null);
