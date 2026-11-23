using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Airi.Controls;
using Airi.Domain;
using Airi.Services;
using Airi.ViewModels;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class WindowVisualStyleTests
{
    [Fact]
    public Task Windows_ShareSingleGalleryDictionaryAndChrome() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var viewModel = await fixture.CreateAsync();
        var mainWindow = new MainWindow(viewModel);
        var thumbnailWindow = new ThumbnailSelectionWindow(CreateCandidates());
        try
        {
            var application = Application.Current!;
            var dictionary = Assert.Single(application.Resources.MergedDictionaries);
            Assert.Equal("Resources/Gallery.xaml", dictionary.Source.OriginalString);
            Assert.Empty(mainWindow.Resources.MergedDictionaries);
            Assert.Empty(thumbnailWindow.Resources.MergedDictionaries);
            foreach (var key in new[] { "GalleryBackground", "GallerySurface", "GalleryText", "GalleryPrimary" })
            {
                Assert.Same(application.FindResource(key), mainWindow.FindResource(key));
                Assert.Same(application.FindResource(key), thumbnailWindow.FindResource(key));
            }
            Assert.Equal(WindowStyle.SingleBorderWindow, mainWindow.WindowStyle);
            Assert.Equal(WindowStyle.None, thumbnailWindow.WindowStyle);
            Assert.Equal(Color.FromRgb(0x18, 0x1C, 0x1E), Assert.IsType<SolidColorBrush>(mainWindow.Background).Color);
            var assembly = typeof(App).Assembly;
            using var stream = assembly.GetManifestResourceStream(Assert.Single(assembly.GetManifestResourceNames(), name => name.EndsWith(".g.resources", StringComparison.Ordinal)))!;
            using var resources = new ResourceReader(stream);
            var keys = resources.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).ToArray();
            Assert.DoesNotContain(keys, key => key.StartsWith("themes/", StringComparison.OrdinalIgnoreCase) || key is "views/metadataeditorwindow.baml" or "views/videodeleteconfirmationwindow.baml");
        }
        finally { thumbnailWindow.Close(); mainWindow.Close(); }
    });

    [Fact]
    public Task ThumbnailActionsAndTextTooltip_LoadGalleryTemplatesWithoutShowing() => WpfTestHost.RunAsync(() =>
    {
        var window = new ThumbnailSelectionWindow(CreateCandidates());
        try
        {
            var primary = (Style)Application.Current!.FindResource("GalleryPrimary");
            var hover = Assert.Single(primary.Triggers.OfType<Trigger>(), trigger => trigger.Property == UIElement.IsMouseOverProperty && Equals(trigger.Value, true));
            var hoverBackground = Assert.IsType<SolidColorBrush>(Assert.Single(hover.Setters.OfType<Setter>(), setter => setter.Property == Control.BackgroundProperty).Value);
            var hoverForeground = Assert.IsType<SolidColorBrush>(Assert.Single(hover.Setters.OfType<Setter>(), setter => setter.Property == Control.ForegroundProperty).Value);
            Assert.Same(Application.Current.FindResource("GalleryHover"), hoverBackground);
            Assert.Same(Application.Current.FindResource("GalleryText"), hoverForeground);
            Assert.True(ContrastRatio(hoverForeground.Color, hoverBackground.Color) >= 4.5);
            foreach (var name in new[] { "RegenerateButton", "ConfirmButton" })
            {
                var button = Assert.IsType<Button>(window.FindName(name));
                Assert.Same(Application.Current!.FindResource("GalleryPrimary"), button.Style);
                AssertGalleryButtonTemplate(button);
            }
            var cancel = Assert.IsType<Button>(window.FindName("CancelButton"));
            Assert.Same(Application.Current!.FindResource(typeof(Button)), cancel.Style);
            AssertGalleryButtonTemplate(cancel);
            var danger = new Button { Content = "휴지통", Style = (Style)Application.Current.FindResource("GalleryDangerButton") };
            AssertGalleryButtonTemplate(danger);
            var accessKey = new Button { Content = "_확인", Style = (Style)Application.Current.FindResource("GalleryPrimary") };
            AssertGalleryButtonTemplate(accessKey);
            var explicitLabel = new TextBlock { Text = "명시적 색상", Foreground = (Brush)Application.Current.FindResource("GalleryMuted") };
            var customContent = new Button { Content = explicitLabel, Style = (Style)Application.Current.FindResource("GalleryPrimary") };
            AssertGalleryButtonTemplate(customContent);
            Assert.Same(Application.Current.FindResource("GalleryMuted"), explicitLabel.Foreground);
            Assert.False(Assert.IsType<Button>(window.FindName("ConfirmButton")).IsEnabled);
            var tooltip = new ToolTip { Content = "임의 영상 재생" };
            tooltip.ApplyTemplate();
            Assert.NotNull(tooltip.Template);
            Assert.Same(Application.Current!.FindResource("GallerySurface"), tooltip.Background);
            Assert.Same(Application.Current.FindResource("GalleryText"), tooltip.Foreground);
            Assert.False(tooltip.IsOpen);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });

    [Theory]
    [InlineData(LibraryOperationIntent.Recycle)]
    [InlineData(LibraryOperationIntent.Restore)]
    public Task GalleryOperationDialog_UsesFullValuesAndCancelDefaultWithoutShowing(LibraryOperationIntent intent) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var viewModel = await fixture.CreateAsync();
        var window = new MainWindow(viewModel);
        var title = new string('가', 300);
        var path = Path.Combine(fixture.Root, new string('a', 220) + ".mp4");
        var dialog = window.CreateGalleryOperationDialog(new GalleryOperationConfirmation(intent, new[] { title }) { Paths = new[] { path } });
        try
        {
            Assert.Equal(intent == LibraryOperationIntent.Restore ? "영상을 복원할까요?" : "휴지통으로 이동할까요?", dialog.Title);
            Assert.Null(dialog.Owner);
            Assert.Empty(dialog.Resources.MergedDictionaries);
            Assert.Same(Application.Current!.FindResource("GalleryText"), dialog.Foreground);
            Assert.Same(Application.Current.FindResource("GallerySurface"), Assert.IsType<Border>(dialog.Content).Background);
            var texts = FindDescendants<TextBlock>(dialog).Select(text => text.Text).ToArray();
            Assert.Contains(title, texts);
            Assert.Contains(path, texts);
            Assert.Contains("대상 1개", texts);
            Assert.Contains(texts, text => text.Contains(intent == LibraryOperationIntent.Restore ? "원래 경로로 복원" : "메타데이터는 앱에서 보존", StringComparison.Ordinal));
            var buttons = FindDescendants<Button>(dialog).ToArray();
            var cancel = Assert.Single(buttons, button => Equals(button.Content, "취소"));
            Assert.True(cancel.IsDefault);
            Assert.True(cancel.IsCancel);
            var confirm = Assert.Single(buttons, button => Equals(button.Content, "확인"));
            Assert.False(confirm.IsDefault);
            Assert.Same(Application.Current.FindResource("GalleryPrimary"), confirm.Style);
            foreach (var button in buttons) AssertGalleryButtonTemplate(button);
            Assert.NotEqual(true, dialog.DialogResult);
            Assert.False(dialog.IsVisible);
        }
        finally { dialog.Close(); window.Close(); }
    });

    [Fact]
    public Task SearchAndSortControls_UseGalleryNativeChrome() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var viewModel = await fixture.CreateAsync();
        var window = new MainWindow(viewModel);
        try
        {
            var search = Assert.IsType<Border>(window.FindName("SearchBorder"));
            Assert.Equal(new CornerRadius(7), search.CornerRadius);
            Assert.Equal(39, search.Height);
            Assert.Same(Application.Current!.FindResource("GalleryBackground"), search.Background);
            var sort = Assert.IsType<ComboBox>(window.FindName("SortPicker"));
            sort.ApplyTemplate();
            Assert.NotNull(sort.Template.FindName("PART_Popup", sort));
            Assert.Equal(6, window.ViewModel.SortOptions.Count);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

    private static double ContrastRatio(Color foreground, Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        var first = Luminance(foreground);
        var second = Luminance(background);
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }

    private static void AssertGalleryButtonTemplate(Button button)
    {
        button.ApplyTemplate();
        button.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        button.Arrange(new Rect(button.DesiredSize));
        var border = Assert.IsType<Border>(button.Template.FindName("Body", button));
        Assert.Equal(new CornerRadius(7), border.CornerRadius);
        if (button.Content is string)
        {
            var label = Assert.Single(FindVisualDescendants<TextBlock>(button));
            Assert.Equal(Assert.IsType<SolidColorBrush>(button.Foreground).Color, Assert.IsType<SolidColorBrush>(label.Foreground).Color);
        }
    }

    private static IEnumerable<T> FindVisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualDescendants<T>(child)) yield return descendant;
        }
    }

    [Theory]
    [InlineData(1507)]
    [InlineData(340)]
    public Task SelectionMode_UpdatesSplitButtonWithoutMovingGallery(double width) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        try
        {
            var size = new Size(width, 884);
            ArrangeRoot(window, size);
            Assert.Null(window.FindName("VisibleCheck"));
            var prefix = Assert.IsType<StackPanel>(window.FindName("GalleryPrefix"));
            Assert.DoesNotContain(FindDescendants<Button>(prefix), b => ReferenceEquals(b.Command, vm.EditCommand));
            var sort = Assert.IsType<ComboBox>(window.FindName("SortPicker"));
            var control = Assert.IsType<Border>(window.FindName("SelectionModeControl"));
            var mode = Assert.IsType<Button>(window.FindName("SelectionModeButton"));
            var modeLabel = Assert.IsType<TextBlock>(window.FindName("SelectionModeLabel"));
            var actions = Assert.IsType<Button>(window.FindName("SelectionActionsButton"));
            var badge = Assert.IsType<Border>(window.FindName("SelectionCountBadge"));
            var filterRow = Assert.IsType<Grid>(sort.Parent);
            Assert.Same(filterRow, control.Parent);
            var modeIcon = Assert.IsType<GalleryIcon>(window.FindName("SelectionModeIcon"));
            Assert.Null(window.FindName("CheckedSummary"));
            var list = Assert.IsType<ListBox>(window.FindName("VideoList"));
            var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
            var listOrigin = list.TranslatePoint(new Point(), root);
            var controlBounds = control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize));
            var sortBounds = sort.TransformToAncestor(root).TransformBounds(new Rect(sort.RenderSize));
            var filterRowBounds = filterRow.TransformToAncestor(root).TransformBounds(new Rect(filterRow.RenderSize));
            Assert.Equal(filterRowBounds.Right, controlBounds.Right, 2);
            Assert.Equal(sortBounds.Top, controlBounds.Top, 2);
            Assert.True(controlBounds.Left - sortBounds.Right >= 16);
            Assert.InRange(sort.ActualWidth, 130, 200);
            var card = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0));
            var video = Assert.IsType<VideoItem>(card.DataContext);
            var checkbox = Assert.Single(FindVisualDescendants<CheckBox>(card));
            Assert.Equal(Visibility.Collapsed, checkbox.Visibility);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.False(actions.IsVisible);
            Assert.Equal("다중 선택", modeLabel.Text);
            Assert.Equal("checkbox", modeIcon.Kind);
            Assert.Equal(Visibility.Visible, modeIcon.Visibility);

            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            ArrangeRoot(window, size);
            Assert.Equal(Visibility.Visible, checkbox.Visibility);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.False(actions.IsEnabled);
            Assert.Equal("선택 종료", modeLabel.Text);
            Assert.Equal(Visibility.Collapsed, modeIcon.Visibility);
            Assert.Equal(listOrigin, list.TranslatePoint(new Point(), root));
            Assert.True(await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list));
            Assert.Contains(video, vm.Videos);

            card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
            });
            card.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
            });
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            Assert.True(video.IsChecked);
            Assert.True(checkbox.IsChecked);
            Assert.Equal(1, vm.CheckedCount);
            ArrangeRoot(window, size);
            Assert.True(actions.IsEnabled);
            Assert.Equal(Visibility.Visible, badge.Visibility);
            Assert.Equal("1", FindVisualDescendants<TextBlock>(badge).Single().Text);
            Assert.Equal(listOrigin, list.TranslatePoint(new Point(), root));
            Assert.Equal(controlBounds, control.TransformToAncestor(root).TransformBounds(new Rect(control.RenderSize)));
            var menu = Assert.IsType<ContextMenu>(actions.ContextMenu);
            var outside = Assert.IsType<MenuItem>(window.FindName("FilteredSelectionSummary"));
            Assert.Same(actions, menu.PlacementTarget);
            Assert.Same(vm.FetchMetadataCommand, Assert.IsType<MenuItem>(window.FindName("SelectionRefreshItem")).Command);
            Assert.Same(vm.DeleteCommand, Assert.IsType<MenuItem>(window.FindName("SelectionDeleteItem")).Command);
            Assert.Same(vm.RestoreCommand, Assert.IsType<MenuItem>(window.FindName("SelectionRestoreItem")).Command);
            Assert.Same(vm.ClearCheckedCommand, Assert.IsType<MenuItem>(window.FindName("SelectionClearItem")).Command);
            Assert.Equal(Visibility.Collapsed, outside.Visibility);

            vm.SearchQuery = "no-matching-selection-test";
            await vm.GalleryQueryTask;
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            Assert.Equal(Visibility.Visible, outside.Visibility);
            Assert.Equal("필터 밖 영상 1개 포함", Assert.IsType<TextBlock>(outside.Header).Text);
            vm.SearchQuery = string.Empty;
            await vm.GalleryQueryTask;
            ArrangeRoot(window, size);
            card = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(video));
            checkbox = Assert.Single(FindVisualDescendants<CheckBox>(card));

            Assert.True(window.HandleGallerySelectionModeKey(Key.Space, ModifierKeys.None, false, card));
            Assert.False(video.IsChecked);
            Assert.True(window.HandleGallerySelectionModeKey(Key.Space, ModifierKeys.None, false, card));
            Assert.True(video.IsChecked);
            Assert.True(window.HandleGallerySelectionModeKey(Key.Escape, ModifierKeys.None, false, card));
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            Assert.Equal(0, vm.CheckedCount);
            Assert.Equal(Visibility.Collapsed, checkbox.Visibility);
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.False(window.IsGallerySelectionMode);
            Assert.Equal(listOrigin, list.TranslatePoint(new Point(), root));

            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(window.IsGallerySelectionMode);
            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.False(window.IsGallerySelectionMode);
            Assert.False(window.IsVisible);
            Assert.False(window.GalleryPopupIsOpenForTests);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task SelectionMode_ShiftAddsDisplayedRangeAndResetsAnchorAfterFilter() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        try
        {
            var mode = Assert.IsType<Button>(window.FindName("SelectionModeButton"));
            var list = Assert.IsType<ListBox>(window.FindName("VideoList"));
            ArrangeRoot(window);
            var items = list.Items.Cast<VideoItem>().ToArray();
            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            window.ToggleGalleryCardSelection(items[2], extendRange: false);
            window.ToggleGalleryCardSelection(items[0], extendRange: false);
            window.ToggleGalleryCardSelection(items[1], extendRange: true);
            Assert.All(items, item => Assert.True(item.IsChecked));

            vm.ClearCheckedCommand.Execute(null);
            window.ToggleGalleryCardSelection(items[2], extendRange: false);
            window.ToggleGalleryCardSelection(items[0], extendRange: true);
            Assert.All(items, item => Assert.True(item.IsChecked));

            vm.ClearCheckedCommand.Execute(null);
            window.ToggleGalleryCardSelection(items[0], extendRange: false);
            vm.SearchQuery = items[2].Title;
            await vm.GalleryQueryTask;
            Assert.Same(items[2], Assert.Single(list.Items.Cast<VideoItem>()));
            window.ToggleGalleryCardSelection(items[2], extendRange: true);
            Assert.True(items[2].IsChecked);
            Assert.True(items[0].IsChecked);
            Assert.False(items[1].IsChecked);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task SelectionMode_RetainsChecksOnCancelAndEndsOnlyWhenAllTargetsComplete(bool partial) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        try
        {
            var mode = Assert.IsType<Button>(window.FindName("SelectionModeButton"));
            mode.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var first = vm.Videos[0];
            var second = vm.Videos[1];
            window.ToggleGalleryCardSelection(first, extendRange: false);
            window.ToggleGalleryCardSelection(second, extendRange: false);

            await vm.BeginGalleryEditAsync();
            vm.CancelEditCommand.Execute(null);
            Assert.True(window.IsGallerySelectionMode);
            Assert.Equal(2, vm.CheckedCount);

            await vm.BeginGalleryEditAsync();
            var draft = Assert.IsType<GalleryEditorDraft>(vm.EditorDraft);
            if (partial)
            {
                var entry = draft.Targets.Single(item => item.Id == first.Id);
                draft.IsAutomaticProposal = true;
                draft.AutomaticCandidates = new() { [entry.Id] = entry with { Meta = entry.Meta with { Studio = "Updated studio" } } };
                draft.MetadataUpdatesNeedingAttention.Add(second.Id);
                vm.PreviewChangesCommand.Execute(null);
            }
            else
            {
                var studio = draft.Fields.Single(field => field.Field == MetadataField.Studio);
                studio.IsApplied = true;
                studio.Value = "Updated studio";
                vm.PreviewChangesCommand.Execute(null);
            }

            vm.ApplyChangesCommand.Execute(null);
            await vm.GalleryActionTask;
            Assert.Null(vm.EditorDraft);
            Assert.False(first.IsChecked);
            Assert.Equal(partial, second.IsChecked);
            Assert.Equal(partial, window.IsGallerySelectionMode);
            Assert.Equal(partial ? 1 : 0, vm.CheckedCount);
        }
        finally { window.Close(); }
    });

    private static void ArrangeRoot(MainWindow window, Size? viewport = null)
    {
        var root = (FrameworkElement)window.Content;
        var size = viewport ?? new Size(1507, 884);
        for (var pass = 0; pass < 3; pass++)
        {
            root.Measure(size);
            root.Arrange(new Rect(size));
            root.UpdateLayout();
            WpfTestHost.DrainDispatcher(window.Dispatcher);
        }
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match) yield return match;
            foreach (var descendant in FindDescendants<T>(child)) yield return descendant;
        }
    }

    private static VideoThumbnailCandidate[] CreateCandidates()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg");
        return Enumerable.Range(1, 5).Select(index => new VideoThumbnailCandidate(TimeSpan.FromSeconds(index), imagePath)).ToArray();
    }
}
