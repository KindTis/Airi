using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Airi.Domain;
using Airi.Controls;
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
    [InlineData(LibraryOperationIntent.Delete)]
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
            Assert.Equal(intent switch
            {
                LibraryOperationIntent.Delete => "선택한 항목을 삭제할까요?",
                LibraryOperationIntent.Restore => "영상을 복원할까요?",
                _ => "휴지통으로 이동할까요?"
            }, dialog.Title);
            Assert.Null(dialog.Owner);
            Assert.Empty(dialog.Resources.MergedDictionaries);
            Assert.Same(Application.Current!.FindResource("GalleryText"), dialog.Foreground);
            Assert.Same(Application.Current.FindResource("GallerySurface"), Assert.IsType<Border>(dialog.Content).Background);
            var texts = FindDescendants<TextBlock>(dialog).Select(text => text.Text).ToArray();
            Assert.Contains(title, texts);
            Assert.Contains(path, texts);
            Assert.Contains("대상 1개", texts);
            var expectedMessage = intent switch
            {
                LibraryOperationIntent.Delete => "라이브러리에서 삭제",
                LibraryOperationIntent.Restore => "원래 경로로 복원",
                _ => "메타데이터는 앱에서 보존"
            };
            Assert.Contains(texts, text => text.Contains(expectedMessage, StringComparison.Ordinal));
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
            ArrangeRoot(window);
            var sort = Assert.IsType<ComboBox>(window.FindName("SortPicker"));
            sort.ApplyTemplate();
            Assert.NotNull(sort.Template.FindName("PART_Popup", sort));
            var sortTrigger = Assert.IsType<ToggleButton>(sort.Template.FindName("SortTrigger", sort));
            var actorPicker = Assert.IsType<GalleryFacetPicker>(window.FindName("ActorPicker"));
            var actorTrigger = Assert.IsType<Button>(actorPicker.FindName("Trigger"));
            Assert.Same(actorTrigger.Style, sortTrigger.Style);
            Assert.Equal(actorTrigger.MinHeight, sortTrigger.MinHeight);
            foreach (var trigger in new ButtonBase[] { actorTrigger, sortTrigger })
            {
                trigger.ApplyTemplate();
                var border = Assert.IsType<Border>(trigger.Template.FindName("FieldBorder", trigger));
                Assert.Equal(new CornerRadius(7), border.CornerRadius);
                Assert.Same(search.BorderBrush, border.BorderBrush);
            }
            Assert.Equal(6, window.ViewModel.SortOptions.Count);
            Assert.False(window.IsVisible);
        }
        finally { window.Close(); }
    });

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
            Assert.Null(window.FindName("GalleryHeading"));
            Assert.Null(window.FindName("HeadingTitle"));
            Assert.Null(window.FindName("NoFilters"));
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
            Assert.Equal($"결과 {vm.ResultCount}개 · 보관 {vm.NormalCount}개",
                Assert.IsType<TextBlock>(window.FindName("LibraryCounts")).Text);
            var random = Assert.IsType<Button>(window.FindName("RandomPlayButton"));
            Assert.Same(vm.RandomPlayCommand, random.Command);
            Assert.Same(Application.Current!.FindResource("GalleryPrimary"), random.Style);
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

    [Theory]
    [InlineData(1280, 880, false)]
    [InlineData(1280, 720, false)]
    [InlineData(415, 880, true)]
    public Task GalleryCard_ShowsOnlyCodeAndActorBelowTitle(double width, double height, bool compact) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        var host = new Window { Width = width, Height = height, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize };
        try
        {
            var template = Assert.IsType<DataTemplate>(window.FindResource("GalleryCard"));
            var item = new VideoItem
            {
                Title = "작품 제목", ProductCode = "ARAN-117", DisplayActors = ["우타카네 유우리"],
                Genres = ["고화질"], Studio = "제작사 이름", ReleaseDate = new DateOnly(2026, 11, 21)
            };
            var metrics = GalleryLayoutMetrics.Calculate(width, height, width, false);
            var list = new ListBox { Tag = new { Metrics = metrics }, ItemTemplate = template, ItemsSource = new[] { item } };
            host.Content = list;
            WpfTestHost.Show(host);
            host.UpdateLayout();
            WpfTestHost.DrainDispatcher(host.Dispatcher);

            var container = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));
            var presenter = Assert.Single(FindVisualDescendants<ContentPresenter>(container), candidate => candidate.ContentTemplate == template);
            var wide = Assert.IsType<Grid>(template.FindName("DesktopMeta", presenter));
            var narrow = Assert.IsType<StackPanel>(template.FindName("CompactMeta", presenter));
            Assert.Equal(compact ? Visibility.Collapsed : Visibility.Visible, wide.Visibility);
            Assert.Equal(compact ? Visibility.Visible : Visibility.Collapsed, narrow.Visibility);
            FrameworkElement metadata = compact ? narrow : wide;
            var shown = FindDescendants<TextBlock>(metadata).Select(text => text.Text).ToArray();
            Assert.Equal(new[] { "ARAN-117", "우타카네 유우리" }, shown);
            Assert.Equal(compact ? 38.4 : 19.2, metadata.ActualHeight, 4);
            Assert.Equal(metrics.CoverHeight + metrics.TitleTop + (compact ? 17.4 : 20.3)
                + metrics.MetadataTop + metadata.ActualHeight, metrics.CardHeight, 4);
        }
        finally { host.Close(); window.Close(); }
    });

    [Theory]
    [InlineData(1507, 884)]
    [InlineData(340, 700)]
    [InlineData(340, 500)]
    [InlineData(340, 400)]
    [InlineData(320, 360)]
    public Task PointerTooltip_KeepsInformationBelowCoverWithoutActionButtons(double width, double height) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        try
        {
            var surface = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("DetailSurface"));
            Assert.False(surface.IsHitTestVisible);
            Assert.False(surface.Focusable);
            Assert.Null(window.FindName("DetailPlay"));
            var item = vm.Videos[0];
            var description = new string('설', 1200);
            var path = @"C:\" + new string('x', 400) + ".mp4";
            item.Title = new string('제', 180);
            item.ProductCode = "ARAN-117";
            item.Description = description;
            item.Studio = "BabyEntertainment";
            item.RecycleState = LibraryOperationState.RepairRequired;
            item.RecycleReason = new string('복', 500);
            item.UpdateFileState(path, item.SizeBytes, item.LastModifiedUtc, item.Presence, item.CreatedUtc);
            item.Actors = Enumerable.Range(1, 30).Select(index => $"배우 {index}").ToArray();
            item.Genres = Enumerable.Range(1, 20).Select(index => $"긴 장르 {index}").ToArray();
            item.Tags = Enumerable.Range(1, 30).Select(index => $"긴 태그 {index}").ToArray();
            vm.InspectCommand.Execute(item);
            surface.DataContext = vm;
            Assert.IsType<Grid>(window.FindName("DetailBody")).DataContext = item;
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var layout = window.ApplyGalleryTooltipLayout(new Size(width, height));
            surface.Measure(new Size(layout.Width + 80, layout.Height + 80));
            surface.Arrange(new Rect(surface.DesiredSize));
            var cover = Assert.IsAssignableFrom<FrameworkElement>(window.FindName("DetailCoverFrame"));
            Assert.Null(window.FindName("DetailScroll"));
            Assert.DoesNotContain(FindDescendants<ScrollViewer>(surface), _ => true);
            var information = Assert.IsType<StackPanel>(window.FindName("DetailReadOnly"));
            Assert.Same(window.FindName("DetailTitle"), information.Children[0]);
            Assert.Same(window.FindName("DetailDescription"), information.Children[1]);
            var facts = Assert.IsType<Grid>(Assert.IsType<Border>(information.Children[2]).Child);
            Assert.Same(window.FindName("DetailFacts"), facts);
            var actors = Assert.IsType<StackPanel>(window.FindName("DetailActors"));
            var studio = Assert.IsType<StackPanel>(window.FindName("DetailStudio"));
            Assert.Equal(Grid.GetRow(actors), Grid.GetRow(studio));
            Assert.True(Grid.GetColumn(studio) > Grid.GetColumn(actors));
            foreach (var field in new[] { actors, studio })
            {
                var label = Assert.IsType<TextBlock>(field.Children[0]);
                var value = Assert.IsType<TextBlock>(field.Children[1]);
                Assert.True(value.TranslatePoint(new Point(), field).Y >= label.TranslatePoint(new Point(0, label.ActualHeight), field).Y);
            }
            Assert.Equal(item.Studio, Assert.IsType<TextBlock>(window.FindName("DetailStudioText")).Text);
            var genres = Assert.IsType<StackPanel>(window.FindName("DetailGenres"));
            var tags = Assert.IsType<StackPanel>(window.FindName("DetailTags"));
            Assert.Equal(genres.TranslatePoint(new Point(), facts).Y, tags.TranslatePoint(new Point(), facts).Y, 4);
            Assert.True(tags.TranslatePoint(new Point(), facts).X > genres.TranslatePoint(new Point(), facts).X);
            Assert.Equal(genres.ActualWidth, tags.ActualWidth, 4);
            var file = Assert.IsType<Border>(information.Children[3]);
            Assert.Same(window.FindName("DetailFile"), file);
            Assert.Contains(Assert.IsType<TextBlock>(window.FindName("DetailPath")), FindDescendants<TextBlock>(file));
            Assert.Equal("ARAN-117", Assert.IsType<TextBlock>(window.FindName("DetailProductCode")).Text);
            Assert.Same(Application.Current!.FindResource("GalleryText"), Assert.IsType<TextBlock>(window.FindName("DetailActorsText")).Foreground);
            Assert.DoesNotContain(FindDescendants<TextBlock>(file), text =>
                System.Windows.Data.BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path == nameof(VideoItem.CreatedLabel));
            var coverBottom = cover.TranslatePoint(new Point(0, cover.ActualHeight), surface).Y;
            var informationTop = information.TranslatePoint(new Point(), surface).Y;
            Assert.True(informationTop >= coverBottom + layout.Gap - .01, $"Information {informationTop} must follow cover {coverBottom}");
            Assert.True(information.ActualWidth >= cover.ActualWidth);
            Assert.Equal(cover.ActualWidth + 24, surface.ActualWidth, 5);
            Assert.Equal(12, cover.TranslatePoint(new Point(), surface).X, 5);
            Assert.True(information.DesiredSize.Height <= layout.BodyHeight + .1);
            Assert.True(file.TranslatePoint(new Point(0, file.ActualHeight), information).Y <= layout.BodyHeight + .1);
            Assert.InRange(layout.Height, 1, height - 24);
            foreach (var name in new[] { "DetailTitle", "DetailActorsText", "DetailGenresText", "DetailTagsText", "DetailDescriptionText", "DetailPath" })
            {
                var text = Assert.IsType<TextBlock>(window.FindName(name));
                Assert.Equal(TextTrimming.CharacterEllipsis, text.TextTrimming);
                Assert.True(text.ActualHeight <= text.MaxHeight + .1);
            }
            foreach (var name in new[] { "DetailGenresText", "DetailTagsText" })
            {
                var field = Assert.IsType<TextBlock>(window.FindName(name));
                Assert.Equal(TextWrapping.Wrap, field.TextWrapping);
                Assert.InRange(field.MaxHeight, field.LineHeight, 2 * field.LineHeight);
                if (height >= 700) Assert.Equal(2 * field.LineHeight, field.ActualHeight, 4);
            }
            Assert.Equal(description, item.Description);
            Assert.Equal(path, item.SourcePath);
            Assert.DoesNotContain(FindDescendants<Button>(surface), _ => true);
            Assert.False(window.IsVisible);
            Assert.False(window.GalleryPopupIsOpenForTests);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task Hover_SelectsImmediatelyWithoutChangingCheckedTargets() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        try
        {
            var checkedItem = vm.Videos[0];
            var hovered = vm.Videos[1];
            checkedItem.IsChecked = true;
            var anchor = new ListBoxItem { DataContext = hovered };
            typeof(MainWindow).GetMethod("OnGalleryCardEnter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, new object[] { anchor, new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0) });
            Assert.Same(hovered, vm.SelectedVideo);
            Assert.True(hovered.IsActive);
            Assert.False(checkedItem.IsActive);
            Assert.True(checkedItem.IsChecked);
            Assert.False(hovered.IsChecked);
            // The same selection path is used when a hovered recycling slot receives another item.
            var recycled = vm.Videos[2];
            anchor.DataContext = recycled;
            typeof(MainWindow).GetMethod("HoverGalleryCard", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, new object[] { recycled, anchor, new Point(20, 20) });
            Assert.Same(recycled, vm.SelectedVideo);
            Assert.True(recycled.IsActive);
            Assert.False(hovered.IsActive);
            Assert.True(checkedItem.IsChecked);
            Assert.False(window.IsVisible);
            Assert.False(window.GalleryPopupIsOpenForTests);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public Task F1_EditCapturesCheckedTargetsOrCurrentSelectionAndCancelPreservesStoredData(int checkedCount) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var window = new MainWindow(vm);
        try
        {
            var checkedItems = vm.Videos.Take(checkedCount).ToArray();
            foreach (var item in checkedItems) item.IsChecked = true;
            vm.SelectedVideo = vm.Videos[2];
            var expected = checkedCount == 0 ? new[] { vm.SelectedVideo! } : checkedItems;
            expected[0].RecycleState = LibraryOperationState.RepairRequired;
            expected[0].RecycleReason = new string('복', 500);
            await (Task)typeof(MainWindow).GetMethod("OpenMetadataEditorAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(window, null)!;
            var draft = Assert.IsType<GalleryEditorDraft>(vm.EditorDraft);
            Assert.Equal(expected.Select(item => item.Id), draft.Targets.Select(item => item.Id));
            Assert.Equal(checkedCount > 1, draft.IsBatch);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var fullPath = Assert.IsType<TextBox>(window.GalleryEditorWindowForTests.FindName("EditorSourcePath"));
            Assert.True(fullPath.IsReadOnly);
            Assert.Equal(expected[0].SourcePath, fullPath.Text);
            var fullReason = Assert.IsType<TextBlock>(window.GalleryEditorWindowForTests.FindName("EditorRecycleReason"));
            Assert.Equal(expected[0].RecycleReason, fullReason.Text);
            Assert.False(window.GalleryPopupIsOpenForTests);
            vm.SelectedVideo = vm.Videos[1];
            Assert.Equal(expected.Select(item => item.Id), draft.Targets.Select(item => item.Id));
            vm.CancelEditCommand.Execute(null);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            Assert.Null(vm.EditorDraft);
            Assert.Equal(fixture.InitialJson, await File.ReadAllTextAsync(fixture.Store.FilePath));
            Assert.DoesNotContain(Application.Current!.Windows.Cast<Window>(), w => w.IsVisible);
        }
        finally { vm.CancelEditCommand.Execute(null); window.Close(); }
    });

    [Fact]
    public Task KeyboardAndCardMenu_UseCapturedVideoWithoutLaunchingExternalApps() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var vm = await fixture.CreateAsync();
        var launcher = new RecordingLauncher();
        var window = new MainWindow(vm, videoLauncher: launcher);
        try
        {
            vm.Videos[0].IsChecked = true;
            var selected = vm.Videos[1];
            vm.SelectedVideo = selected;
            var handler = typeof(MainWindow).GetMethod("HandleGalleryPlaybackKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            bool Enter(DependencyObject focused, bool repeat = false) => (bool)handler.Invoke(window, new object[] { System.Windows.Input.Key.Enter, System.Windows.Input.ModifierKeys.None, repeat, focused })!;
            Assert.True(Enter(window.GetVideoListForTests()));
            Assert.False(Enter(new TextBox()));
            Assert.False(Enter(window.GetVideoListForTests(), true));
            Assert.Equal(new[] { selected.SourcePath }, launcher.PlayPaths);
            var menu = window.CreateGalleryCardMenu(selected);
            var items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.DoesNotContain(items, item => Equals(item.Header, "재생") || Equals(item.Header, "편집"));
            Assert.Contains(items, item => Equals(item.Header, "메타데이터 갱신"));
            Assert.True(Assert.Single(items, item => Equals(item.Header, "삭제")).IsEnabled);
            vm.SelectedVideo = vm.Videos[2];
            Assert.Single(items, item => Equals(item.Header, "폴더에서 보기")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.Equal(new[] { selected.SourcePath }, launcher.FolderPaths);
            Assert.Same(selected, vm.SelectedVideo);
            Assert.True(vm.Videos[0].IsChecked);
            Assert.False(menu.IsOpen);
            Assert.False(window.IsVisible);

            File.Delete(selected.SourcePath);
            selected.Presence = VideoPresenceState.Missing;
            window.ConfirmGalleryOperationOverride = _ => Task.FromResult(true);
            var missingMenu = window.CreateGalleryCardMenu(selected);
            var delete = Assert.Single(missingMenu.Items.OfType<MenuItem>(), item => Equals(item.Header, "삭제"));
            Assert.True(delete.IsEnabled);
            vm.SelectedVideo = vm.Videos[2];
            delete.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await vm.GalleryActionTask;
            Assert.DoesNotContain(selected, vm.Videos);
            var saved = await fixture.Store.LoadAsync();
            Assert.Equal(2, saved.Videos.Count);
            Assert.DoesNotContain(saved.Videos, entry => entry.Id == selected.Id);
            Assert.Empty(fixture.Recycler.Paths);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(1507, 884, 500, 420, 80, 80, false)]
    [InlineData(1507, 884, 500, 420, 1400, 80, true)]
    [InlineData(1507, 884, 500, 420, 4, 4, false)]
    [InlineData(1507, 884, 500, 420, 1503, 880, true)]
    [InlineData(340, 400, 280, 260, 160, 210, false)]
    [InlineData(340, 400, 280, 260, 180, 210, true)]
    public void PointerTooltip_PlacementTracksPointerAndStaysInsideViewport(double width, double height,
        double popupWidth, double popupHeight, double x, double y, bool expectedLeft)
    {
        var viewport = new Size(width, height);
        var size = new Size(popupWidth, popupHeight);
        var left = false;
        var first = GalleryTooltipLayout.Place(new Point(x, y), viewport, size, ref left);
        Assert.Equal(expectedLeft, left);
        Assert.Equal(16, left ? x - first.Right : first.Left - x, 5);
        Assert.Equal(Math.Clamp(y, 12, height - popupHeight - 12), first.Top, 5);
        Assert.InRange(first.Left, 12, width - 12);
        Assert.InRange(first.Right, 12, width - 12);
        Assert.InRange(first.Bottom, 12, height - 12);
        Assert.Equal(popupHeight, first.Height, 5);
        Assert.InRange(first.Width, 1, popupWidth);
        if (y == 80)
        {
            var second = GalleryTooltipLayout.Place(new Point(x + 4, y + 4), viewport, size, ref left);
            Assert.Equal(first.X + 4, second.X, 5);
            Assert.Equal(first.Y + 4, second.Y, 5);
        }
    }

    private sealed class RecordingLauncher : IVideoLauncher
    {
        public List<string> PlayPaths { get; } = new();
        public List<string> FolderPaths { get; } = new();
        public VideoLaunchResult Play(string path) { PlayPaths.Add(path); return new(true); }
        public VideoLaunchResult ShowInFolder(string path) { FolderPaths.Add(path); return new(true); }
    }

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
