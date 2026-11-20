using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Airi.Services;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class WindowVisualStyleTests
{
    [Fact]
    public Task Windows_UseRequestedChrome() => WpfTestHost.RunAsync(() =>
    {
        var mainWindow = new MainWindow();
        var metadataWindow = new MetadataEditorWindow(CreateVideoItem());
        var thumbnailWindow = new ThumbnailSelectionWindow(CreateCandidates());

        try
        {
            var application = Application.Current
                ?? throw new InvalidOperationException("WPF application is not initialized.");
            var customWindowStyle = Assert.IsType<Style>(
                application.TryFindResource("CustomWindowStyle"));

            Assert.Equal(WindowStyle.SingleBorderWindow, mainWindow.WindowStyle);
            var titleBarBrush = Assert.IsType<SolidColorBrush>(mainWindow.Background);
            Assert.Equal(Color.FromRgb(0x18, 0x1C, 0x1E), titleBarBrush.Color);
            Assert.Equal(WindowStyle.None, metadataWindow.WindowStyle);
            Assert.Equal(WindowStyle.None, thumbnailWindow.WindowStyle);
        }
        finally
        {
            thumbnailWindow.Close();
            metadataWindow.Close();
            mainWindow.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ActionButtons_UseRoleStylesAndSharedShape() => WpfTestHost.RunAsync(() =>
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("WPF application is not initialized.");
        var primaryStyle = Assert.IsType<Style>(
            application.TryFindResource("PrimaryActionButtonStyle"));
        var secondaryStyle = Assert.IsType<Style>(
            application.TryFindResource("SecondaryActionButtonStyle"));
        var mainWindow = new MainWindow();
        var metadataWindow = new MetadataEditorWindow(CreateVideoItem());
        var thumbnailWindow = new ThumbnailSelectionWindow(CreateCandidates());

        try
        {
            WpfTestHost.DrainDispatcher(mainWindow.Dispatcher);
            AssertBrushSetter(
                primaryStyle,
                Control.BackgroundProperty,
                Color.FromRgb(0x3B, 0x82, 0xF6));
            AssertBrushSetter(
                secondaryStyle,
                Control.BackgroundProperty,
                Color.FromRgb(0x1E, 0x22, 0x34));
            Assert.NotSame(primaryStyle, FindDescendants<Button>(mainWindow).Single(button => ReferenceEquals(button.Command, mainWindow.ViewModel.RandomPlayCommand)).Style);
            AssertButtonsUseStyle(
                metadataWindow,
                primaryStyle,
                "파일 선택",
                "썸네일 생성",
                "저장");
            AssertButtonsUseStyle(
                metadataWindow,
                secondaryStyle,
                "초기화",
                "Try Parse On 141Jav",
                "취소");
            AssertButtonsUseStyle(thumbnailWindow, Assert.IsType<Style>(thumbnailWindow.FindResource("PrimaryActionButtonStyle")), "다시 생성", "선택");
            AssertButtonsUseStyle(thumbnailWindow, Assert.IsType<Style>(thumbnailWindow.FindResource("SecondaryActionButtonStyle")), "취소");

            AssertRounded(FindButton(metadataWindow, "취소"));
        }
        finally
        {
            thumbnailWindow.Close();
            metadataWindow.Close();
            mainWindow.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task MetadataFooterButtons_UseSharedStyleNaturalHeight() => WpfTestHost.RunAsync(() =>
    {
        var window = new MetadataEditorWindow(CreateVideoItem());

        try
        {
            Assert.True(double.IsNaN(window.TryParseOn141JavButton.Height));
            Assert.True(double.IsNaN(window.CancelButton.Height));
            Assert.True(double.IsNaN(window.SaveButton.Height));
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task SearchAndSortControls_UseGalleryNativeChrome() => WpfTestHost.RunAsync(() =>
    {
        var window = new MainWindow();
        try
        {
            var search = Assert.IsType<Border>(window.FindName("SearchBorder"));
            Assert.Equal(new CornerRadius(7), search.CornerRadius); Assert.Equal(39, search.Height);
            Assert.Equal(Color.FromRgb(0x18, 0x1C, 0x1E), Assert.IsType<SolidColorBrush>(search.Background).Color);
            var sort = Assert.IsType<ComboBox>(window.FindName("SortPicker")); sort.ApplyTemplate();
            Assert.NotNull(sort.Template.FindName("PART_Popup", sort));
            Assert.Equal(6, window.ViewModel.SortOptions.Count);
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    });
    [Fact]
    public Task MetadataFields_UseRoundedInputChrome() => WpfTestHost.RunAsync(() =>
    {
        var window = new MetadataEditorWindow(CreateVideoItem());

        try
        {
            var inputBoxStyle = Assert.IsType<Style>(window.TryFindResource("InputBoxStyle"));
            var textBoxes = FindDescendants<TextBox>(window)
                .Where(textBox => ReferenceEquals(textBox.Style, inputBoxStyle))
                .ToArray();

            Assert.Equal(4, textBoxes.Length);
            foreach (var textBox in textBoxes)
            {
                textBox.ApplyTemplate();
                var border = Assert.IsType<Border>(
                    textBox.Template.FindName("border", textBox));
                Assert.Equal(new CornerRadius(14), border.CornerRadius);
            }

            var datePickerStyle = Assert.IsType<Style>(window.TryFindResource("DatePickerStyle"));
            var datePicker = Assert.Single(
                FindDescendants<DatePicker>(window),
                picker => ReferenceEquals(picker.Style, datePickerStyle));
            datePicker.ApplyTemplate();
            var datePickerBorder = Assert.IsType<Border>(
                VisualTreeHelper.GetChild(datePicker, 0));
            Assert.Equal(new CornerRadius(14), datePickerBorder.CornerRadius);
        }
        finally
        {
            window.Close();
        }

        return Task.CompletedTask;
    });

    private static void AssertBrushSetter(
        Style style,
        DependencyProperty property,
        Color expectedColor)
    {
        var setter = Assert.Single(
            style.Setters.OfType<Setter>(),
            candidate => candidate.Property == property);
        var brush = Assert.IsType<SolidColorBrush>(setter.Value);
        Assert.Equal(expectedColor, brush.Color);
    }

    private static void AssertButtonsUseStyle(
        DependencyObject root,
        Style expectedStyle,
        params string[] labels)
    {
        foreach (var label in labels)
        {
            Assert.Same(expectedStyle, FindButton(root, label).Style);
        }
    }

    private static void AssertRounded(Button button)
    {
        button.ApplyTemplate();
        var border = Assert.IsType<Border>(
            button.Template.FindName("ActionButtonBorder", button));
        Assert.Equal(new CornerRadius(14), border.CornerRadius);
    }

    private static Button FindButton(DependencyObject root, string label) =>
        Assert.Single(FindDescendants<Button>(root), button => Equals(button.Content, label));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static VideoItem CreateVideoItem() => new()
    {
        Title = "Sample",
        Description = string.Empty,
        Actors = [],
        Tags = []
    };

    private static VideoThumbnailCandidate[] CreateCandidates()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg");
        return Enumerable.Range(1, 5)
            .Select(index => new VideoThumbnailCandidate(TimeSpan.FromSeconds(index), imagePath))
            .ToArray();
    }
}
