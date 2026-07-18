using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Airi.Services.VideoPreview;

internal sealed class RawVideoFramePipe
{
    private readonly int _frameByteCount;
    private readonly int _frameRate;
    private readonly int _readyFrameCount;
    private readonly Channel<VideoPreviewFrame> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RawVideoFramePipe(int frameByteCount, int frameRate, int capacity, int readyFrameCount)
    {
        if (frameByteCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameByteCount));
        if (frameRate <= 0) throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (readyFrameCount <= 0 || readyFrameCount > capacity) throw new ArgumentOutOfRangeException(nameof(readyFrameCount));

        _frameByteCount = frameByteCount;
        _frameRate = frameRate;
        _readyFrameCount = readyFrameCount;
        _channel = Channel.CreateBounded<VideoPreviewFrame>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    }

    public Task Ready => _ready.Task;

    public async Task ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cancellation.Token);
        var token = linked.Token;
        Exception? failure = null;
        var frameIndex = 0;
        try
        {
            while (true)
            {
                var bytes = new byte[_frameByteCount];
                var offset = 0;
                while (offset < bytes.Length)
                {
                    var read = await stream.ReadAsync(bytes.AsMemory(offset), token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        if (offset == 0)
                        {
                            if (frameIndex < _readyFrameCount)
                            {
                                throw new EndOfStreamException("Video stream ended before the preview was ready.");
                            }
                            return;
                        }
                        throw new EndOfStreamException("Video stream ended inside a raw frame.");
                    }
                    offset += read;
                }

                var frame = new VideoPreviewFrame(
                    bytes,
                    TimeSpan.FromSeconds(frameIndex / (double)_frameRate));
                await _channel.Writer.WriteAsync(frame, token).ConfigureAwait(false);
                frameIndex++;
                if (frameIndex == _readyFrameCount)
                {
                    _ready.TrySetResult();
                }
            }
        }
        catch (Exception ex)
        {
            failure = ex;
            _ready.TrySetException(ex);
            throw;
        }
        finally
        {
            _channel.Writer.TryComplete(failure);
        }
    }

    public async IAsyncEnumerable<VideoPreviewFrame> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var frame in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return frame;
        }
    }

    public void Cancel() => _cancellation.Cancel();
}
