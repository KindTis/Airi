using System.IO;
using System.Windows.Media.Imaging;
using Airi.Services;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VideoThumbnailExtractorIntegrationTests
{
    [Fact]
    public async Task ExtractAsync_BundledFfmpegCreatesFiveOrderedJpegs()
    {
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            "Airi.Tests",
            Guid.NewGuid().ToString("N"));
        var extractor = new VideoThumbnailExtractor(BinaryFolder);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        try
        {
            var candidates = await extractor.ExtractAsync(
                FixturePath("h264.mp4"),
                outputDirectory,
                new Random(1979),
                timeout.Token);

            Assert.Equal(5, candidates.Count);
            Assert.Equal(candidates.OrderBy(candidate => candidate.Timestamp), candidates);
            foreach (var candidate in candidates)
            {
                Assert.True(File.Exists(candidate.FilePath));
                Assert.True(new FileInfo(candidate.FilePath).Length > 0);

                await using var stream = File.OpenRead(candidate.FilePath);
                var decoder = new JpegBitmapDecoder(
                    stream,
                    BitmapCreateOptions.PreservePixelFormat,
                    BitmapCacheOption.OnLoad);
                var frame = Assert.Single(decoder.Frames);
                Assert.InRange(frame.PixelWidth, 1, 1280);
                Assert.Equal(16d / 9d, (double)frame.PixelWidth / frame.PixelHeight, precision: 2);
            }
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    private static string BinaryFolder => Path.Combine(
        AppContext.BaseDirectory,
        "resources",
        "ffmpeg",
        "win-x64");

    private static string FixturePath(string fileName) => Path.Combine(
        AppContext.BaseDirectory,
        "TestData",
        "VideoPreview",
        fileName);
}
