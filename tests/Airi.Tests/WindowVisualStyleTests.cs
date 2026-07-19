using System.IO;
using System.Windows;
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

            Assert.Same(customWindowStyle, mainWindow.Style);
            var titleBarBrush = Assert.IsType<SolidColorBrush>(mainWindow.BorderBrush);
            Assert.Equal(Color.FromRgb(0x15, 0x18, 0x28), titleBarBrush.Color);
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
