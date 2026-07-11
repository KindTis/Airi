using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Infrastructure;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class AppStartupTests
{
    [Fact]
    public Task AppStartup_DelayedFactoryCompletesAfterExit_DoesNotCreateWindow() => WpfTestHost.RunAsync(async () =>
    {
        var completion = new TaskCompletionSource<IThumbnailImageLoader>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = 0;
        var coordinator = new AppStartupCoordinator(
            Application.Current.Dispatcher,
            token => completion.Task.WaitAsync(token),
            loader =>
            {
                windows++;
                return new Window();
            },
            _ => { },
            _ => { });

        var startup = coordinator.StartAsync();
        coordinator.Cancel();
        completion.TrySetResult(new FakeLoader());
        await startup;

        Assert.Equal(0, windows);
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    });

    [Fact]
    public Task AppStartup_ConcurrentStartRequests_CreateOneWindow() => WpfTestHost.RunAsync(async () =>
    {
        var completion = new TaskCompletionSource<IThumbnailImageLoader>(TaskCreationOptions.RunContinuationsAsynchronously);
        var windows = 0;
        Window? created = null;
        var coordinator = new AppStartupCoordinator(
            Application.Current.Dispatcher,
            _ => completion.Task,
            _ =>
            {
                windows++;
                created = new Window();
                return created;
            },
            _ => { },
            _ => { });

        var first = coordinator.StartAsync();
        var second = coordinator.StartAsync();
        completion.SetResult(new FakeLoader());
        await Task.WhenAll(first, second);

        Assert.Equal(1, windows);
        Assert.NotNull(created);
        Assert.True(created!.IsVisible);
        created.Close();
    });

    [Fact]
    public Task AppStartup_FactoryFault_LogsOnceAndShutsDown() => WpfTestHost.RunAsync(async () =>
    {
        var logs = 0;
        var shutdowns = 0;
        var coordinator = new AppStartupCoordinator(
            Application.Current.Dispatcher,
            _ => Task.FromException<IThumbnailImageLoader>(new InvalidOperationException("boom")),
            _ => new Window(),
            _ => logs++,
            _ => shutdowns++);

        await coordinator.StartAsync();

        Assert.Equal(1, logs);
        Assert.Equal(1, shutdowns);
        Assert.Empty(Application.Current.Windows.Cast<Window>());
    });

    private sealed class FakeLoader : IThumbnailImageLoader
    {
        public FakeLoader()
        {
            var pixels = Enumerable.Repeat((byte)0, 4).ToArray();
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            bitmap.Freeze();
            FallbackSource = bitmap;
        }

        public ImageSource FallbackSource { get; }

        public Task<ThumbnailImageResult> LoadAsync(string thumbnailPath, int decodePixelWidth, CancellationToken cancellationToken) =>
            Task.FromResult(new ThumbnailImageResult(FallbackSource, true));
    }
}
