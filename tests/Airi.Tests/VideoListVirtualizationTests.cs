using System.Windows.Controls;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

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

    private static ListBox CreateMeasuredList()
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
            ItemsSource = Enumerable.Range(0, 40),
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
