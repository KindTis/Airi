using System.Windows;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class WpfTestHostTests
{
    [Fact]
    public Task Show_PlacesVisibleWindowOutsideVirtualScreenWithoutActivation() =>
        WpfTestHost.RunAsync(() =>
        {
            var window = new Window
            {
                Width = 120,
                Height = 80
            };

            try
            {
                WpfTestHost.Show(window);

                Assert.True(window.IsVisible);
                Assert.False(window.ShowActivated);
                Assert.False(window.ShowInTaskbar);
                Assert.Equal(WindowStartupLocation.Manual, window.WindowStartupLocation);
                Assert.True(window.Left + window.ActualWidth <= SystemParameters.VirtualScreenLeft);
                Assert.True(window.Top + window.ActualHeight <= SystemParameters.VirtualScreenTop);
            }
            finally
            {
                window.Close();
            }

            return Task.CompletedTask;
        });

    [Fact]
    public Task ShowDialog_PlacesVisibleWindowOutsideVirtualScreenWithoutActivation() =>
        WpfTestHost.RunAsync(() =>
        {
            var observedOffscreen = false;
            var window = new Window
            {
                Width = 120,
                Height = 80
            };
            window.Loaded += (_, _) =>
            {
                observedOffscreen =
                    window.IsVisible &&
                    !window.ShowActivated &&
                    !window.ShowInTaskbar &&
                    window.WindowStartupLocation == WindowStartupLocation.Manual &&
                    window.Left + window.ActualWidth <= SystemParameters.VirtualScreenLeft &&
                    window.Top + window.ActualHeight <= SystemParameters.VirtualScreenTop;
                window.Dispatcher.BeginInvoke(window.Close);
            };

            WpfTestHost.ShowDialog(window);

            Assert.True(observedOffscreen);
            return Task.CompletedTask;
        });
}
