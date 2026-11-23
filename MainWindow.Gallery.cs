using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Airi.Controls;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Services.VideoPreview;
using Airi.ViewModels;
using Airi.Views;
using Microsoft.Win32;
namespace Airi;

public partial class MainWindow
{
    internal GalleryPageScrollHost GalleryHostForTests => GalleryHost;
    internal TextBox SearchTextBox => SearchInput;
    internal TooltipPreviewState? GalleryPreviewStateForTests => _galleryPreviewState;
    internal FrameworkElement GalleryPopupRootForTests => DetailSurface;
    internal bool GalleryPopupIsOpenForTests => GalleryDetail.IsOpen;
    internal void OpenGalleryForTests(VideoItem item, ListBoxItem anchor) => _galleryPopup?.Focus(item, anchor);
    internal Task CloseGalleryForTestsAsync() { _galleryPopup?.Close(); return StopGalleryPreviewAsync(); }
    private GalleryPopupCoordinator? _galleryPopup;
    private GalleryLayoutMetrics? _appliedMetrics;
    private readonly IVideoLauncher _videoLauncher = new WindowsVideoLauncher();
    private Guid? _galleryPreviewSession;
    private TooltipPreviewState? _galleryPreviewState;
    private CancellationTokenSource? _galleryCoverCancellation;
    private Task _galleryPreviewTask = Task.CompletedTask;
    private CancellationTokenSource? _galleryThumbnailCancellation;
    private Task _galleryThumbnailTask = Task.CompletedTask;
    private Window? _galleryDialog;
    internal Func<GalleryOperationConfirmation, Task<bool>>? ConfirmGalleryOperationOverride { get; set; }
    private ListBoxItem? _lastDetailAnchor;
    private static readonly DependencyPropertyKey IsGallerySelectionModePropertyKey =
        DependencyProperty.RegisterReadOnly(nameof(IsGallerySelectionMode), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsGallerySelectionModeProperty = IsGallerySelectionModePropertyKey.DependencyProperty;
    public bool IsGallerySelectionMode => (bool)GetValue(IsGallerySelectionModeProperty);
    private VideoItem? _gallerySelectionPressedItem;
    private VideoItem? _gallerySelectionAnchor;
    private double _lastPageOffset;
    private bool _galleryImeComposing;
    private bool _galleryWasEditing;
    private void InitializeGallerySurface()
    {
        _galleryPopup = new GalleryPopupCoordinator(Dispatcher, OpenGalleryDetail, CloseGalleryDetail);
        ViewModel.PropertyChanged += OnGalleryPropertyChanged;
        ViewModel.GalleryScrollToTopRequested += OnGalleryScrollToTop;
        ViewModel.GalleryQueryRequested += CloseGalleryReadingForQuery;
        ViewModel.GalleryCheckedWorkCompleted += OnGalleryCheckedWorkCompleted;
        ViewModel.ConfirmOperationAsync = ConfirmGalleryOperationAsync;
        ViewModel.StopPreviewForOperationAsync = StopGalleryOperationPreviewAsync;
        TextCompositionManager.AddPreviewTextInputStartHandler(this, (_, _) => _galleryImeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(this, (_, _) => _galleryImeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(this, (_, _) => _galleryImeComposing = false);
        TextCompositionManager.AddPreviewTextInputStartHandler(DetailSurface, (_, _) => _galleryImeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(DetailSurface, (_, _) => _galleryImeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(DetailSurface, (_, _) => _galleryImeComposing = false);
        LostKeyboardFocus += (_, _) => _galleryImeComposing = false;
        DetailSurface.LostKeyboardFocus += (_, _) => _galleryImeComposing = false;
        DetailSurface.CloseRequested = CloseGalleryWithFocus;
        GalleryHost.ViewportChanged += OnGalleryViewportChanged;
        VideoList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnGalleryListScroll));
        PreviewMouseDown += OnGalleryBackgroundMouseDown;
        Deactivated += OnGalleryDeactivated;
        LocationChanged += OnGalleryLocationChanged;
        SelectionActionsMenu.PlacementTarget = SelectionActionsButton;
        RefreshGalleryState();
    }
    private void OnGalleryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.SearchQuery) or nameof(MainViewModel.SelectedSortOption) or nameof(MainViewModel.CurrentGalleryView)) { ++_galleryFocusGeneration; CloseGalleryReadingForQuery(); }
        if (e.PropertyName is nameof(MainViewModel.IsEditing) or nameof(MainViewModel.IsChangePreview) or nameof(MainViewModel.IsSaving) or nameof(MainViewModel.EditorDraft)) RefreshGalleryEditor();
        if (e.PropertyName is nameof(MainViewModel.ResultCount) or nameof(MainViewModel.CurrentGalleryView) or nameof(MainViewModel.CheckedCount) or nameof(MainViewModel.VisibleCheckedCount) or nameof(MainViewModel.ShowVideoSkeleton) or nameof(MainViewModel.AllVisibleChecked)) RefreshGalleryState();
        if (e.PropertyName == nameof(MainViewModel.InspectedVideo) && GalleryDetail.IsOpen && !ViewModel.IsEditing && _galleryPopup?.Item is { } item && !ReferenceEquals(item, ViewModel.InspectedVideo)) _galleryPopup.Close();
    }
    private void RefreshGalleryState()
    {
        if (ViewModel is null) return;
        var trash = ViewModel.CurrentGalleryView == GalleryView.Trash;
        SelectionRefreshItem.Visibility = SelectionDeleteItem.Visibility = trash ? Visibility.Collapsed : Visibility.Visible;
        SelectionRestoreItem.Visibility = trash ? Visibility.Visible : Visibility.Collapsed;
        if (SelectionActionsMenu.IsOpen && ViewModel.CheckedCount == 0) SelectionActionsMenu.IsOpen = false;
        HeadingTitle.Text = ViewModel.CurrentGalleryView switch { GalleryView.Incomplete => "정보 미완성", GalleryView.Missing => "파일 없음", GalleryView.Trash => "휴지통", _ => "라이브러리" };
        foreach (var nav in new[] { NavAll, NavIncomplete, NavMissing, NavTrash }) { var active = nav.Tag?.ToString() == ViewModel.CurrentGalleryView.ToString(); nav.Background = active ? (Brush)FindResource("GalleryTint") : Brushes.Transparent; nav.Foreground = (Brush)FindResource(active ? "GalleryAccent" : "GalleryText"); nav.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal; var label = EnumerateVisualDescendants<TextBlock>(nav).FirstOrDefault(); if (label is not null) { label.Foreground = nav.Foreground; label.FontWeight = nav.FontWeight; } foreach (var icon in EnumerateVisualDescendants<GalleryIcon>(nav)) icon.Foreground = (Brush)FindResource(active ? "GalleryAccent" : "GalleryMuted"); }
        EmptyState.Visibility = ViewModel.ResultCount == 0 && !ViewModel.ShowVideoSkeleton ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = trash ? "휴지통이 비어 있습니다" : "영상을 찾을 수 없습니다";
        NoFilters.Visibility = ViewModel.QueryChips.Count == 0 ? Visibility.Visible : Visibility.Collapsed; ClearFilters.Visibility = ViewModel.QueryChips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GalleryHost.InvalidateMeasure();
    }
    private void OnGalleryScrollToTop() { CloseGalleryReadingForQuery(); GalleryHost.ResetOffset(); RefreshGalleryState(); }
    private void OnGalleryViewportChanged(object? sender, EventArgs e)
    {
        var metrics = GalleryHost.Metrics;
        if (_appliedMetrics != metrics) { _appliedMetrics = metrics; ApplyGalleryResponsive(metrics); }
        if (Math.Abs(_lastPageOffset - GalleryHost.VerticalOffset) > .01) { _lastPageOffset = GalleryHost.VerticalOffset; _galleryPopup?.Scroll(); }
        if (GalleryDetail.IsOpen) PositionGalleryPopup();
    }
    private void ApplyGalleryResponsive(GalleryLayoutMetrics m)
    {
        GalleryHeader.Padding = new Thickness(m.Compact ? 14 : 24, 0, m.Compact ? 14 : 24, 0);
        LogoMark.Width = LogoMark.Height = m.Compact ? 25 : 29; Wordmark.FontSize = m.Compact ? 23 : 26; Wordmark.Margin = new Thickness(m.Compact ? 7 : 10, 0, 0, 0);
        HeaderCaption.Visibility = ((Content as FrameworkElement)?.ActualWidth ?? ActualWidth) > 1230 ? Visibility.Visible : Visibility.Collapsed;
        HeaderHistory.Visibility = m.Compact ? Visibility.Collapsed : Visibility.Visible; CompactHistory.Visibility = m.Compact ? Visibility.Visible : Visibility.Collapsed; SearchShortcut.Visibility = m.Compact ? Visibility.Collapsed : Visibility.Visible;
        var clientWidth = (Content as FrameworkElement)?.ActualWidth ?? ActualWidth;
        SearchBorder.Width = m.Compact ? Math.Max(70, clientWidth - 125) : Math.Min(clientWidth * (clientWidth <= 1000 ? .58 : .42), 600);
        SearchBorder.Margin = new Thickness(m.Compact ? 12 : 24, 0, 0, 0); SearchBorder.Padding = new Thickness(m.Compact ? 8 : 12, 0, m.Compact ? 8 : 12, 0);
        SearchInput.FontSize = m.Compact ? 12 : 13; HeaderHistory.Margin = new Thickness(clientWidth <= 1000 ? 12 : 24, 0, 0, 0);
        GalleryHeading.Margin = new Thickness(m.SidePadding, m.HeadingTop, m.SidePadding, m.HeadingBottom); HeadingTitle.FontSize = m.TitleSize; HeadingTitle.LineHeight = m.TitleSize * 1.3;
        FacetShelf.Margin = new Thickness(m.SidePadding, 0, m.SidePadding, m.ShelfBottom);
        var facetWidth = clientWidth <= 1230 ? 105d : 125d;
        var availableShelf = GalleryHost.ActualWidth - (m.Compact ? 0 : 170) - 2 * m.SidePadding;
        const double sortWidth = 170, minimumCompactSortWidth = 135, selectionGap = 16, compactFacetGap = 6;
        var selectionSpace = SelectionModeControl.Width + selectionGap;
        var wrapFacets = m.Compact || availableShelf < 3 * facetWidth + 27 + sortWidth + selectionSpace;
        var separateSortRow = wrapFacets && (availableShelf - compactFacetGap) / 2 < minimumCompactSortWidth + selectionSpace;
        FacetShelf.ColumnDefinitions[0].Width = FacetShelf.ColumnDefinitions[2].Width = wrapFacets ? new GridLength(1, GridUnitType.Star) : new GridLength(facetWidth);
        FacetShelf.ColumnDefinitions[4].Width = wrapFacets ? new GridLength(0) : new GridLength(facetWidth);
        FacetShelf.ColumnDefinitions[6].Width = new GridLength(wrapFacets ? 0 : sortWidth);
        FacetShelf.ColumnDefinitions[1].Width = new GridLength(wrapFacets ? compactFacetGap : 9);
        FacetShelf.ColumnDefinitions[3].Width = FacetShelf.ColumnDefinitions[5].Width = new GridLength(wrapFacets ? 0 : 9);
        FacetShelf.ColumnDefinitions[7].Width = wrapFacets ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        Grid.SetRow(GenrePicker, wrapFacets ? 1 : 0); Grid.SetColumn(GenrePicker, wrapFacets ? 0 : 4);
        var sortRow = separateSortRow ? 2 : wrapFacets ? 1 : 0;
        var controlsTop = wrapFacets ? compactFacetGap : 0;
        Grid.SetRow(SortPicker, sortRow);
        Grid.SetColumn(SortPicker, separateSortRow ? 0 : wrapFacets ? 2 : 6);
        Grid.SetColumnSpan(SortPicker, separateSortRow ? 3 : 1);
        Grid.SetRow(SelectionModeControl, sortRow);
        GenrePicker.Margin = SelectionModeControl.Margin = new Thickness(0, controlsTop, 0, 0);
        SortPicker.Margin = new Thickness(0, controlsTop, wrapFacets ? selectionSpace : 0, 0);
        ActorPicker.IsCompact = StudioPicker.IsCompact = GenrePicker.IsCompact = m.Compact;
        Chips.Margin = new Thickness(m.SidePadding, 0, m.SidePadding, m.Compact ? 10 : m.Short ? 7 : 8);
        MobileFilter.Visibility = m.Compact ? Visibility.Visible : Visibility.Collapsed;
        DetailSurface.Padding = new Thickness(m.Compact ? 18 : 20); Toast.Margin = new Thickness(16, 0, 16, m.Compact ? 20 : 47);
        GalleryHost.InvalidateMeasure();
    }
    private void OnPageMouseWheel(object sender, MouseWheelEventArgs e) { if (!GalleryHost.IsCompact) return; GalleryHost.SetVerticalOffset(GalleryHost.VerticalOffset - e.Delta * .6); e.Handled = true; }
    private void OnGalleryListScroll(object sender, ScrollChangedEventArgs e) { if (e.ViewportWidthChange != 0 || e.ExtentHeightChange != 0) GalleryHost.InvalidateMeasure(); if (e.VerticalChange != 0) { _galleryPopup?.Scroll(); if (GalleryDetail.IsOpen) PositionGalleryPopup(); } }
    private void OnViewClick(object sender, RoutedEventArgs e) { if (sender is Button b) ViewModel.SetViewCommand.Execute(b.Tag); }
    private void OnMobileFilterClick(object sender, RoutedEventArgs e) { var body = new StackPanel(); foreach (var entry in new[] { ("전체 영상", GalleryView.All), ("정보 미완성", GalleryView.Incomplete), ("파일 없음", GalleryView.Missing), ("휴지통", GalleryView.Trash) }) { var button = new Button { Content = entry.Item1, Margin = new Thickness(0, 4, 0, 4) }; button.Click += (_, _) => { ViewModel.SetViewCommand.Execute(entry.Item2); _galleryDialog?.Close(); }; body.Children.Add(button); } ShowGalleryDialog("라이브러리", body, false); }
    private void OnSelectionModeClick(object sender, RoutedEventArgs e) => SetGallerySelectionMode(!IsGallerySelectionMode);

    private void OnSelectionActionsClick(object sender, RoutedEventArgs e)
    {
        if (IsGallerySelectionMode && SelectionActionsButton.IsEnabled)
            SelectionActionsMenu.IsOpen = true;
    }

    private void OnGalleryCheckedWorkCompleted() => SetGallerySelectionMode(false, finishedWork: true);

    private void SetGallerySelectionMode(bool active, bool finishedWork = false)
    {
        if (ViewModel.IsEditing || (!finishedWork && ViewModel.IsSaving) || active == IsGallerySelectionMode) return;

        _gallerySelectionPressedItem = null;
        _gallerySelectionAnchor = null;
        if (!finishedWork) ++_galleryFocusGeneration;
        SetValue(IsGallerySelectionModePropertyKey, active);
        AutomationProperties.SetHelpText(VideoList, active
            ? "카드를 클릭해 선택하고 Shift를 누른 채 클릭해 범위를 추가합니다. 스페이스바로 선택을 전환하고 Esc로 종료합니다."
            : "방향키로 영상을 선택하고 Enter로 상세를 조작합니다.");
        if (active)
        {
            _galleryPopup?.Close();
            return;
        }

        SelectionActionsMenu.IsOpen = false;
        if (!finishedWork) ViewModel.ClearCheckedCommand.Execute(null);
    }

    private void OnGalleryCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsGallerySelectionMode && e.ClickCount == 1 && sender is ListBoxItem { DataContext: VideoItem item })
            _gallerySelectionPressedItem = item;
    }
    private void OnGalleryCardEnter(object sender, MouseEventArgs e) { if (!IsGallerySelectionMode && !ViewModel.IsEditing && sender is ListBoxItem { DataContext: VideoItem item } anchor) _galleryPopup?.Hover(item, anchor); }
    private void OnGalleryCardLeave(object sender, MouseEventArgs e) => _galleryPopup?.LeaveCard();
    private void OnGalleryCardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!IsGallerySelectionMode && sender is ListBoxItem { DataContext: VideoItem item } anchor &&
            e.NewFocus == anchor && Mouse.LeftButton != MouseButtonState.Pressed)
            _galleryPopup?.Focus(item, anchor);
    }

    private void OnGalleryCardClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: VideoItem item } anchor) return;
        if (!IsGallerySelectionMode)
        {
            ViewModel.SelectedVideo = item;
            _galleryPopup?.Click(item, anchor);
            return;
        }

        var pressedItem = _gallerySelectionPressedItem;
        _gallerySelectionPressedItem = null;
        e.Handled = true;
        if (ReferenceEquals(pressedItem, item))
            ToggleGalleryCardSelection(item, (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
    }

    internal void ToggleGalleryCardSelection(VideoItem item, bool extendRange)
    {
        if (!IsGallerySelectionMode || ViewModel.IsEditing || ViewModel.IsSaving) return;
        if (!extendRange)
        {
            item.IsChecked = !item.IsChecked;
            if (item.IsChecked) _gallerySelectionAnchor = item;
            else if (ReferenceEquals(_gallerySelectionAnchor, item)) _gallerySelectionAnchor = null;
            return;
        }

        var endpoint = VideoList.Items.IndexOf(item);
        if (endpoint < 0) return;
        var start = _gallerySelectionAnchor is { IsChecked: true } anchor ? VideoList.Items.IndexOf(anchor) : -1;
        if (start < 0)
        {
            item.IsChecked = true;
            _gallerySelectionAnchor = item;
            return;
        }

        var first = Math.Min(start, endpoint);
        var count = Math.Abs(endpoint - start) + 1;
        ViewModel.CheckGalleryRange(Enumerable.Range(first, count).Select(index => (VideoItem)VideoList.Items[index]));
    }
    private void OnGalleryCardLoaded(object sender, RoutedEventArgs e) { if (sender is ListBoxItem anchor) { anchor.DataContextChanged -= OnGalleryContainerDataContextChanged; anchor.DataContextChanged += OnGalleryContainerDataContextChanged; } }
    private void OnGalleryContainerDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) { if (sender is ListBoxItem anchor) { _galleryPopup?.Recycled(anchor); if (!IsGallerySelectionMode && !ViewModel.IsEditing && anchor.IsMouseOver && e.NewValue is VideoItem item) _galleryPopup?.Hover(item, anchor); } }
    private void OnGalleryCardUnloaded(object sender, RoutedEventArgs e) { if (sender is ListBoxItem anchor) { anchor.DataContextChanged -= OnGalleryContainerDataContextChanged; _galleryPopup?.Recycled(anchor); } }
    private static T? FindAncestor<T>(DependencyObject? obj) where T : DependencyObject { while (obj is not null) { if (obj is T match) return match; obj = obj is Visual ? VisualTreeHelper.GetParent(obj) : LogicalTreeHelper.GetParent(obj); } return null; }
    private void OnDetailEnter(object sender, MouseEventArgs e) => _galleryPopup?.EnterPanel(); private void OnDetailLeave(object sender, MouseEventArgs e) => _galleryPopup?.LeavePanel();
    private void OnGalleryBackgroundMouseDown(object sender, MouseButtonEventArgs e)
    {
        ++_galleryFocusGeneration;
        if (e.ChangedButton == MouseButton.Left) _gallerySelectionPressedItem = null;
        if (ViewModel.IsEditing) return;
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is null) _galleryPopup?.Close();
    }
    private void OnGalleryDeactivated(object? sender, EventArgs e) { if (!ViewModel.IsEditing) _galleryPopup?.Close(); }
    private void OnGalleryLocationChanged(object? sender, EventArgs e) { if (GalleryDetail.IsOpen) PositionGalleryPopup(); }
    private async void OpenGalleryDetail(VideoItem item, ListBoxItem anchor, bool keyboard)
    {
        try
        {
            await StopGalleryPreviewAsync(); if (_galleryPopup?.Item != item || ViewModel.IsEditing || _previewShutdownStarted) return;
            RememberGalleryFocus(item); _lastDetailAnchor = anchor; ViewModel.InspectCommand.Execute(item); DetailSurface.DataContext = ViewModel; DetailReadOnly.Visibility = Visibility.Visible; EditorPanel.Visibility = Visibility.Collapsed;
            GalleryDetail.IsOpen = true; UpdateGalleryDialogAccessibility(); DetailScroll.ScrollToTop(); PositionGalleryPopup();
            var eligible = item.Presence == VideoPresenceState.Available && !item.IsInRecycleBin && File.Exists(item.SourcePath);
            DetailPlay.IsEnabled = eligible; DetailDelete.IsEnabled = eligible; DetailDelete.Visibility = item.IsInRecycleBin ? Visibility.Collapsed : Visibility.Visible; DetailRestore.Visibility = item.IsInRecycleBin ? Visibility.Visible : Visibility.Collapsed; DetailRestore.IsEnabled = item.CanRestore;
            var id = Guid.NewGuid(); _galleryPreviewSession = id; var animations = _clientAreaAnimationsEnabledOverride ?? SystemParameters.ClientAreaAnimation;
            var state = new TooltipPreviewState(id, item.ThumbnailSource, _tooltipPreviewController is not null && animations && eligible); _galleryPreviewState = state;
            GalleryCover.SetBinding(Image.SourceProperty, new Binding(nameof(TooltipPreviewState.CoverSource)) { Source = state }); GalleryPreviewFrame.SetBinding(Image.SourceProperty, new Binding(nameof(TooltipPreviewState.PreviewSource)) { Source = state });
            GalleryPreviewFrame.SetBinding(VisibilityProperty, new Binding(nameof(TooltipPreviewState.IsPreviewVisible)) { Source = state, Converter = (IValueConverter)FindResource("GalleryVisibility") }); GalleryPreviewProgress.SetBinding(VisibilityProperty, new Binding(nameof(TooltipPreviewState.IsDelayIndicatorVisible)) { Source = state, Converter = (IValueConverter)FindResource("GalleryVisibility") });
            GalleryPreviewProgress.IsIndeterminate = false; GalleryPreviewProgress.Value = 0;
            if (state.IsDelayIndicatorVisible)
            {
                var progress = new System.Windows.Media.Animation.DoubleAnimation(0, 100, TimeSpan.FromSeconds(2));
                progress.Completed += (_, _) => { if (_galleryPreviewSession == id && state.IsDelayIndicatorVisible) GalleryPreviewProgress.IsIndeterminate = true; };
                GalleryPreviewProgress.BeginAnimation(RangeBase.ValueProperty, progress);
            }
            _galleryCoverCancellation = new CancellationTokenSource(); var token = _galleryCoverCancellation.Token;
            if (_thumbnailImageLoader is not null) _ = LoadGalleryCoverAsync(item, state, token);
            if (_tooltipPreviewController is not null) { var session = new TooltipPreviewSession(id, new VideoPreviewRequest(item.SourcePath, 350, 148, 15, TimeSpan.FromSeconds(10)), animations, eligible, new GalleryPreviewSink(this, item, state, _tooltipPreviewController.Clock)); _galleryPreviewTask = _tooltipPreviewController.RunAsync(session, token); await _galleryPreviewTask; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppLogger.Error("Gallery preview failed.", ex); }
    }
    private async Task LoadGalleryCoverAsync(VideoItem item, TooltipPreviewState state, CancellationToken token)
    {
        try { var result = await _thumbnailImageLoader!.LoadAsync(item.ThumbnailPath, CalculateTooltipDecodeWidth(VisualTreeHelper.GetDpi(this).DpiScaleX), token); if (!token.IsCancellationRequested && !result.IsFallback && GalleryDetail.IsOpen && ReferenceEquals(ViewModel.InspectedVideo, item) && ReferenceEquals(_galleryPreviewState, state)) state.UpdateCover(result.Source); } catch (OperationCanceledException) { } catch (Exception ex) { AppLogger.Error("Gallery cover failed.", ex); }
    }
    private async Task StopGalleryPreviewAsync()
    {
        GalleryPreviewProgress.BeginAnimation(RangeBase.ValueProperty, null); GalleryPreviewProgress.IsIndeterminate = false; GalleryPreviewProgress.Value = 0;
        var id = _galleryPreviewSession; _galleryPreviewSession = null; var cancellation = _galleryCoverCancellation; _galleryCoverCancellation = null; cancellation?.Cancel(); _galleryPreviewState?.SetPhase(TooltipPreviewPhase.Closed);
        if (_tooltipPreviewController is not null && id is { } session) await _tooltipPreviewController.StopAsync(session);
        try { await _galleryPreviewTask; } catch (OperationCanceledException) { } finally { cancellation?.Dispose(); }
    }
    private void CloseGalleryDetail() { GalleryDetail.IsOpen = false; _ = StopGalleryPreviewAsync(); }
    private async Task StopGalleryOperationPreviewAsync(VideoItem item) { if (ReferenceEquals(ViewModel.InspectedVideo, item)) await StopGalleryPreviewAsync(); }
    private void OnGalleryPopupOpened(object sender, EventArgs e)
    {
        PositionGalleryPopup();
        if (!(_clientAreaAnimationsEnabledOverride ?? SystemParameters.ClientAreaAnimation)) return;
        DetailSurface.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(.6, 1, TimeSpan.FromMilliseconds(160)));
        var shift = new TranslateTransform(); DetailSurface.RenderTransform = shift;
        shift.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut } });
    }
    internal static Rect CalculateGalleryPopupPlacement(Rect anchor, Size client, Size panel)
    {
        const double padding = 12, gap = 12;
        var width = Math.Min(panel.Width, Math.Max(1, client.Width - 2 * padding));
        var height = Math.Min(panel.Height, Math.Max(1, client.Height - 2 * padding));
        var right = anchor.Right + gap;
        var left = anchor.Left - width - gap;
        var x = right + width <= client.Width - padding ? right : left >= padding ? left : anchor.Left;
        x = Math.Clamp(x, padding, Math.Max(padding, client.Width - width - padding));
        var y = Math.Clamp(anchor.Top - 6, padding, Math.Max(padding, client.Height - height - padding));
        return new Rect(x, y, width, height);
    }
    private void PositionGalleryPopup()
    {
        if (!GalleryDetail.IsOpen || Content is not FrameworkElement clientRoot || PresentationSource.FromVisual(clientRoot)?.CompositionTarget is not { } target) return;
        var client = clientRoot.RenderSize;
        var width = Math.Min(390, Math.Max(1, client.Width - 24));
        var maximumHeight = Math.Max(40, client.Height - 24);
        DetailSurface.Width = width;
        DetailSurface.MaxHeight = maximumHeight;
        DetailScroll.MaxHeight = Math.Max(1, maximumHeight - DetailSurface.Padding.Top - DetailSurface.Padding.Bottom - GalleryEditorActionHeight());
        var anchor = _galleryPopup?.Anchor ?? _lastDetailAnchor;
        Rect rect;
        if (anchor is { IsLoaded: true } && ReferenceEquals(anchor.DataContext, ViewModel.InspectedVideo))
        {
            // The recycling slot includes the inter-column gap; only the actual card anchors the panel.
            rect = new Rect(anchor.TranslatePoint(new Point(), clientRoot), new Size(GalleryHost.Metrics.CardWidth, GalleryHost.Metrics.CardHeight));
        }
        else rect = new Rect(FacetShelf.TranslatePoint(new Point(), clientRoot), FacetShelf.RenderSize);
        if (!ViewModel.IsEditing && _galleryPopup?.KeyboardOpened == true && (rect.Bottom < 0 || rect.Top > client.Height)) { _galleryPopup.Close(); return; }
        DetailSurface.Measure(new Size(width + DetailSurface.Margin.Left + DetailSurface.Margin.Right, maximumHeight + DetailSurface.Margin.Top + DetailSurface.Margin.Bottom));
        var height = Math.Max(1, DetailSurface.DesiredSize.Height - DetailSurface.Margin.Top - DetailSurface.Margin.Bottom);
        var placement = CalculateGalleryPopupPlacement(rect, client, new Size(width, height));
        // Convert one owner-client point into screen DIP once. Popup offsets never use the OS caption.
        var screen = target.TransformFromDevice.Transform(clientRoot.PointToScreen(placement.TopLeft));
        GalleryDetail.Placement = PlacementMode.AbsolutePoint;
        GalleryDetail.HorizontalOffset = screen.X - DetailSurface.Margin.Left;
        GalleryDetail.VerticalOffset = screen.Y - DetailSurface.Margin.Top;
    }
    private double GalleryEditorActionHeight()
    {
        if (!ViewModel.IsEditing || FindName("EditorActionsPanel") is not FrameworkElement actions) return 0;
        actions.Measure(new Size(Math.Max(1, DetailSurface.Width - DetailSurface.Padding.Left - DetailSurface.Padding.Right), double.PositiveInfinity));
        return actions.DesiredSize.Height;
    }
    private void CloseGalleryWithFocus()
    {
        if (ViewModel.IsSaving) return;
        var bookmark = _galleryFocusBookmark;
        _explicitGalleryClose = true;
        ++_galleryFocusGeneration;
        try
        {
            CancelGalleryThumbnailWork();
            if (ViewModel.CancelEditCommand.CanExecute(null)) ViewModel.CancelEditCommand.Execute(null);
            _galleryPopup?.Close(true);
        }
        finally { _explicitGalleryClose = false; }
        RequestGalleryFocusRestore(bookmark);
    }
    private void OnDetailCloseClick(object sender, RoutedEventArgs e) => CloseGalleryWithFocus();
    private bool HandleGalleryNavigationKey(KeyEventArgs e)
    {
        ++_galleryFocusGeneration;
        if (_galleryImeComposing || e.Key == Key.ImeProcessed) return true;
        if (e.Key == Key.Escape && GalleryDetail.IsOpen) { e.Handled = true; CloseGalleryWithFocus(); return true; }
        if (HandleGallerySelectionModeKey(e.Key, Keyboard.Modifiers, e.IsRepeat, Keyboard.FocusedElement as DependencyObject)) { e.Handled = true; return true; }
        var editing = Keyboard.FocusedElement is TextBoxBase or PasswordBox;
        if (!editing && e.Key == Key.OemQuestion && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; SearchInput.Focus(); SearchInput.BringIntoView(); return true; }
        if (!editing && IsVideoListFocus(Keyboard.FocusedElement as DependencyObject) && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.E) { e.Handled = true; if (!IsGallerySelectionMode || ViewModel.CheckedCount > 0) ViewModel.EditCommand.Execute(null); return true; }
            if (e.Key is Key.Enter or Key.Tab && GalleryDetail.IsOpen) { e.Handled = true; FocusGalleryAction(); return true; }
        }
        return false;
    }
    private void FocusGalleryAction() { if (ViewModel.IsEditing) EditorPanel.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); else if (DetailPlay.IsEnabled) DetailPlay.Focus(); else DetailReadOnly.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)); }
    private void OnDetailKeyDown(object sender, KeyEventArgs e)
    {
        if (_galleryImeComposing || e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Escape) { e.Handled = true; CloseGalleryWithFocus(); return; }
        if (Keyboard.FocusedElement is TextBoxBase) return;
        if ((e.Key is Key.E or Key.F1) && !ViewModel.IsEditing) { e.Handled = true; ViewModel.EditCommand.Execute(null); }
        else if (e.Key == Key.Delete && !e.IsRepeat && !ViewModel.IsEditing) { e.Handled = true; ViewModel.DeleteInspectedCommand.Execute(null); }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Shift && Keyboard.FocusedElement == DetailPlay) { e.Handled = true; RequestGalleryFocusRestore(_galleryFocusBookmark); }
    }
    internal bool HandleGallerySelectionModeKey(Key key, ModifierKeys modifiers, bool repeat, DependencyObject? focused)
    {
        if (!IsGallerySelectionMode || ViewModel.IsEditing || modifiers != ModifierKeys.None) return false;
        if (key == Key.Escape)
        {
            if (!repeat) SetGallerySelectionMode(false);
            return true;
        }

        if (key != Key.Space || focused is null ||
            ItemsControl.ContainerFromElement(VideoList, focused) is not ListBoxItem { DataContext: VideoItem item })
            return false;

        if (!repeat) ToggleGalleryCardSelection(item, extendRange: false);
        return true;
    }

    private void RefreshGalleryEditor()
    {
        var wasEditing = _galleryWasEditing; _galleryWasEditing = ViewModel.IsEditing;
        if (!wasEditing && ViewModel.IsEditing) RememberGalleryFocus(ViewModel.InspectedVideo ?? ViewModel.SelectedVideo);
        SyncGalleryThumbnailLifetime();
        _galleryPopup?.SetEditing(ViewModel.IsEditing, ViewModel.IsChangePreview, ViewModel.IsSaving);
        SearchInput.IsReadOnly = ViewModel.IsEditing;
        SelectionModeControl.IsEnabled = !ViewModel.IsEditing && !ViewModel.IsSaving;
        SortPicker.IsEnabled = ActorPicker.IsEnabled = StudioPicker.IsEnabled = GenrePicker.IsEnabled = !ViewModel.IsEditing;
        if (ViewModel.IsEditing)
        {
            _ = StopGalleryPreviewAsync(); DetailSurface.DataContext = ViewModel; GalleryDetail.IsOpen = true; DetailReadOnly.Visibility = Visibility.Collapsed; EditorPanel.Visibility = Visibility.Visible;
            var draft = ViewModel.EditorDraft;
            var preview = ViewModel.IsChangePreview;
            var batch = draft?.IsBatch == true;
            DetailHeading.Text = preview ? "변경 미리보기" : batch ? "일괄 편집" : "메타데이터 편집";
            EditorTitle.Text = preview ? draft?.Changes.Count > 0 ? $"{draft.TargetCount}개 영상의 변경사항" : "변경한 내용이 없습니다"
                : batch ? $"{draft?.TargetCount ?? 0}개 영상 선택" : GalleryItemDisplayTitle(ViewModel.InspectedVideo);
            if (FindName("EditorHint") is TextBlock hint) { hint.Text = batch ? "체크한 항목만 수정합니다." : "변경 내용을 먼저 확인한 뒤 적용합니다."; hint.Visibility = preview ? Visibility.Collapsed : Visibility.Visible; }
            if (FindName("EditorPreservation") is TextBlock preservation) preservation.Text = preview ? "표시된 항목만 변경합니다.\n나머지 메타데이터와 원본 파일은 유지됩니다." : batch ? "제목·설명·썸네일은 개별 값을 유지합니다.\n원본 파일과 경로는 변경하지 않습니다." : "원본 파일과 경로는 변경하지 않습니다.";
            if (FindName("EditorActionsPanel") is FrameworkElement actions) actions.IsEnabled = !ViewModel.IsSaving;
            EditorFields.Visibility = ViewModel.IsChangePreview ? Visibility.Collapsed : Visibility.Visible; ThumbnailTools.Visibility = ViewModel.IsChangePreview || ViewModel.EditorDraft?.IsBatch == true ? Visibility.Collapsed : Visibility.Visible; ChangesList.Visibility = ViewModel.IsChangePreview ? Visibility.Visible : Visibility.Collapsed;
            EditorCancel.Visibility = EditorPreview.Visibility = ViewModel.IsChangePreview ? Visibility.Collapsed : Visibility.Visible; EditorBack.Visibility = EditorApply.Visibility = ViewModel.IsChangePreview ? Visibility.Visible : Visibility.Collapsed;
            if (ViewModel.IsSaving) EditorTitle.Text = "저장 중…";
            EditorPanel.IsEnabled = !ViewModel.IsSaving; UpdateGalleryDialogAccessibility(); PositionGalleryPopup();
        }
        else
        {
            DetailHeading.Text = "영상 상세정보";
            EditorPanel.Visibility = Visibility.Collapsed; DetailReadOnly.Visibility = Visibility.Visible;
            if (GalleryDetail.IsOpen && ViewModel.InspectedVideo is null) _galleryPopup?.Close();
            if (wasEditing && !_explicitGalleryClose && !_previewShutdownStarted) RequestGalleryFocusRestore(_galleryFocusBookmark, ViewModel.IsSaving);
        }
    }
    private void OnDetailPlayClick(object sender, RoutedEventArgs e) { if (ViewModel.InspectedVideo is { } item) TryPlayVideo(item); }
    private void OnDetailFetchClick(object sender, RoutedEventArgs e) => ViewModel.FetchMetadataCommand.Execute(null);
    private void OnLibraryFolderClick(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: string path }) { var result = _videoLauncher.OpenFolder(LibraryPathHelper.ResolveToAbsolute(path)); if (!result.Succeeded) ShowGalleryMessage("폴더를 열 수 없습니다", result.Reason ?? "경로를 확인해 주세요."); } }
    private void OnDetailEditClick(object sender, RoutedEventArgs e) => ViewModel.EditCommand.Execute(null);
    private void OnDetailDeleteClick(object sender, RoutedEventArgs e) => ViewModel.DeleteInspectedCommand.Execute(null);
    private void OnDetailRestoreClick(object sender, RoutedEventArgs e) => ViewModel.RestoreInspectedCommand.Execute(null);
    private void OnDetailReinspectClick(object sender, RoutedEventArgs e) => ViewModel.ReinspectInspectedCommand.Execute(null);
    private void OnDetailFolderClick(object sender, RoutedEventArgs e) { if (ViewModel.InspectedVideo is { } item) { var result = _videoLauncher.ShowInFolder(item.SourcePath); if (!result.Succeeded) ShowGalleryMessage("폴더를 열 수 없습니다", result.Reason ?? "경로를 확인해 주세요."); } }
    private void OnActorLinkClick(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: string value }) ViewModel.AddFacetFilter(GalleryFacet.Actors, value); }
    private void OnStudioLinkClick(object sender, RoutedEventArgs e) { if (ViewModel.InspectedVideo is { } item) ViewModel.AddFacetFilter(GalleryFacet.Studio, item.Studio, string.IsNullOrWhiteSpace(item.Studio)); }
    private void OnGenreLinkClick(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: string value }) ViewModel.AddFacetFilter(GalleryFacet.Genres, value); }
    private void OnTagLinkClick(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: string value }) ViewModel.SearchForTag(value); }
    private Task<bool> ConfirmGalleryOperationAsync(GalleryOperationConfirmation confirmation)
    {
        if (_previewShutdownStarted) return Task.FromResult(false);
        if (ConfirmGalleryOperationOverride is { } confirm) return confirm(confirmation);
        return Task.FromResult(ShowGalleryDialog(CreateGalleryOperationDialog(confirmation)) == true);
    }
    internal Window CreateGalleryOperationDialog(GalleryOperationConfirmation confirmation)
    {
        var body = new StackPanel(); body.Children.Add(new TextBlock { Text = $"대상 {confirmation.Count}개", Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = confirmation.Intent == LibraryOperationIntent.Restore ? "영상을 Windows 휴지통에서 원래 경로로 복원합니다." : "영상 파일만 Windows 휴지통으로 이동하며 메타데이터는 앱에서 보존합니다.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        for (var i = 0; i < Math.Min(5, confirmation.Count); i++) { body.Children.Add(new TextBlock { Text = confirmation.Titles[i], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }); if (i < confirmation.Paths.Count) body.Children.Add(new TextBlock { Text = confirmation.Paths[i], Foreground = (Brush)FindResource("GalleryMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 7) }); }
        if (confirmation.Count > 5) body.Children.Add(new TextBlock { Text = $"그 외 {confirmation.Count - 5}개", Margin = new Thickness(0, 7, 0, 0) });
        var title = confirmation.Intent == LibraryOperationIntent.Restore ? "영상을 복원할까요?" : "휴지통으로 이동할까요?";
        return CreateGalleryDialog(title, new ScrollViewer { Content = body, MaxHeight = 400, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, true);
    }
    private bool? ShowGalleryDialog(string title, UIElement body, bool confirmation) =>
        ShowGalleryDialog(CreateGalleryDialog(title, body, confirmation));

    internal Window CreateGalleryDialog(string title, UIElement body, bool confirmation)
    {
        var dialog = new Window { Title = title, WindowStartupLocation = WindowStartupLocation.CenterOwner, SizeToContent = SizeToContent.Height, Width = Math.Min(480, Math.Max(280, ((Content as FrameworkElement)?.ActualWidth ?? ActualWidth) - 32)), MaxHeight = Math.Max(250, ActualHeight - 32), ResizeMode = ResizeMode.NoResize, Background = (Brush)FindResource("GallerySurface"), Foreground = (Brush)FindResource("GalleryText"), FontFamily = FontFamily, FontSize = 12, ShowInTaskbar = false };
        var layout = new StackPanel { Margin = new Thickness(28) }; layout.Children.Add(new TextBlock { Text = title, FontSize = 20, Margin = new Thickness(0, 0, 0, 14), TextWrapping = TextWrapping.Wrap }); layout.Children.Add(body);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 23, 0, 0) };
        var cancel = new Button { Content = confirmation ? "취소" : "닫기", IsCancel = true, IsDefault = true }; cancel.Click += (_, _) => dialog.DialogResult = false; actions.Children.Add(cancel);
        if (confirmation) { var apply = new Button { Content = "확인", Style = (Style)FindResource("GalleryPrimary"), Margin = new Thickness(9, 0, 0, 0) }; apply.Click += (_, _) => dialog.DialogResult = true; actions.Children.Add(apply); }
        layout.Children.Add(actions);
        dialog.WindowStyle = WindowStyle.None; dialog.AllowsTransparency = true; dialog.Background = Brushes.Transparent; dialog.Width += 120; dialog.MaxHeight += 120;
        dialog.Content = new Border { Child = layout, Background = (Brush)FindResource("GallerySurface"), CornerRadius = new CornerRadius(12), Margin = new Thickness(60), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 60, ShadowDepth = 18, Direction = 270, Opacity = .2 } };
        return dialog;
    }
    private bool? ShowGalleryDialog(Window dialog)
    {
        dialog.Owner = this;
        _galleryDialog = dialog;
        dialog.PreviewKeyUp += OnVideoDeleteKeyUp;
        try { return dialog.ShowDialog(); } finally { dialog.PreviewKeyUp -= OnVideoDeleteKeyUp; _galleryDialog = null; }
    }
    private void ShowGalleryMessage(string title, string message) => ShowGalleryDialog(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 22.8 }, false);
    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        var body = CreateGalleryHistoryBody(out var dispose);
        try { ShowGalleryDialog("변경 내역", new ScrollViewer { Content = body, MaxHeight = 450, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, false); }
        finally { dispose(); }
    }
    private async void OnChooseThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.EditorDraft is not { } draft || draft.IsBatch || ViewModel.IsSaving || !_galleryThumbnailTask.IsCompleted) return;
        var dialog = new OpenFileDialog { Title = "커버 이미지 선택", Filter = "이미지|*.jpg;*.jpeg;*.png;*.webp;*.bmp|모든 파일|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true || !ReferenceEquals(ViewModel.EditorDraft, draft)) return;
        await RunGalleryThumbnailWorkAsync(draft, (generation, token) => LoadGalleryThumbnailFileAsync(draft, dialog.FileName, generation, token));
    }
    private void OnResetGalleryThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSaving && _galleryThumbnailTask.IsCompleted) ViewModel.EditorDraft?.ThumbnailEditor.ResetThumbnail();
    }
    private async void OnGenerateGalleryThumbnailClick(object sender, RoutedEventArgs e)
    {
        if (!_galleryThumbnailTask.IsCompleted || ViewModel.EditorDraft is not { } draft || draft.IsBatch || ViewModel.InspectedVideo is not { } item || item.IsInRecycleBin || item.Presence != VideoPresenceState.Available) return;
        await RunGalleryThumbnailWorkAsync(draft, (generation, token) => GenerateGalleryThumbnailAsync(draft, item, generation, token));
    }

    private sealed class GalleryPreviewSink : ITooltipPreviewSink
    {
        private readonly MainWindow _window; private readonly VideoItem _item; private readonly TooltipPreviewState _state; private readonly TimeProvider _clock;
        public GalleryPreviewSink(MainWindow window, VideoItem item, TooltipPreviewState state, TimeProvider clock) { _window = window; _item = item; _state = state; _clock = clock; }
        public bool IsCurrent(Guid id) => _window.Dispatcher.CheckAccess() ? Current(id) : _window.Dispatcher.Invoke(() => Current(id));
        private bool Current(Guid id) => _window._galleryPreviewSession == id && ReferenceEquals(_window.ViewModel.InspectedVideo, _item) && _window.GalleryDetail.IsOpen && !_window.ViewModel.IsEditing && _window._galleryPopup?.Anchor is { IsLoaded: true } anchor && ReferenceEquals(anchor.DataContext, _item);
        public async ValueTask<DispatcherCallbackResult> SetPhaseAsync(Guid id, TooltipPreviewPhase phase, CancellationToken token) { if (!IsCurrent(id)) return default; return await _window.Dispatcher.InvokeAsync(() => { if (!Current(id)) return default; var start = _clock.GetTimestamp(); _state.SetPhase(phase); var end = _clock.GetTimestamp(); return new DispatcherCallbackResult(true, end, _clock.GetElapsedTime(start, end)); }, DispatcherPriority.Render, token); }
        public async ValueTask<FramePresentationResult> ShowFrameAsync(Guid id, int width, int height, VideoPreviewFrame frame, CancellationToken token) { if (!IsCurrent(id)) return default; return await _window.Dispatcher.InvokeAsync(() => { if (!Current(id)) return default; var start = _clock.GetTimestamp(); _state.ShowFrame(width, height, frame); var end = _clock.GetTimestamp(); return new FramePresentationResult(true, end, _clock.GetElapsedTime(start, end)); }, DispatcherPriority.Render, token); }
    }
    private async Task StopGallerySurfaceAsync() { CancelGalleryThumbnailWork(); _galleryDialog?.Close(); _galleryPopup?.Dispose(); await StopGalleryPreviewAsync(); try { await _galleryThumbnailTask; } catch (OperationCanceledException) { } ViewModel.PropertyChanged -= OnGalleryPropertyChanged; ViewModel.GalleryScrollToTopRequested -= OnGalleryScrollToTop; ViewModel.GalleryQueryRequested -= CloseGalleryReadingForQuery; ViewModel.GalleryCheckedWorkCompleted -= OnGalleryCheckedWorkCompleted; GalleryHost.ViewportChanged -= OnGalleryViewportChanged; }
}
