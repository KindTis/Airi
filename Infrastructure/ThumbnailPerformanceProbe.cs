using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace Airi.Infrastructure;

internal enum ThumbnailMeasurementPhase
{
    Cold,
    Warm
}

internal enum StartupTimingMarker
{
    StartupMeasurementBegin,
    MainWindowLoaded,
    LibraryLoaded,
    FirstBatchPublished,
    VisualFirstMeaningfulCard,
    VisualFirstThumbnail,
    FirstThumbnailApplied,
    AllItemsPublished,
    StartupTerminal
}

internal enum ThumbnailResourceCheckpointKind
{
    PhaseStart,
    VisualFirstMeaningfulCard,
    VisualFirstThumbnail,
    FirstSteady,
    StartupTerminal,
    PhaseEnd
}

internal enum ThumbnailDecodedOwnerKind
{
    LoaderCache,
    RealizedItemSource,
    Unexpected
}

internal readonly record struct ThumbnailPhaseBoundaryState(
    int ActiveDecodeCount,
    int ViewModelInFlightCount,
    int RegistrationCount,
    int PreviousRuntimeStateCount,
    bool DispatcherDrained);

internal readonly record struct ThumbnailPhaseSealState(
    int ActiveDecodeCount,
    int ViewModelInFlightCount,
    bool StableFor500Milliseconds,
    bool AllActiveRegistrationsTerminal);

internal readonly record struct ThumbnailTimingPoint(
    long ElapsedTicks,
    double ElapsedMilliseconds);

internal readonly record struct ThumbnailFallbackInitializationRecord(
    long ElapsedTicks,
    double ElapsedMilliseconds,
    int ThreadId);

internal readonly record struct ThumbnailResourceCheckpoint(
    ThumbnailResourceCheckpointKind Kind,
    long ElapsedTicks,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int Gc0CollectionCount,
    int Gc1CollectionCount,
    int Gc2CollectionCount,
    int LoaderCacheEntryCount,
    int RealizedNonFallbackItemSourceCount,
    int RegistrationCount,
    int DecodedStrongReferenceCount,
    int OwnerSlotBound);

internal readonly record struct ThumbnailCheckpointMaximum(
    long WorkingSetBytes,
    long ManagedHeapBytes,
    int RegistrationCount,
    int DecodedStrongReferenceCount,
    int OwnerSlotBound);

internal readonly record struct ThumbnailGcPhaseDelta(int Gen0, int Gen1, int Gen2);

internal sealed record ThumbnailPerformanceSnapshot(
    int SchemaVersion,
    ThumbnailMeasurementPhase Phase,
    IReadOnlyDictionary<StartupTimingMarker, ThumbnailTimingPoint> Markers,
    IReadOnlyList<ThumbnailRequestProbeRecord> Requests,
    IReadOnlyList<ThumbnailImageRegistrationRecord> Registrations,
    IReadOnlyList<StartupDispatcherBatchRecord> DispatcherBatches,
    IReadOnlyList<ThumbnailResourceCheckpoint> ResourceCheckpoints,
    ThumbnailCheckpointMaximum CheckpointMaximum,
    ThumbnailGcPhaseDelta GcPhaseDelta,
    ThumbnailFallbackInitializationRecord? FallbackInitialization,
    int ActiveRegistrationCount,
    int MaxRegistrationCount);

internal readonly record struct ThumbnailRequestProbeRecord(
    long ItemIdentity,
    long Generation,
    string NormalizedPath,
    int DecodePixelWidth,
    bool InRealizationWindow);

internal readonly record struct ThumbnailImageRegistrationRecord(
    int ImageIdentity,
    long ItemIdentity,
    bool Enter);

internal readonly record struct StartupDispatcherBatchRecord(
    string Kind,
    int ItemCount,
    long ElapsedTicks,
    int ThreadId);

internal sealed class ThumbnailPerformanceProbe
{
    public const int SchemaVersion = 2;

    private readonly object _sync = new();
    private readonly bool _enabled;
    private readonly Dictionary<StartupTimingMarker, ThumbnailTimingPoint> _markers = new();
    private readonly List<ThumbnailRequestProbeRecord> _requestRecords = new();
    private readonly List<ThumbnailImageRegistrationRecord> _registrationRecords = new();
    private readonly List<StartupDispatcherBatchRecord> _dispatcherBatchRecords = new();
    private readonly List<ThumbnailResourceCheckpoint> _resourceCheckpoints = new();
    private readonly HashSet<(int ImageIdentity, long ItemIdentity)> _activeRegistrations = new();
    private readonly Dictionary<long, int> _activeItemCounts = new();
    private readonly HashSet<(ThumbnailDecodedOwnerKind Kind, long Identity)> _decodedStrongReferenceOwners = new();
    private int _maxRegistrationCount;
    private ThumbnailFallbackInitializationRecord? _fallbackInitialization;
    private bool _active;
    private long _phaseOrigin;
    private ThumbnailMeasurementPhase _phase;

    private ThumbnailPerformanceProbe(bool enabled)
    {
        _enabled = enabled;
    }

    public static ThumbnailPerformanceProbe Disabled { get; } = new(false);

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _enabled && _active;
            }
        }
    }

    public static ThumbnailPerformanceProbe CreateEnabled() => new(true);

    public void BeginMeasurementPhase(ThumbnailMeasurementPhase phase) =>
        BeginMeasurementPhase(phase, new ThumbnailPhaseBoundaryState(0, 0, 0, 0, true));

    public void BeginMeasurementPhase(
        ThumbnailMeasurementPhase phase,
        ThumbnailPhaseBoundaryState boundary)
    {
        if (!_enabled)
        {
            return;
        }

        lock (_sync)
        {
            if (_active)
            {
                throw new InvalidOperationException("A thumbnail measurement phase is already active.");
            }
            if (boundary.ActiveDecodeCount != 0 ||
                boundary.ViewModelInFlightCount != 0 ||
                boundary.RegistrationCount != 0 ||
                boundary.PreviousRuntimeStateCount != 0 ||
                !boundary.DispatcherDrained)
            {
                throw new InvalidOperationException("A thumbnail measurement phase requires a quiescent phase boundary.");
            }
            if (_activeRegistrations.Count != 0 || _activeItemCounts.Count != 0)
            {
                throw new InvalidOperationException("A thumbnail measurement phase cannot begin with active image registrations.");
            }
            if (_decodedStrongReferenceOwners.Any(owner => owner.Kind != ThumbnailDecodedOwnerKind.LoaderCache))
            {
                throw new InvalidOperationException("A thumbnail measurement phase cannot begin with retained item/runtime owners.");
            }

            _phase = phase;
            _markers.Clear();
            _requestRecords.Clear();
            _registrationRecords.Clear();
            _dispatcherBatchRecords.Clear();
            _resourceCheckpoints.Clear();
            _maxRegistrationCount = 0;
            _fallbackInitialization = null;
            _phaseOrigin = Stopwatch.GetTimestamp();
            _active = true;
            RecordResourceCheckpointUnsafe(ThumbnailResourceCheckpointKind.PhaseStart);
        }
    }

    public bool TryMark(StartupTimingMarker marker)
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_sync)
        {
            if (!_active || _markers.ContainsKey(marker))
            {
                return false;
            }

            var elapsedTicks = marker == StartupTimingMarker.StartupMeasurementBegin
                ? 0
                : Math.Max(0, Stopwatch.GetTimestamp() - _phaseOrigin);
            _markers.Add(marker, new ThumbnailTimingPoint(
                elapsedTicks,
                elapsedTicks * 1000d / Stopwatch.Frequency));
            if (TryMapCheckpoint(marker, out var checkpointKind))
            {
                RecordResourceCheckpointUnsafe(checkpointKind);
            }
            return true;
        }
    }

    public bool HasMarker(StartupTimingMarker marker)
    {
        if (!_enabled)
        {
            return false;
        }

        lock (_sync)
        {
            return _active && _markers.ContainsKey(marker);
        }
    }

    public IReadOnlyList<StartupTimingMarker> GetRecordedMarkers()
    {
        lock (_sync)
        {
            return _markers.Keys.OrderBy(marker => marker).ToArray();
        }
    }

    public void EnterImageRegistration(int imageIdentity, long itemIdentity)
    {
        lock (_sync)
        {
            if (!_enabled || !_activeRegistrations.Add((imageIdentity, itemIdentity)))
            {
                return;
            }

            if (_active)
            {
                _registrationRecords.Add(new ThumbnailImageRegistrationRecord(imageIdentity, itemIdentity, true));
            }
            _activeItemCounts.TryGetValue(itemIdentity, out var count);
            _activeItemCounts[itemIdentity] = count + 1;
            if (_active)
            {
                _maxRegistrationCount = Math.Max(_maxRegistrationCount, _activeRegistrations.Count);
            }
        }
    }

    public void LeaveImageRegistration(int imageIdentity, long itemIdentity)
    {
        lock (_sync)
        {
            if (!_enabled || !_activeRegistrations.Remove((imageIdentity, itemIdentity)))
            {
                return;
            }

            if (_active)
            {
                _registrationRecords.Add(new ThumbnailImageRegistrationRecord(imageIdentity, itemIdentity, false));
            }
            var count = _activeItemCounts[itemIdentity] - 1;
            if (count == 0)
            {
                _activeItemCounts.Remove(itemIdentity);
            }
            else
            {
                _activeItemCounts[itemIdentity] = count;
            }
        }
    }

    public void RecordThumbnailRequest(
        long itemIdentity,
        long generation,
        string normalizedPath,
        int decodePixelWidth)
    {
        lock (_sync)
        {
            if (!_enabled || !_active)
            {
                return;
            }

            _requestRecords.Add(new ThumbnailRequestProbeRecord(
                itemIdentity,
                generation,
                normalizedPath,
                decodePixelWidth,
                _activeItemCounts.TryGetValue(itemIdentity, out var count) && count > 0));
        }
    }

    public IReadOnlyList<ThumbnailRequestProbeRecord> GetRequestRecords()
    {
        lock (_sync)
        {
            return _requestRecords.ToArray();
        }
    }

    public void RecordDispatcherBatch(string kind, int itemCount, long elapsedTicks)
    {
        lock (_sync)
        {
            if (!_enabled || !_active)
            {
                return;
            }

            _dispatcherBatchRecords.Add(new StartupDispatcherBatchRecord(
                kind,
                itemCount,
                elapsedTicks,
                Environment.CurrentManagedThreadId));
        }
    }

    public IReadOnlyList<StartupDispatcherBatchRecord> GetDispatcherBatchRecords()
    {
        lock (_sync)
        {
            return _dispatcherBatchRecords.ToArray();
        }
    }

    public int GetActiveRegistrationCount()
    {
        lock (_sync)
        {
            return _activeRegistrations.Count;
        }
    }

    public void EnterDecodedStrongReferenceOwner(ThumbnailDecodedOwnerKind kind, long identity)
    {
        lock (_sync)
        {
            if (_enabled)
            {
                _decodedStrongReferenceOwners.Add((kind, identity));
            }
        }
    }

    public void LeaveDecodedStrongReferenceOwner(ThumbnailDecodedOwnerKind kind, long identity)
    {
        lock (_sync)
        {
            if (_enabled)
            {
                _decodedStrongReferenceOwners.Remove((kind, identity));
            }
        }
    }

    public int GetDecodedStrongReferenceOwnerCount()
    {
        lock (_sync)
        {
            return _decodedStrongReferenceOwners.Count;
        }
    }

    public void RecordFallbackInitialization(long elapsedTicks, int threadId)
    {
        lock (_sync)
        {
            if (!_enabled || !_active || _fallbackInitialization is not null)
            {
                return;
            }

            _fallbackInitialization = new ThumbnailFallbackInitializationRecord(
                elapsedTicks,
                elapsedTicks * 1000d / Stopwatch.Frequency,
                threadId);
        }
    }

    public void RecordFirstSteadyCheckpoint()
    {
        lock (_sync)
        {
            if (_enabled && _active && _resourceCheckpoints.All(
                    checkpoint => checkpoint.Kind != ThumbnailResourceCheckpointKind.FirstSteady))
            {
                RecordResourceCheckpointUnsafe(ThumbnailResourceCheckpointKind.FirstSteady);
            }
        }
    }

    public ThumbnailPerformanceSnapshot EndMeasurementPhase()
    {
        return EndMeasurementPhaseCore(validateSeal: false, default);
    }

    public ThumbnailPerformanceSnapshot EndMeasurementPhase(ThumbnailPhaseSealState seal)
    {
        return EndMeasurementPhaseCore(validateSeal: true, seal);
    }

    private ThumbnailPerformanceSnapshot EndMeasurementPhaseCore(
        bool validateSeal,
        ThumbnailPhaseSealState seal)
    {
        if (!_enabled)
        {
            throw new InvalidOperationException("The disabled performance probe cannot end a measurement phase.");
        }

        lock (_sync)
        {
            if (!_active)
            {
                throw new InvalidOperationException("No thumbnail measurement phase is active.");
            }

            if (validateSeal &&
                (!_markers.ContainsKey(StartupTimingMarker.StartupTerminal) ||
                 seal.ActiveDecodeCount != 0 ||
                 seal.ViewModelInFlightCount != 0 ||
                 !seal.StableFor500Milliseconds ||
                 !seal.AllActiveRegistrationsTerminal))
            {
                throw new InvalidOperationException("A thumbnail measurement phase cannot be sealed before startup and thumbnail work are stable.");
            }

            RecordResourceCheckpointUnsafe(ThumbnailResourceCheckpointKind.PhaseEnd);
            var maximum = new ThumbnailCheckpointMaximum(
                _resourceCheckpoints.Max(checkpoint => checkpoint.WorkingSetBytes),
                _resourceCheckpoints.Max(checkpoint => checkpoint.ManagedHeapBytes),
                _resourceCheckpoints.Max(checkpoint => checkpoint.RegistrationCount),
                _resourceCheckpoints.Max(checkpoint => checkpoint.DecodedStrongReferenceCount),
                _resourceCheckpoints.Max(checkpoint => checkpoint.OwnerSlotBound));
            var firstCheckpoint = _resourceCheckpoints[0];
            var lastCheckpoint = _resourceCheckpoints[^1];
            var gcDelta = new ThumbnailGcPhaseDelta(
                Math.Max(0, lastCheckpoint.Gc0CollectionCount - firstCheckpoint.Gc0CollectionCount),
                Math.Max(0, lastCheckpoint.Gc1CollectionCount - firstCheckpoint.Gc1CollectionCount),
                Math.Max(0, lastCheckpoint.Gc2CollectionCount - firstCheckpoint.Gc2CollectionCount));

            var snapshot = new ThumbnailPerformanceSnapshot(
                SchemaVersion,
                _phase,
                new ReadOnlyDictionary<StartupTimingMarker, ThumbnailTimingPoint>(
                    new Dictionary<StartupTimingMarker, ThumbnailTimingPoint>(_markers)),
                Array.AsReadOnly(_requestRecords.ToArray()),
                Array.AsReadOnly(_registrationRecords.ToArray()),
                Array.AsReadOnly(_dispatcherBatchRecords.ToArray()),
                Array.AsReadOnly(_resourceCheckpoints.ToArray()),
                maximum,
                gcDelta,
                _fallbackInitialization,
                _activeRegistrations.Count,
                _maxRegistrationCount);
            _active = false;
            return snapshot;
        }
    }

    private void RecordResourceCheckpointUnsafe(ThumbnailResourceCheckpointKind kind)
    {
        using var process = Process.GetCurrentProcess();
        var loaderOwners = _decodedStrongReferenceOwners.Count(
            owner => owner.Kind == ThumbnailDecodedOwnerKind.LoaderCache);
        var itemOwners = _decodedStrongReferenceOwners.Count(
            owner => owner.Kind == ThumbnailDecodedOwnerKind.RealizedItemSource);
        _resourceCheckpoints.Add(new ThumbnailResourceCheckpoint(
            kind,
            Math.Max(0, Stopwatch.GetTimestamp() - _phaseOrigin),
            process.WorkingSet64,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            loaderOwners,
            itemOwners,
            _activeRegistrations.Count,
            _decodedStrongReferenceOwners.Count,
            loaderOwners + itemOwners));
    }

    private static bool TryMapCheckpoint(
        StartupTimingMarker marker,
        out ThumbnailResourceCheckpointKind checkpointKind)
    {
        checkpointKind = marker switch
        {
            StartupTimingMarker.VisualFirstMeaningfulCard => ThumbnailResourceCheckpointKind.VisualFirstMeaningfulCard,
            StartupTimingMarker.VisualFirstThumbnail => ThumbnailResourceCheckpointKind.VisualFirstThumbnail,
            StartupTimingMarker.StartupTerminal => ThumbnailResourceCheckpointKind.StartupTerminal,
            _ => default
        };
        return marker is StartupTimingMarker.VisualFirstMeaningfulCard or
            StartupTimingMarker.VisualFirstThumbnail or
            StartupTimingMarker.StartupTerminal;
    }
}
