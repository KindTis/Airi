using Airi.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class GalleryPageScrollHostTests
{
    [Theory]
    [InlineData(700, 850, true, 2, 66, 400d / 269d)]
    [InlineData(701, 850, false, 2, 64, 400d / 269d)]
    [InlineData(1230, 851, false, 2, 76, 400d / 269d)]
    [InlineData(1231, 851, false, 4, 76, 400d / 269d)]
    public void Metrics_UseClientBreakpoints(double width, double height, bool compact, int columns, double header, double ratio)
    {
        var metrics = GalleryLayoutMetrics.Calculate(width, height, width - (compact ? 0 : 170), false);
        Assert.Equal(compact, metrics.Compact); Assert.Equal(columns, metrics.Columns); Assert.Equal(header, metrics.HeaderHeight); Assert.Equal(ratio, metrics.CardWidth / metrics.CoverHeight, 5);
    }

    [Theory]
    [InlineData(1280, 820)]
    [InlineData(1507, 884)]
    public Task WideGallery_FillsFourActualColumnsAndUsesSampledCoverRatio(double width, double height) =>
        VerifyActualLayoutAsync(new[] { new Size(width, height) });

    [Fact]
    public Task SameGallery_ResizeKeepsColumnsRatioAndNonOverlappingCells() =>
        VerifyActualLayoutAsync(new[]
        {
            new Size(1280, 820), new Size(1554, 902), new Size(1100, 820),
            new Size(700, 850), new Size(1507, 884)
        });

    private static Task VerifyActualLayoutAsync(IReadOnlyList<Size> sizes) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new VideoDeletionFixture();
        using var viewModel = await fixture.CreateAsync();
        var window = new MainWindow(viewModel);
        try
        {
            var list = window.GetVideoListForTests();
            var placeholder = new BitmapImage();
            placeholder.BeginInit();
            placeholder.CacheOption = BitmapCacheOption.OnLoad;
            placeholder.UriSource = new Uri(System.IO.Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"));
            placeholder.EndInit();
            placeholder.Freeze();
            list.ItemsSource = Enumerable.Range(0, 80).Select(i =>
            {
                var item = new VideoItem { Title = $"Layout {i}" };
                item.CompleteThumbnailLoad(placeholder);
                return item;
            }).ToArray();
            var root = (FrameworkElement)window.Content;
            for (var sizeIndex = 0; sizeIndex < sizes.Count; sizeIndex++)
            {
                var size = sizes[sizeIndex];
                for (var pass = 0; pass < 3; pass++)
                {
                    root.Measure(size);
                    root.Arrange(new Rect(new Point(), size));
                    root.UpdateLayout();
                    WpfTestHost.DrainDispatcher(window.Dispatcher);
                }
                Assert.False(window.IsVisible);
                Assert.Null(PresentationSource.FromVisual(window));
                Assert.False(window.GalleryPopupIsOpenForTests);
                var metrics = window.GalleryHostForTests.Metrics;
                var expectedColumns = size.Width <= 1230 ? 2 : 4;
                Assert.Equal(expectedColumns, metrics.Columns);
                var panel = Assert.Single(VisualDescendants<WpfToolkit.Controls.VirtualizingWrapPanel>(list));
                var cards = VisualDescendants<ListBoxItem>(panel)
                    .OrderBy(item => list.ItemContainerGenerator.IndexFromContainer(item)).Take(expectedColumns + 1).ToArray();
                Assert.Equal(expectedColumns + 1, cards.Length);
                var first = cards[0].TranslatePoint(new Point(), panel);
                foreach (var card in cards.Take(expectedColumns))
                {
                    Assert.InRange(Math.Abs(card.TranslatePoint(new Point(), panel).Y - first.Y), 0, .01);
                    Assert.Equal(metrics.ColumnStride, card.ActualWidth, 5);
                    Assert.Equal(metrics.RowStride, card.ActualHeight, 5);
                }
                Assert.Equal(metrics.RowStride, cards[expectedColumns].TranslatePoint(new Point(), panel).Y - first.Y, 5);
                Assert.InRange(Math.Abs(panel.ActualWidth - expectedColumns * metrics.ColumnStride), 0, .05);
                // UniformToFill may render beyond its clip: the visible cover frame defines the ratio.
                var cover = Assert.Single(VisualDescendants<GalleryRoundedBorder>(cards[0]));
                Assert.True(cover.ActualWidth > 0 && cover.ActualHeight > 0);
                Assert.Equal(400d / 269d, cover.ActualWidth / cover.ActualHeight, 5);
                Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
                Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
                Assert.True(ScrollViewer.GetCanContentScroll(list));
                Assert.NotNull(((IScrollInfo)panel).ScrollOwner);
                if (sizeIndex == 0 && sizes.Count > 1)
                {
                    // Resizing at index zero refreshes the panel's implicit size cache; reproduce away from it.
                    ((IScrollInfo)panel).ScrollOwner.ScrollToVerticalOffset(8 * metrics.RowStride);
                    root.UpdateLayout();
                    WpfTestHost.DrainDispatcher(window.Dispatcher);
                    Assert.DoesNotContain(0, VisualDescendants<ListBoxItem>(panel)
                        .Select(item => list.ItemContainerGenerator.IndexFromContainer(item)));
                }
            }
        }
        finally { window.Close(); }
    });

    private static IEnumerable<T> VisualDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in VisualDescendants<T>(child)) yield return nested;
        }
    }

    [Fact]
    public Task CompactTenThousand_HasFiniteRealizationAndFooterAfterGrid() => WpfTestHost.RunAsync(async () =>
    {
        var item = new FrameworkElementFactory(typeof(Border)); item.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("Tag.Metrics.ColumnStride") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBox), 1) }); item.SetBinding(FrameworkElement.HeightProperty, new System.Windows.Data.Binding("Tag.Metrics.RowStride") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(ListBox), 1) });
        var panel = new FrameworkElementFactory(typeof(WpfToolkit.Controls.VirtualizingWrapPanel)); panel.SetValue(WpfToolkit.Controls.VirtualizingWrapPanel.OrientationProperty, Orientation.Horizontal); panel.SetValue(WpfToolkit.Controls.VirtualizingWrapPanel.SpacingModeProperty, WpfToolkit.Controls.SpacingMode.None);
        var list = new ListBox { BorderThickness = new Thickness(0), ItemsSource = Enumerable.Range(0, 10000), ItemTemplate = new DataTemplate { VisualTree = item }, ItemsPanel = new ItemsPanelTemplate(panel) };
        ScrollViewer.SetCanContentScroll(list, true); VirtualizingPanel.SetIsVirtualizing(list, true); VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling); VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel); VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(1, 1)); VirtualizingPanel.SetCacheLengthUnit(list, VirtualizationCacheLengthUnit.Page);
        var footer = new Border { Height = 31 }; var host = new GalleryPageScrollHost { Header = new Border { Height = 66 }, Prefix = new Border { Height = 230 }, List = list, Suffix = footer };
        list.Tag = host; var containerStyle = new Style(typeof(ListBoxItem)); containerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0))); containerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0))); list.ItemContainerStyle = containerStyle;
        var window = new Window { Width = 446, Height = 919, Content = new ScrollViewer { CanContentScroll = true, Content = host, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } }; WpfTestHost.Show(window); window.UpdateLayout(); WpfTestHost.DrainDispatcher(window.Dispatcher);
        foreach (var index in new[] { 0, 5000, 9999 }) { host.ScrollToIndex(index); window.UpdateLayout(); await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded); window.UpdateLayout(); WpfTestHost.DrainDispatcher(window.Dispatcher); var realized = Enumerable.Range(0, 10000).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null); Assert.InRange(realized, 1, 60); Assert.True(list.ItemContainerGenerator.ContainerFromIndex(index) != null, $"index={index}; page={host.VerticalOffset}/{host.ExtentHeight}; inner={GalleryPageScrollHost.FindScroll(list)?.VerticalOffset}/{GalleryPageScrollHost.FindScroll(list)?.ExtentHeight}; width={list.ActualWidth}; metrics={host.Metrics}; realized=" + string.Join(",", Enumerable.Range(0, 10000).Where(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null))); Assert.True(double.IsFinite(list.ActualHeight)); }
        Assert.True(footer.TranslatePoint(new Point(), host).Y < host.ActualHeight); window.Close();
    });
}
