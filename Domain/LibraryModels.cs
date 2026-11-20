using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Airi.Domain;

public enum MetadataField { Title, Date, Actors, Thumbnail, Tags, Description, Studio, Genres }
public enum LibraryFilePresence { Checking, Available, Missing }
public enum LibraryOperationIntent { Recycle, Restore }
public enum LibraryOperationState { Pending, Trashed, RestorePending, Restored, Uncertain, RepairRequired, Failed }

public record VideoMeta(
    string Title,
    DateOnly? Date,
    IReadOnlyList<string> Actors,
    string Thumbnail,
    IReadOnlyList<string> Tags,
    string Description)
{
    public string? Studio { get; init; }
    public IReadOnlyList<string> Genres { get; init; } = Array.Empty<string>();
    public string? RawDate { get; init; }
    public IReadOnlyList<MetadataField> ManualFieldLocks { get; init; } = Array.Empty<MetadataField>();
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
    [JsonIgnore] public bool DateNeedsReview => !string.IsNullOrWhiteSpace(RawDate) && Date is null;
}

public record VideoEntry(
    string Path,
    VideoMeta Meta,
    long SizeBytes = 0,
    DateTime LastModifiedUtc = default,
    DateTime CreatedUtc = default)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long Revision { get; init; }
    public LibraryFilePresence Presence { get; init; } = LibraryFilePresence.Checking;
    public DateTime? PresenceProbedAtUtc { get; init; }
    [JsonIgnore] public string? OriginalLastModifiedText { get; init; }
    [JsonIgnore] public string? OriginalCreatedText { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record LibraryJournalEntry
{
    public Guid OperationId { get; init; } = Guid.NewGuid();
    public Guid EntryId { get; init; }
    public long BaseRevision { get; init; }
    public string OriginalPath { get; init; } = string.Empty;
    public VideoEntry? Snapshot { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;
    public LibraryOperationIntent Intent { get; init; }
    public LibraryOperationState State { get; init; }
    public string? Outcome { get; init; }
    public string? Reason { get; init; }
    public string? NativeDiagnostics { get; init; }
    public string? InspectionReason { get; init; }
    public string? ObservedSourcePath { get; init; }
    public string? ObservedDestinationPath { get; init; }
    public string? ReceiptJson { get; init; }
    public bool MoveConfirmed { get; init; }
    public bool RestoreAvailable { get; init; }
    public string? Error { get; init; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public class LibraryData
{
    public int Version { get; set; } = 2;
    public List<VideoEntry> Videos { get; set; } = new();
    public List<LibraryJournalEntry> Journal { get; set; } = new();
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }

    public LibraryData WithVideos(IEnumerable<VideoEntry> videos) => new()
    {
        Version = Version,
        Videos = videos.ToList(),
        Journal = Journal.ToList(),
        AdditionalData = AdditionalData is null ? null : new(AdditionalData)
    };

    public LibraryJournalEntry? LatestOperation(Guid entryId) =>
        Journal.LastOrDefault(record => record.EntryId == entryId);

    public bool IsInRecycleBin(Guid entryId) => LatestOperation(entryId)?.State is
        LibraryOperationState.Pending or LibraryOperationState.Trashed or
        LibraryOperationState.RestorePending or LibraryOperationState.Uncertain or
        LibraryOperationState.RepairRequired;
}
