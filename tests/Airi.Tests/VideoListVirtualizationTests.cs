using System.Windows.Controls;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VideoListVirtualizationTests
{
    [Fact]
    public void PackageContract_UsesHorizontalVirtualizingWrapPanel()
    {
        WpfTestHost.Run(() =>
        {
            var panel = new WpfToolkit.Controls.VirtualizingWrapPanel
            {
                Orientation = Orientation.Horizontal,
                SpacingMode = WpfToolkit.Controls.SpacingMode.None,
                StretchItems = false
            };

            Assert.Equal(Orientation.Horizontal, panel.Orientation);
            Assert.Equal(WpfToolkit.Controls.SpacingMode.None, panel.SpacingMode);
            Assert.False(panel.StretchItems);
        });
    }

    [Fact]
    public void ProductionVideoList_EnablesRecyclingPixelVirtualization()
    {
        WpfTestHost.Run(() =>
        {
            var window = new MainWindow();
            var list = window.GetVideoListForTests();

            Assert.True(ScrollViewer.GetCanContentScroll(list));
            Assert.True(VirtualizingPanel.GetIsVirtualizing(list));
            Assert.Equal(VirtualizationMode.Recycling, VirtualizingPanel.GetVirtualizationMode(list));
            Assert.Equal(ScrollUnit.Pixel, VirtualizingPanel.GetScrollUnit(list));
            Assert.Equal(new VirtualizationCacheLength(1, 1), VirtualizingPanel.GetCacheLength(list));
            Assert.Equal(VirtualizationCacheLengthUnit.Page, VirtualizingPanel.GetCacheLengthUnit(list));

            var panel = Assert.IsType<WpfToolkit.Controls.VirtualizingWrapPanel>(list.ItemsPanel.LoadContent());
            Assert.Equal(Orientation.Horizontal, panel.Orientation);
            Assert.Equal(WpfToolkit.Controls.SpacingMode.None, panel.SpacingMode);
            Assert.False(panel.StretchItems);
            window.Close();
        });
    }

    [Fact]
    public void HorizontalPanel_WrapsAtRightEdgeAndScrollsVertically()
    {
        WpfTestHost.Run(() =>
        {
            var list = CreateMeasuredList();
            var window = new Window
            {
                Width = 1200,
                Height = 820,
                Content = list,
                SizeToContent = SizeToContent.Manual
            };
            window.Show();
            window.UpdateLayout();
            WpfTestHost.DrainDispatcher(window.Dispatcher);

            var containers = Enumerable.Range(0, list.Items.Count)
                .Select(index => list.ItemContainerGenerator.ContainerFromIndex(index))
                .OfType<ListBoxItem>()
                .ToArray();
            Assert.NotEmpty(containers);
            var firstY = containers[0].TranslatePoint(new Point(), list).Y;
            var firstWrapped = containers.First(container =>
                container.TranslatePoint(new Point(), list).Y > firstY + 1);
            var scroll = FindVisualChild<ScrollViewer>(list);

            Assert.True(firstWrapped.TranslatePoint(new Point(), list).Y > firstY);
            Assert.NotNull(scroll);
            Assert.True(scroll!.ExtentHeight > scroll.ViewportHeight);
            window.Close();
        });
    }

    [Fact]
    public Task Virtualization_TwoHundredItems_TopMiddleLastStayWithinCalculatedLimit() =>
        AssertVirtualizationBoundsAsync(200);

    [Fact]
    public Task Virtualization_OneThousandItems_TopMiddleLastStayWithinCalculatedLimit() =>
        AssertVirtualizationBoundsAsync(1000);

    [Fact]
    public Task Virtualization_MediumAndStressSteadyCountsDifferByAtMostOneGuardRow() =>
        WpfTestHost.RunAsync(async () =>
        {
            var medium = await MeasurePositionsAsync(200);
            var stress = await MeasurePositionsAsync(1000);
            Assert.True(Math.Abs(stress.Top - medium.Top) <= medium.Columns);
            Assert.True(Math.Abs(stress.Middle - medium.Middle) <= medium.Columns);
            Assert.True(Math.Abs(stress.Last - medium.Last) <= medium.Columns);
        });

    [Fact]
    public Task Virtualization_FirstLastRoundTrip_PreservesSelectionAndItemSource() =>
        WpfTestHost.RunAsync(async () =>
        {
            var list = CreateMeasuredList(1000);
            var source = list.ItemsSource;
            var window = CreateWindow(list);
            list.SelectedIndex = 0;
            await ScrollToAsync(list, 999);
            Assert.Same(source, list.ItemsSource);
            Assert.Equal(0, list.SelectedIndex);
            await ScrollToAsync(list, 0);
            Assert.Same(source, list.ItemsSource);
            Assert.Equal(0, list.SelectedIndex);
            window.Close();
        });

    private static async Task AssertVirtualizationBoundsAsync(int itemCount)
    {
        await WpfTestHost.RunAsync(async () =>
        {
            var measurement = await MeasurePositionsAsync(itemCount);
            Assert.InRange(measurement.Top, 1, measurement.HardLimit);
            Assert.InRange(measurement.Middle, 1, measurement.HardLimit);
            Assert.InRange(measurement.Last, 1, measurement.HardLimit);
            Assert.InRange(measurement.Maximum, 1, measurement.HardLimit);
        });
    }

    private static async Task<(int Top, int Middle, int Last, int Maximum, int Columns, int HardLimit)>
        MeasurePositionsAsync(int itemCount)
    {
        var list = CreateMeasuredList(itemCount);
        var window = CreateWindow(list);
        var top = await MeasureAtAsync(list, 0);
        var first = Enumerable.Range(0, list.Items.Count)
            .Select(index => list.ItemContainerGenerator.ContainerFromIndex(index))
            .OfType<ListBoxItem>()
            .First();
        var scroll = Assert.IsType<ScrollViewer>(FindVisualChild<ScrollViewer>(list));
        var itemWidth = Math.Max(1, first.DesiredSize.Width);
        var itemHeight = Math.Max(1, first.DesiredSize.Height);
        var columns = Math.Max(1, (int)Math.Floor(scroll.ViewportWidth / itemWidth));
        var visibleRows = Math.Max(1, (int)Math.Ceiling(scroll.ViewportHeight / itemHeight));
        var hardLimit = ((3 * visibleRows) + 1) * columns;
        var middle = await MeasureAtAsync(list, itemCount / 2);
        var last = await MeasureAtAsync(list, itemCount - 1);
        var maximum = Math.Max(top, Math.Max(middle, last));
        window.Close();
        return (top, middle, last, maximum, columns, hardLimit);
    }

    private static Window CreateWindow(ListBox list)
    {
        var window = new Window
        {
            Width = 1200,
            Height = 820,
            Content = list,
            SizeToContent = SizeToContent.Manual
        };
        window.Show();
        window.UpdateLayout();
        WpfTestHost.DrainDispatcher(window.Dispatcher);
        return window;
    }

    private static async Task<int> MeasureAtAsync(ListBox list, int index)
    {
        await ScrollToAsync(list, index);
        var first = CountRealized(list);
        await Task.Delay(500);
        WpfTestHost.DrainDispatcher(list.Dispatcher);
        var second = CountRealized(list);
        Assert.Equal(first, second);
        return second;
    }

    private static async Task ScrollToAsync(ListBox list, int index)
    {
        list.ScrollIntoView(list.Items[index]);
        list.UpdateLayout();
        await list.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        WpfTestHost.DrainDispatcher(list.Dispatcher);
    }

    private static int CountRealized(ListBox list) =>
        Enumerable.Range(0, list.Items.Count)
            .Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem);

    private static ListBox CreateMeasuredList(int itemCount = 40)
    {
        var panelFactory = new FrameworkElementFactory(typeof(WpfToolkit.Controls.VirtualizingWrapPanel));
        panelFactory.SetValue(WpfToolkit.Controls.VirtualizingWrapPanel.OrientationProperty, Orientation.Horizontal);
        panelFactory.SetValue(WpfToolkit.Controls.VirtualizingWrapPanel.SpacingModeProperty, WpfToolkit.Controls.SpacingMode.None);
        panelFactory.SetValue(WpfToolkit.Controls.VirtualizingWrapPanel.StretchItemsProperty, false);
        var itemFactory = new FrameworkElementFactory(typeof(Border));
        itemFactory.SetValue(FrameworkElement.WidthProperty, 284d);
        itemFactory.SetValue(FrameworkElement.HeightProperty, 274d);

        var list = new ListBox
        {
            Width = 1200,
            Height = 820,
            ItemsSource = Enumerable.Range(0, itemCount),
            ItemsPanel = new ItemsPanelTemplate(panelFactory),
            ItemTemplate = new DataTemplate { VisualTree = itemFactory }
        };
        ScrollViewer.SetCanContentScroll(list, true);
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(list, ScrollBarVisibility.Auto);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        VirtualizingPanel.SetCacheLength(list, new VirtualizationCacheLength(1, 1));
        VirtualizingPanel.SetCacheLengthUnit(list, VirtualizationCacheLengthUnit.Page);
        return list;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }
}
