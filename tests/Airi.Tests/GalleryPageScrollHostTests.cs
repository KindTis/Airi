using Airi.Controls;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class GalleryPageScrollHostTests
{
    [Theory]
    [InlineData(700, 850, true, 2, 66, 1.4)]
    [InlineData(701, 850, false, 2, 64, 2.15)]
    [InlineData(1230, 851, false, 2, 76, 1.9)]
    [InlineData(1231, 851, false, 3, 76, 1.9)]
    public void Metrics_UseClientBreakpoints(double width, double height, bool compact, int columns, double header, double ratio)
    {
        var metrics = GalleryLayoutMetrics.Calculate(width, height, width - (compact ? 0 : 170), false);
        Assert.Equal(compact, metrics.Compact); Assert.Equal(columns, metrics.Columns); Assert.Equal(header, metrics.HeaderHeight); Assert.Equal(ratio, metrics.CardWidth / metrics.CoverHeight, 5);
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
