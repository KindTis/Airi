using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Airi.Controls;

/// <summary>Finite viewport adapter for the gallery. Only the compact page owns outer scrolling;
/// the existing recycling wrap panel continues to own realization and thumbnail lifetimes.</summary>
public sealed class GalleryPageScrollHost : Panel, IScrollInfo
{
    public static readonly DependencyProperty HeaderProperty = Part(nameof(Header));
    public static readonly DependencyProperty PrefixProperty = Part(nameof(Prefix));
    public static readonly DependencyProperty SidebarProperty = Part(nameof(Sidebar));
    public static readonly DependencyProperty ListProperty = Part(nameof(List));
    public static readonly DependencyProperty SuffixProperty = Part(nameof(Suffix));
    private static DependencyProperty Part(string name) => DependencyProperty.Register(name, typeof(FrameworkElement), typeof(GalleryPageScrollHost), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, e) => { var h = (GalleryPageScrollHost)d; if (e.OldValue is UIElement old) h.Children.Remove(old); if (e.NewValue is UIElement next) h.Children.Add(next); }));
    public FrameworkElement? Header { get => (FrameworkElement?)GetValue(HeaderProperty); set => SetValue(HeaderProperty, value); }
    public FrameworkElement? Prefix { get => (FrameworkElement?)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    public FrameworkElement? Sidebar { get => (FrameworkElement?)GetValue(SidebarProperty); set => SetValue(SidebarProperty, value); }
    public FrameworkElement? List { get => (FrameworkElement?)GetValue(ListProperty); set => SetValue(ListProperty, value); }
    public FrameworkElement? Suffix { get => (FrameworkElement?)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }
    public static readonly DependencyProperty MetricsProperty = DependencyProperty.Register(nameof(Metrics), typeof(GalleryLayoutMetrics), typeof(GalleryPageScrollHost), new PropertyMetadata(GalleryLayoutMetrics.Calculate(1280, 720, 1108, false)));
    public GalleryLayoutMetrics Metrics { get => (GalleryLayoutMetrics)GetValue(MetricsProperty); private set => SetValue(MetricsProperty, value); }
    public bool IsCompact => Metrics.Compact;
    public event EventHandler? ViewportChanged;
    private double _extent, _viewport, _offset, _prefixHeight, _gridHeight, _footerHeight, _headerHeight;
    private Size _last;
    private bool _syncQueued;
    private bool _scrollResetRequested;
    private (int Index, double ScreenY)? _resizeAnchor;
    public GalleryPageScrollHost() { ClipToBounds = true; Loaded += (_, _) => { if (FindList(List) is ItemsControl list) ((INotifyPropertyChanged)list.Items).PropertyChanged += ItemsChanged; }; Unloaded += (_, _) => { if (FindList(List) is ItemsControl list) ((INotifyPropertyChanged)list.Items).PropertyChanged -= ItemsChanged; }; }
    private void ItemsChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == "Count") InvalidateMeasure(); }
    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsFinite(available.Width) ? available.Width : ActualWidth;
        var height = double.IsFinite(available.Height) ? available.Height : Math.Max(1, ActualHeight);
        width = Math.Max(1, width); height = Math.Max(1, height);
        // Breakpoints use the client, before the outer scrollbar consumes its ten DIP.
        var clientRoot = Window.GetWindow(this)?.Content as FrameworkElement;
        var client = clientRoot?.ActualWidth ?? ScrollOwner?.ActualWidth ?? width;
        var clientHeight = clientRoot?.ActualHeight ?? height;
        if (client <= 0) client = width;
        var compact = client <= 700;
        var contentWidth = compact ? width : Math.Max(1, width - 170);
        var hasScroll = compact ? _extent > height + .1 : FindScroll(List)?.ComputedVerticalScrollBarVisibility == Visibility.Visible;
        var metrics = GalleryLayoutMetrics.Calculate(client, clientHeight, contentWidth, hasScroll && !compact);
        if (Metrics != metrics)
        {
            if (!_scrollResetRequested && _resizeAnchor is null && _last.Height > 0 && FindList(List) is { } anchorList) _resizeAnchor = CaptureAnchor(anchorList);
            Metrics = metrics;
        }
        _headerHeight = metrics.HeaderHeight;
        Header?.Measure(new Size(width, _headerHeight));
        Prefix?.Measure(new Size(contentWidth, double.PositiveInfinity));
        var prefixHeight = Prefix?.DesiredSize.Height ?? 0;
        if (!_scrollResetRequested && _resizeAnchor is null && _last.Height > 0 && Math.Abs(prefixHeight - _prefixHeight) > .1 && FindList(List) is { } prefixList) _resizeAnchor = CaptureAnchor(prefixList);
        _prefixHeight = prefixHeight;
        Suffix?.Measure(new Size(width, double.PositiveInfinity));
        _footerHeight = Suffix?.DesiredSize.Height ?? 31;
        Sidebar?.Measure(new Size(170, Math.Max(0, height - _headerHeight - _footerHeight)));
        if (FindList(List) is ListBox list)
        {
            var p = metrics.GridPadding;
            list.Padding = new Thickness(p.Left, p.Top, Math.Max(0, p.Right - metrics.ColumnGap), Math.Max(0, p.Bottom - metrics.RowGap));
            ScrollViewer.SetVerticalScrollBarVisibility(list, compact ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);
            ScrollViewer.SetPanningMode(list, compact ? PanningMode.None : PanningMode.VerticalOnly);
            _gridHeight = p.Top + p.Bottom + Math.Ceiling(list.Items.Count / (double)metrics.Columns) * metrics.RowStride - metrics.RowGap;
            _gridHeight = Math.Max(compact ? 120 : 0, _gridHeight);
            _extent = compact ? _headerHeight + _prefixHeight + _gridHeight + _footerHeight : height;
            _viewport = height;
            if (compact && _resizeAnchor is { } resize) _offset = _headerHeight + _prefixHeight + resize.Index / metrics.Columns * metrics.RowStride - resize.ScreenY;
            _offset = Math.Clamp(_offset, 0, Math.Max(0, _extent - height));
            var gridTop = _headerHeight + _prefixHeight;
            var listTop = compact ? Math.Max(0, gridTop - _offset) : gridTop;
            var listHeight = compact ? Math.Max(1, Math.Min(height - listTop, gridTop + _gridHeight - _offset)) : Math.Max(1, height - gridTop - _footerHeight);
            List!.Measure(new Size(contentWidth, listHeight));
        }
        ScrollOwner?.InvalidateScrollInfo();
        if (_last != new Size(width, height)) { _last = new Size(width, height); QueueSync(); }
        return new Size(width, height);
    }
    protected override Size ArrangeOverride(Size size)
    {
        var compact = Metrics.Compact;
        var x = compact ? 0 : 170d;
        Header?.Arrange(new Rect(0, compact ? -_offset : 0, size.Width, _headerHeight));
        Sidebar?.Arrange(new Rect(0, _headerHeight, compact ? 0 : 170, Math.Max(0, size.Height - _headerHeight - _footerHeight)));
        if (Sidebar is not null) Sidebar.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Prefix?.Arrange(new Rect(x, _headerHeight - (compact ? _offset : 0), Math.Max(1, size.Width - x), _prefixHeight));
        var gridTop = _headerHeight + _prefixHeight;
        var top = compact ? Math.Max(0, gridTop - _offset) : gridTop;
        var bottom = compact ? Math.Min(size.Height, gridTop + _gridHeight - _offset) : size.Height - _footerHeight;
        List?.Arrange(new Rect(x, top, Math.Max(1, size.Width - x), Math.Max(1, bottom - top)));
        if (List is not null) List.Visibility = bottom > top ? Visibility.Visible : Visibility.Hidden;
        Suffix?.Arrange(new Rect(0, compact ? gridTop + _gridHeight - _offset : size.Height - _footerHeight, size.Width, _footerHeight));
        QueueSync();
        return size;
    }
    private void QueueSync()
    {
        if (_syncQueued) return; _syncQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { _syncQueued = false; if (FindScroll(List) is { } scroll) { if (IsCompact) scroll.ScrollToVerticalOffset(Math.Max(0, _offset - _headerHeight - _prefixHeight)); else if (_resizeAnchor is { } resize) scroll.ScrollToVerticalOffset(Math.Max(0, resize.Index / Metrics.Columns * Metrics.RowStride - resize.ScreenY + _headerHeight + _prefixHeight)); } _resizeAnchor = null; _scrollResetRequested = false; ViewportChanged?.Invoke(this, EventArgs.Empty); }));
    }
    private (int Index, double ScreenY)? CaptureAnchor(ListBox list)
    {
        var viewportTop = List?.TranslatePoint(new Point(), this).Y ?? 0;
        var viewportBottom = viewportTop + (List?.ActualHeight ?? ActualHeight);
        foreach (var container in Realized(list).OrderBy(item => list.ItemContainerGenerator.IndexFromContainer(item)))
        {
            var p = container.TranslatePoint(new Point(), this);
            if (p.Y + container.ActualHeight <= viewportTop || p.Y >= viewportBottom) continue;
            return (list.ItemContainerGenerator.IndexFromContainer(container), p.Y);
        }
        return null;
    }
    private static IEnumerable<ListBoxItem> Realized(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is ListBoxItem item) yield return item; else foreach (var nested in Realized(child)) yield return nested; }
    }
    private static ListBox? FindList(DependencyObject? root)
    {
        if (root is ListBox list) return list; if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindList(VisualTreeHelper.GetChild(root, i)) is { } found) return found; return null;
    }
    internal static ScrollViewer? FindScroll(DependencyObject? root)
    {
        if (root is null) return null;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var c = VisualTreeHelper.GetChild(root, i); if (c is ScrollViewer s) return s; if (FindScroll(c) is { } found) return found; }
        return null;
    }
    public void ScrollToIndex(int index)
    {
        if (FindList(List) is not ListBox list || index < 0 || index >= list.Items.Count) return;
        _scrollResetRequested = true; _resizeAnchor = null;
        if (IsCompact) SetVerticalOffset(_headerHeight + _prefixHeight + index / Metrics.Columns * Metrics.RowStride);
        else list.ScrollIntoView(list.Items[index]);
    }
    public void ResetOffset() { _scrollResetRequested = true; _resizeAnchor = null; if (IsCompact) SetVerticalOffset(0); else FindScroll(List)?.ScrollToTop(); }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; } = true;
    public double ExtentWidth => _last.Width; public double ExtentHeight => _extent;
    public double ViewportWidth => _last.Width; public double ViewportHeight => _viewport;
    public double HorizontalOffset => 0; public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(_offset - 24); public void LineDown() => SetVerticalOffset(_offset + 24);
    public void MouseWheelUp() => SetVerticalOffset(_offset - 72); public void MouseWheelDown() => SetVerticalOffset(_offset + 72);
    public void PageUp() => SetVerticalOffset(_offset - _viewport); public void PageDown() => SetVerticalOffset(_offset + _viewport);
    public void SetVerticalOffset(double value) { var next = Math.Clamp(double.IsNaN(value) ? 0 : value, 0, Math.Max(0, _extent - _viewport)); if (Math.Abs(next - _offset) < .01) return; _offset = next; InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo(); ViewportChanged?.Invoke(this, EventArgs.Empty); }
    public void LineLeft() { }
    public void LineRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void SetHorizontalOffset(double value) { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (!IsCompact || !IsAncestorOf(visual)) return rectangle;
        if (Header is { } header && (ReferenceEquals(header, visual) || header.IsAncestorOf(visual)))
        {
            SetVerticalOffset(0);
            return rectangle;
        }
        for (DependencyObject? current = visual; current is not null && !ReferenceEquals(current, this); current = VisualTreeHelper.GetParent(current))
            if (current is ListBoxItem item && FindList(List) is { } list)
            {
                var index = list.ItemContainerGenerator.IndexFromContainer(item);
                if (index >= 0) ScrollToIndex(index);
                return rectangle;
            }
        var target = visual.TransformToAncestor(this).TransformBounds(rectangle);
        if (target.Top < 0) SetVerticalOffset(_offset + target.Top);
        else if (target.Bottom > ViewportHeight) SetVerticalOffset(_offset + target.Bottom - ViewportHeight);
        return rectangle;
    }
}

public sealed record GalleryLayoutMetrics(bool Compact, bool Short, int Columns, double HeaderHeight, double CardWidth, double CoverHeight, double CardHeight, double ColumnGap, double RowGap, Thickness GridPadding, double HeadingTop, double HeadingBottom, double SidePadding, double ShelfBottom, double TitleSize, double CardTitleSize, double TitleTop, double MetadataTop)
{
    public double ColumnStride => CardWidth + ColumnGap;
    public double RowStride => CardHeight + RowGap;
    public static GalleryLayoutMetrics Calculate(double clientWidth, double clientHeight, double resultWidth, bool scrollbar)
    {
        var compact = clientWidth <= 700; var shortWindow = !compact && clientHeight <= 850;
        var columns = clientWidth <= 1230 ? 2 : 3;
        var side = compact ? 15 : clientWidth <= 1230 ? 18 : 25;
        var col = clientWidth >= 1700 ? 24 : clientWidth <= 1000 ? 14 : 17;
        var row = clientWidth >= 1700 ? 24 : clientWidth <= 1000 ? 20 : 22;
        var gridTop = clientWidth <= 1000 ? 12 : 14; var gridBottom = clientWidth <= 1000 ? 20 : 23;
        if (shortWindow) { col = 17; row = 17; gridTop = 9; }
        if (compact) { col = 12; row = 21; gridTop = 12; gridBottom = 20; }
        // Grid padding remains 25 between 1001 and 1230; the heading uses 18 there.
        var gridSide = compact ? 15 : clientWidth <= 1000 ? 18 : 25;
        var width = Math.Max(1, (resultWidth - (scrollbar ? 10 : 0) - 2 * gridSide - (columns - 1) * col) / columns);
        var cover = width / (compact ? 1.4 : shortWindow ? 2.15 : 1.9);
        var titleTop = shortWindow ? 6 : 10; var metaTop = shortWindow ? 3 : 5;
        var cardHeight = cover + titleTop + (compact ? 17.4 : 20.3) + 2 * metaTop + (compact ? 76.8 : 38.4);
        return new(compact, shortWindow, columns, compact ? 66 : shortWindow ? 64 : 76, width, cover, cardHeight, col, row, new Thickness(gridSide, gridTop, gridSide, gridBottom), compact ? 21 : shortWindow ? 15 : 21, compact ? 12 : shortWindow ? 11 : 17, side, compact ? 10 : shortWindow ? 8 : 13, compact || shortWindow ? 22 : 24, compact ? 12 : 14, titleTop, metaTop);
    }
}
