using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Airi.Domain;

namespace Airi.ViewModels;

internal sealed record ExistingScanItemSnapshot(
    string NormalizedPath,
    VideoEntry EntrySnapshot,
    VideoItem ItemIdentity);

internal sealed record ScanPreparationSeed(
    TargetFolder[] TargetReferences,
    VideoEntry[] VideoReferences,
    KeyValuePair<string, VideoItem>[] ItemIdentitiesByNormalizedPath);

internal sealed record ScanPreparationSnapshot(
    IReadOnlyList<TargetFolder> Targets,
    IReadOnlyList<VideoEntry> Videos,
    IReadOnlyDictionary<string, ExistingScanItemSnapshot> ExistingByPath);

internal abstract record ScanApplyOperation;

internal sealed record UpdateScanItem(
    VideoItem Item,
    string AbsolutePath,
    long SizeBytes,
    DateTime LastWriteUtc,
    DateTime CreatedUtc,
    VideoPresenceState Presence) : ScanApplyOperation;

internal sealed record AddScanItem(
    VideoEntry Entry,
    VideoItem Item,
    string MetadataPath) : ScanApplyOperation;

internal sealed class ScanApplyPlan
{
    public ScanApplyPlan(
        LibraryData finalLibrary,
        IEnumerable<ScanApplyOperation> operations,
        IEnumerable<string> metadataPaths,
        IEnumerable<string> actorSnapshot,
        DateTime scanTimestamp,
        int addedCount,
        int missingCount,
        int updatedCount)
    {
        FinalLibrary = finalLibrary ?? throw new ArgumentNullException(nameof(finalLibrary));
        Operations = Array.AsReadOnly((operations ?? throw new ArgumentNullException(nameof(operations))).ToArray());
        MetadataPaths = Array.AsReadOnly((metadataPaths ?? throw new ArgumentNullException(nameof(metadataPaths))).ToArray());
        ActorSnapshot = Array.AsReadOnly((actorSnapshot ?? throw new ArgumentNullException(nameof(actorSnapshot))).ToArray());
        ScanTimestamp = scanTimestamp;
        AddedCount = addedCount;
        MissingCount = missingCount;
        UpdatedCount = updatedCount;
    }

    public LibraryData FinalLibrary { get; }
    public ReadOnlyCollection<ScanApplyOperation> Operations { get; }
    public ReadOnlyCollection<string> MetadataPaths { get; }
    public ReadOnlyCollection<string> ActorSnapshot { get; }
    public DateTime ScanTimestamp { get; }
    public int AddedCount { get; }
    public int MissingCount { get; }
    public int UpdatedCount { get; }
}
