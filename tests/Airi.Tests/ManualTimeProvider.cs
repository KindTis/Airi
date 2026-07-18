using System.Collections.Generic;

namespace Airi.Tests;

internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _sync = new();
    private readonly PriorityQueue<ScheduledTimer, (long Due, long Order)> _timers = new();
    private readonly Dictionary<long, TaskCompletionSource> _timerScheduled = new();
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;
    private long _timestamp;
    private long _order;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public override long GetTimestamp() => _timestamp;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        if (period != Timeout.InfiniteTimeSpan)
        {
            throw new NotSupportedException("Periodic timers are not used by tooltip preview tests.");
        }
        var timer = new ScheduledTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Schedule(TimeSpan delay, Action callback) =>
        CreateTimer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);

    public Task WaitForTimerDueAtAsync(TimeSpan timestamp)
    {
        lock (_sync)
        {
            if (_timers.UnorderedItems.Any(item => item.Priority.Due == timestamp.Ticks))
            {
                return Task.CompletedTask;
            }
            if (!_timerScheduled.TryGetValue(timestamp.Ticks, out var scheduled))
            {
                scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _timerScheduled.Add(timestamp.Ticks, scheduled);
            }
            return scheduled.Task;
        }
    }

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delta));
        var target = checked(_timestamp + delta.Ticks);
        while (true)
        {
            ScheduledTimer? timer;
            lock (_sync)
            {
                if (!_timers.TryPeek(out timer, out var priority) || priority.Due > target)
                {
                    SetTime(target);
                    return;
                }
                _timers.Dequeue();
                if (!timer.TryTake(priority.Due))
                {
                    continue;
                }
                SetTime(priority.Due);
            }
            timer.Invoke();
        }
    }

    private void SetTime(long timestamp)
    {
        var delta = timestamp - _timestamp;
        _timestamp = timestamp;
        _utcNow = _utcNow.AddTicks(delta);
    }

    private void Schedule(ScheduledTimer timer, long due)
    {
        lock (_sync)
        {
            _timers.Enqueue(timer, (due, _order++));
            if (_timerScheduled.Remove(due, out var scheduled))
            {
                scheduled.TrySetResult();
            }
        }
    }

    private sealed class ScheduledTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private long _due = long.MaxValue;
        private bool _disposed;

        public ScheduledTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan) throw new NotSupportedException();
            if (_disposed) return false;
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                _due = long.MaxValue;
                return true;
            }
            if (dueTime < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(dueTime));
            _due = checked(_owner.GetTimestamp() + dueTime.Ticks);
            _owner.Schedule(this, _due);
            return true;
        }

        public bool TryTake(long due)
        {
            if (_disposed || _due != due) return false;
            _due = long.MaxValue;
            return true;
        }

        public void Invoke() => _callback(_state);
        public void Dispose() => _disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
