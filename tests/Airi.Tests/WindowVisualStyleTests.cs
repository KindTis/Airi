using System.Collections;
using System.IO;
using System.Resources;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
