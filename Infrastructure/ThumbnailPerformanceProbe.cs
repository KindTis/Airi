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

internal readonly record struct ThumbnailTimingPoint(
    long ElapsedTicks,
    double ElapsedMilliseconds);

internal sealed record ThumbnailPerformanceSnapshot(
    int SchemaVersion,
    ThumbnailMeasurementPhase Phase,
    IReadOnlyDictionary<StartupTimingMarker, ThumbnailTimingPoint> Markers);

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
    public const int SchemaVersion = 1;

    private readonly object _sync = new();
    private readonly bool _enabled;
    private readonly Dictionary<StartupTimingMarker, ThumbnailTimingPoint> _markers = new();
    private readonly List<ThumbnailRequestProbeRecord> _requestRecords = new();
    private readonly List<ThumbnailImageRegistrationRecord> _registrationRecords = new();
    private readonly List<StartupDispatcherBatchRecord> _dispatcherBatchRecords = new();
    private readonly HashSet<(int ImageIdentity, long ItemIdentity)> _activeRegistrations = new();
    private readonly Dictionary<long, int> _activeItemCounts = new();
    private int _maxRegistrationCount;
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

    public void BeginMeasurementPhase(ThumbnailMeasurementPhase phase)
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

            _phase = phase;
            _markers.Clear();
            _requestRecords.Clear();
            _registrationRecords.Clear();
            _dispatcherBatchRecords.Clear();
            _activeRegistrations.Clear();
            _activeItemCounts.Clear();
            _maxRegistrationCount = 0;
            _phaseOrigin = Stopwatch.GetTimestamp();
            _active = true;
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
            if (!_enabled || !_active || !_activeRegistrations.Add((imageIdentity, itemIdentity)))
            {
                return;
            }

            _registrationRecords.Add(new ThumbnailImageRegistrationRecord(imageIdentity, itemIdentity, true));
            _activeItemCounts.TryGetValue(itemIdentity, out var count);
            _activeItemCounts[itemIdentity] = count + 1;
            _maxRegistrationCount = Math.Max(_maxRegistrationCount, _activeRegistrations.Count);
        }
    }

    public void LeaveImageRegistration(int imageIdentity, long itemIdentity)
    {
        lock (_sync)
        {
            if (!_enabled || !_active || !_activeRegistrations.Remove((imageIdentity, itemIdentity)))
            {
                return;
            }

            _registrationRecords.Add(new ThumbnailImageRegistrationRecord(imageIdentity, itemIdentity, false));
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

    public ThumbnailPerformanceSnapshot EndMeasurementPhase()
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

            var snapshot = new ThumbnailPerformanceSnapshot(
                SchemaVersion,
                _phase,
                new ReadOnlyDictionary<StartupTimingMarker, ThumbnailTimingPoint>(
                    new Dictionary<StartupTimingMarker, ThumbnailTimingPoint>(_markers)));
            _active = false;
            return snapshot;
        }
    }
}
