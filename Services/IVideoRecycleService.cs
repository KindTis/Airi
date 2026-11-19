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
    internal string? Diagnostics { get; init; }
    // Observation from the validated target callback; it does not prove the operation completed.
    internal string? ObservedDestinationPath { get; init; }
}

public interface IVideoRecycleService
{
    Task<VideoRecycleResult> RecycleAsync(string absolutePath);
}
