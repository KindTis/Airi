using System.Windows;
using System.Windows.Controls;
using Airi.Infrastructure;
namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class GalleryPopupCoordinatorTests
{
    [Fact]
    public void HoverAndGrace_AreExactly300And180Milliseconds() => WpfTestHost.Run(() =>
    {
        var clock = new Clock(); var item = new VideoItem(); var anchor = new ListBoxItem { DataContext = item }; var window = new Window { Content = anchor, Width = 300, Height = 200 }; WpfTestHost.Show(window); window.UpdateLayout();
        var opens = 0; var closes = 0; using var coordinator = new GalleryPopupCoordinator(window.Dispatcher, (_, _, _) => opens++, () => closes++, clock);
        coordinator.Hover(item, anchor); clock.Advance(299); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.Equal(0, opens);
        clock.Advance(1); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.Equal(1, opens);
        coordinator.LeaveCard(); clock.Advance(179); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.Equal(0, closes);
        coordinator.EnterPanel(); clock.Advance(1); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.Equal(0, closes);
        coordinator.LeavePanel(); clock.Advance(180); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.Equal(1, closes); window.Close();
    });
    [Fact]
    public void RecycledPendingAnchor_CannotOpenStaleVideo() => WpfTestHost.Run(() =>
    {
        var clock = new Clock(); var item = new VideoItem(); var anchor = new ListBoxItem { DataContext = item }; var window = new Window { Content = anchor, Width = 300, Height = 200 }; WpfTestHost.Show(window); var opens = 0; using var coordinator = new GalleryPopupCoordinator(window.Dispatcher, (_, _, _) => opens++, () => { }, clock);
        coordinator.Hover(item, anchor); anchor.DataContext = new VideoItem(); clock.Advance(300); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.Equal(0, opens); window.Close();
    });
    [Fact]
    public void Editing_PinsTargetAcrossHoverScrollAndRecycling() => WpfTestHost.Run(() =>
    {
        var item = new VideoItem(); var anchor = new ListBoxItem { DataContext = item }; var window = new Window { Content = anchor, Width = 300, Height = 200 }; WpfTestHost.Show(window); using var coordinator = new GalleryPopupCoordinator(window.Dispatcher, (_, _, _) => { }, () => { });
        coordinator.Focus(item, anchor); coordinator.SetEditing(true); coordinator.Hover(new VideoItem(), anchor); coordinator.Scroll(); coordinator.Recycled(anchor); Assert.Same(item, coordinator.Item); Assert.Equal(GalleryPopupState.Editing, coordinator.State);
        coordinator.SetEditing(false); coordinator.Close(true); coordinator.Focus(item, anchor); Assert.Equal(GalleryPopupState.Closed, coordinator.State); window.Close();
    });
    [Fact]
    public void QueryChanged_ClosesReadingAndCancelsPendingAnchor() => WpfTestHost.Run(() =>
    {
        var clock = new Clock(); var item = new VideoItem(); var anchor = new ListBoxItem { DataContext = item };
        var window = new Window { Content = anchor, Width = 300, Height = 200 }; WpfTestHost.Show(window);
        var opens = 0; using var coordinator = new GalleryPopupCoordinator(window.Dispatcher, (_, _, _) => opens++, () => { }, clock);
        coordinator.Focus(item, anchor); Assert.Equal(1, opens); coordinator.QueryChanged(); Assert.Equal(GalleryPopupState.Closed, coordinator.State);
        coordinator.Hover(item, anchor); coordinator.QueryChanged(); clock.Advance(1000); WpfTestHost.DrainDispatcher(window.Dispatcher);
        Assert.Equal(1, opens); Assert.Equal(GalleryPopupState.Closed, coordinator.State); window.Close();
    });
    [Fact]
    public void MovingToNewCard_ImmediatelyHidesPreviousInformationWithoutShowingWindow() => WpfTestHost.Run(() =>
    {
        var clock = new Clock();
        var first = new VideoItem();
        var second = new VideoItem();
        var opens = 0;
        var closes = 0;
        using var coordinator = new GalleryPopupCoordinator(System.Windows.Threading.Dispatcher.CurrentDispatcher, (_, _, _) => opens++, () => closes++, clock);
        coordinator.Focus(first, new ListBoxItem { DataContext = first });
        coordinator.Hover(second, new ListBoxItem { DataContext = second });
        Assert.Equal(1, closes);
        Assert.Null(coordinator.Item);
        Assert.Equal(GalleryPopupState.HoverPending, coordinator.State);
        clock.Advance(299);
        WpfTestHost.DrainDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Assert.Equal(1, opens);
    });

    [Fact]
    public void SameCardHover_KeepsReadingAndClosesAfter180msWithoutShowingWindow() => WpfTestHost.Run(() =>
    {
        var clock = new Clock();
        var item = new VideoItem();
        var anchor = new ListBoxItem { DataContext = item };
        var opens = 0;
        var closes = 0;
        using var coordinator = new GalleryPopupCoordinator(System.Windows.Threading.Dispatcher.CurrentDispatcher, (_, _, _) => opens++, () => closes++, clock);
        coordinator.Focus(item, anchor);
        coordinator.Hover(item, anchor);
        Assert.Equal(1, opens);
        Assert.False(coordinator.KeyboardOpened);
        coordinator.LeaveCard();
        clock.Advance(179);
        WpfTestHost.DrainDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Assert.Equal(0, closes);
        clock.Advance(1);
        WpfTestHost.DrainDispatcher(System.Windows.Threading.Dispatcher.CurrentDispatcher);
        Assert.Equal(1, closes);
        Assert.Equal(GalleryPopupState.Closed, coordinator.State);
    });

    private sealed class Clock : TimeProvider
    {
        private long _now; private readonly List<Timer> _timers = [];
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) { var timer = new Timer(this, callback, state, dueTime); _timers.Add(timer); return timer; }
        public void Advance(long ms) { _now += ms; foreach (var timer in _timers.ToArray()) if (!timer.Disposed && timer.Due <= _now) { timer.Disposed = true; timer.Callback(timer.State); } }
        private sealed class Timer(Clock clock, TimerCallback callback, object? state, TimeSpan due) : ITimer
        {
            internal TimerCallback Callback => callback; internal object? State => state; internal long Due { get; private set; } = clock._now + (long)due.TotalMilliseconds; internal bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { Due = clock._now + (long)dueTime.TotalMilliseconds; return !Disposed; }
            public void Dispose() => Disposed = true; public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
