using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace Airi.Infrastructure;

public enum GalleryPopupState { Closed, HoverPending, Detail, ClosePending, Editing, Preview, Saving }
/// <summary>Owns popup timing and anchor generations, never application selection or edits.</summary>
public sealed class GalleryPopupCoordinator : IDisposable
{
    private readonly Dispatcher _dispatcher; private readonly TimeProvider _clock; private readonly Action<VideoItem, ListBoxItem, bool> _open; private readonly Action _close; private ITimer? _openTimer, _closeTimer; private long _generation; private bool _panelInside; private bool _disposed; private Guid? _suppressFocus;
    public GalleryPopupState State { get; private set; }
    public VideoItem? Item { get; private set; }
    public ListBoxItem? Anchor { get; private set; }
    public bool KeyboardOpened { get; private set; }
    public bool IsPinned => State is GalleryPopupState.Editing or GalleryPopupState.Preview or GalleryPopupState.Saving;
    public GalleryPopupCoordinator(Dispatcher dispatcher, Action<VideoItem, ListBoxItem, bool> open, Action close, TimeProvider? clock = null) { _dispatcher = dispatcher; _open = open; _close = close; _clock = clock ?? TimeProvider.System; }
    public void Hover(VideoItem item, ListBoxItem anchor)
    {
        if (_disposed || IsPinned) return; _suppressFocus = null; CancelClose(); if (ReferenceEquals(Item, item) && State == GalleryPopupState.Detail) return;
        var generation = ++_generation; _openTimer?.Dispose(); State = GalleryPopupState.HoverPending;
        _openTimer = _clock.CreateTimer(_ => _dispatcher.BeginInvoke(new Action(() => { if (_disposed || generation != _generation || IsPinned || !ReferenceEquals(anchor.DataContext, item) || !anchor.IsLoaded) return; Open(item, anchor, false); })), null, TimeSpan.FromMilliseconds(300), Timeout.InfiniteTimeSpan);
    }
    public void Focus(VideoItem item, ListBoxItem anchor) { if (_disposed || IsPinned) return; if (_suppressFocus == item.Id) { _suppressFocus = null; return; } Open(item, anchor, true); }
    public void Click(VideoItem item, ListBoxItem anchor) { if (_disposed || IsPinned) return; if (ReferenceEquals(Item, item) && State == GalleryPopupState.Detail && !KeyboardOpened) { Close(); return; } Open(item, anchor, false); }
    private void Open(VideoItem item, ListBoxItem anchor, bool keyboard) { ++_generation; _openTimer?.Dispose(); CancelClose(); Item = item; Anchor = anchor; KeyboardOpened = keyboard; State = GalleryPopupState.Detail; _open(item, anchor, keyboard); }
    public void LeaveCard() { if (IsPinned) return; _openTimer?.Dispose(); ++_generation; if (Item is null) { State = GalleryPopupState.Closed; return; } ScheduleClose(); }
    public void EnterPanel() { _panelInside = true; _openTimer?.Dispose(); ++_generation; CancelClose(); if (Item is not null && State is GalleryPopupState.ClosePending or GalleryPopupState.HoverPending) State = GalleryPopupState.Detail; }
    public void LeavePanel() { _panelInside = false; if (!IsPinned) ScheduleClose(); }
    private void ScheduleClose() { if (_panelInside || IsPinned || KeyboardOpened) return; State = GalleryPopupState.ClosePending; _closeTimer?.Dispose(); var generation = _generation; _closeTimer = _clock.CreateTimer(_ => _dispatcher.BeginInvoke(new Action(() => { if (!_disposed && generation == _generation && !_panelInside && Anchor?.IsMouseOver != true && !IsPinned) Close(); })), null, TimeSpan.FromMilliseconds(180), Timeout.InfiniteTimeSpan); }
    private void CancelClose() { _closeTimer?.Dispose(); _closeTimer = null; }
    public void SetEditing(bool editing, bool preview = false, bool saving = false) { if (editing) { ++_generation; _openTimer?.Dispose(); CancelClose(); State = saving ? GalleryPopupState.Saving : preview ? GalleryPopupState.Preview : GalleryPopupState.Editing; } else if (IsPinned) State = Item is null ? GalleryPopupState.Closed : GalleryPopupState.Detail; }
    public void SuppressNextFocus(Guid id) => _suppressFocus = id;
    public void ClearFocusSuppression() => _suppressFocus = null;
    public void QueryChanged() { if (!IsPinned) { _suppressFocus = null; Close(); } }
    public void Scroll() { if (IsPinned) return; if (!KeyboardOpened) Close(); else if (Anchor is not { IsLoaded: true } || !ReferenceEquals(Anchor.DataContext, Item)) Close(); }
    public void Recycled(ListBoxItem anchor) { if (ReferenceEquals(Anchor, anchor) && !IsPinned) Close(); }
    public void Close(bool restoreFocus = false) { if (IsPinned) return; ++_generation; _openTimer?.Dispose(); CancelClose(); if (restoreFocus) _suppressFocus = Item?.Id; State = GalleryPopupState.Closed; Item = null; Anchor = null; KeyboardOpened = false; _panelInside = false; _close(); }
    public void Dispose() { if (_disposed) return; _disposed = true; _openTimer?.Dispose(); _closeTimer?.Dispose(); State = GalleryPopupState.Closed; Item = null; Anchor = null; _close(); }
}
