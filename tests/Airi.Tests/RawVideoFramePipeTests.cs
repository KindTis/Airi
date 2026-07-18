using System.IO;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class RawVideoFramePipeTests
{
    [Fact]
    public async Task ReadAsync_PublishesWholeFramesAndSignalsReadyAfterThree()
    {
        var bytes = Enumerable.Range(0, 48).Select(value => (byte)value).ToArray();
        await using var stream = new MemoryStream(bytes);
        var pipe = new RawVideoFramePipe(frameByteCount: 16, frameRate: 15, capacity: 15, readyFrameCount: 3);

        await pipe.ReadAsync(stream, CancellationToken.None);
        var frames = new List<VideoPreviewFrame>();
        await foreach (var frame in pipe.ReadAllAsync(CancellationToken.None))
        {
            frames.Add(frame);
        }

        await pipe.Ready;
        Assert.Equal(3, frames.Count);
        Assert.Equal(TimeSpan.Zero, frames[0].PresentationTimestamp);
        Assert.Equal(TimeSpan.FromSeconds(2d / 15d), frames[2].PresentationTimestamp);
        Assert.All(frames, frame => Assert.Equal(16, frame.BgraPixels.Length));
    }

    [Fact]
    public async Task ReadAsync_RejectsTruncatedFinalFrameBeforeReady()
    {
        await using var stream = new MemoryStream(new byte[33]);
        var pipe = new RawVideoFramePipe(16, 15, 15, 3);

        await Assert.ThrowsAsync<EndOfStreamException>(() => pipe.ReadAsync(stream, CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(() => pipe.Ready);
    }

    [Fact]
    public async Task ReadAsync_BlocksProducerAtConfiguredCapacity()
    {
        await using var stream = new MemoryStream(new byte[48]);
        var pipe = new RawVideoFramePipe(16, 15, capacity: 1, readyFrameCount: 1);

        var read = pipe.ReadAsync(stream, CancellationToken.None);
        await pipe.Ready;
        Assert.False(read.IsCompleted);

        await using var enumerator = pipe.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        await enumerator.DisposeAsync();
        pipe.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }
}
