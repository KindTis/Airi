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

internal sealed class ThumbnailPerformanceProbe
{
    public const int SchemaVersion = 1;

    private readonly object _sync = new();
    private readonly bool _enabled;
    private readonly Dictionary<StartupTimingMarker, ThumbnailTimingPoint> _markers = new();
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
