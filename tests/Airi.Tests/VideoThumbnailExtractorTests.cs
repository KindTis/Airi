using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using Airi.Services;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class VideoThumbnailExtractorTests
{
    [Fact]
    public void SelectTimestamps_PicksOneOrderedValueFromEachFivePercentTrimmedSegment()
    {
        var timestamps = VideoThumbnailExtractor.SelectTimestamps(
            TimeSpan.FromSeconds(100),
            new Random(1979));

        Assert.Equal(5, timestamps.Count);
        Assert.Equal(timestamps.OrderBy(timestamp => timestamp), timestamps);
        for (var index = 0; index < timestamps.Count; index++)
        {
            Assert.InRange(
                timestamps[index],
                TimeSpan.FromSeconds(5 + (18 * index)),
                TimeSpan.FromSeconds(5 + (18 * (index + 1))));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SelectTimestamps_RejectsNonPositiveDuration(double seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            VideoThumbnailExtractor.SelectTimestamps(
                TimeSpan.FromSeconds(seconds),
                new Random(1979)));
    }

    [Fact]
    public async Task ExtractAsync_RunsFiveJpegRequestsWithMaximumConcurrencyTwo()
    {
        var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
        var extractor = new VideoThumbnailExtractor("C:\\bundle", runner);
        var outputDirectory = CreateTemporaryDirectory();

        try
        {
            var candidates = await extractor.ExtractAsync(
                "C:\\secret folder\\movie.mp4",
                outputDirectory,
                new Random(1979),
                CancellationToken.None);

            Assert.Equal(1, runner.ProbeCount);
            Assert.Equal(5, runner.FfmpegStartedCount);
            Assert.Equal(2, runner.MaximumActiveFfmpegCalls);
            Assert.Equal(5, candidates.Count);
            Assert.Equal(candidates.OrderBy(candidate => candidate.Timestamp), candidates);

            var requestsByOutput = runner.FfmpegRequests
                .OrderBy(request => request.ArgumentList[^1], StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(5, requestsByOutput.Length);
            for (var index = 0; index < requestsByOutput.Length; index++)
            {
                var request = requestsByOutput[index];
                var arguments = request.ArgumentList;
                Assert.EndsWith("ffmpeg.exe", request.FileName, StringComparison.OrdinalIgnoreCase);
                Assert.Null(request.RawArguments);
                Assert.Equal(16, arguments.Count);
                Assert.Equal("-hide_banner", arguments[0]);
                Assert.Equal("-loglevel", arguments[1]);
                Assert.Equal("error", arguments[2]);
                Assert.Equal("-ss", arguments[3]);
                Assert.Equal("-i", arguments[5]);
                Assert.Equal("C:\\secret folder\\movie.mp4", arguments[6]);
                Assert.Equal(new[] { "-frames:v", "1", "-an", "-sn", "-dn" }, arguments.Skip(7).Take(5));
                Assert.Equal("-vf", arguments[12]);
                Assert.Equal("scale=w='min(1280,iw)':h=-2", arguments[13]);
                Assert.Equal("-y", arguments[14]);
                Assert.Equal(
                    Path.Combine(outputDirectory, $"candidate-{index + 1:D2}.jpg"),
                    arguments[15]);
                Assert.Equal(
                    candidates[index].Timestamp.TotalSeconds,
                    double.Parse(arguments[4], CultureInfo.InvariantCulture),
                    precision: 6);
                Assert.True(File.Exists(candidates[index].FilePath));
            }
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_OneFailureCancelsActiveAndQueuedSiblingsAndReturnsNoCandidates()
    {
        var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.ExitFailure);
        var extractor = new VideoThumbnailExtractor("C:\\bundle", runner);
        var outputDirectory = CreateTemporaryDirectory();

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                extractor.ExtractAsync(
                    "C:\\movie.mp4",
                    outputDirectory,
                    new Random(1979),
                    CancellationToken.None));

            Assert.Contains("23", failure.Message, StringComparison.Ordinal);
            Assert.Equal(1, runner.ProbeCount);
            Assert.Equal(2, runner.FfmpegStartedCount);
            Assert.True(runner.CanceledActiveCount >= 1);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(ThumbnailRunnerBehavior.MissingOutput)]
    [InlineData(ThumbnailRunnerBehavior.EmptyOutput)]
    public async Task ExtractAsync_MissingOrEmptyOutputFailsWholeBatch(
        ThumbnailRunnerBehavior behavior)
    {
        var runner = new ThumbnailMediaProcessRunner(behavior);
        var extractor = new VideoThumbnailExtractor("C:\\bundle", runner);
        var outputDirectory = CreateTemporaryDirectory();

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                extractor.ExtractAsync(
                    "C:\\movie.mp4",
                    outputDirectory,
                    new Random(1979),
                    CancellationToken.None));

            Assert.Equal(2, runner.FfmpegStartedCount);
            Assert.True(runner.CanceledActiveCount >= 1);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_ProcessStartFailurePreservesOriginalException()
    {
        var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.StartFailure);
        var extractor = new VideoThumbnailExtractor("C:\\bundle", runner);
        var outputDirectory = CreateTemporaryDirectory();

        try
        {
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                extractor.ExtractAsync(
                    "C:\\movie.mp4",
                    outputDirectory,
                    new Random(1979),
                    CancellationToken.None));

            Assert.Same(runner.ProcessStartFailure, failure);
            Assert.True(runner.CanceledActiveCount >= 1);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractAsync_UserCancellationPreservesCancellation()
    {
        var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.BlockUntilCanceled);
        var extractor = new VideoThumbnailExtractor("C:\\bundle", runner);
        var outputDirectory = CreateTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();

        try
        {
            var extraction = extractor.ExtractAsync(
                "C:\\movie.mp4",
                outputDirectory,
                new Random(1979),
                cancellation.Token);
            await runner.TwoFfmpegStarted.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extraction);
            Assert.Equal(2, runner.FfmpegStartedCount);
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Airi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public enum ThumbnailRunnerBehavior
{
    Success,
    ExitFailure,
    MissingOutput,
    EmptyOutput,
    StartFailure,
    BlockUntilCanceled
}

internal sealed class ThumbnailMediaProcessRunner : IMediaProcessRunner
{
    private static readonly byte[] ProbeOutput = Encoding.UTF8.GetBytes(
        "{\"format\":{\"duration\":\"100\",\"format_name\":\"mov,mp4\"}," +
        "\"streams\":[{\"codec_type\":\"video\",\"width\":1920,\"height\":1080,\"codec_name\":\"h264\"}]}");

    private readonly ThumbnailRunnerBehavior _behavior;
    private readonly TaskCompletionSource _twoFfmpegStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeFfmpegCalls;
    private int _maximumActiveFfmpegCalls;
    private int _probeCount;
    private int _ffmpegStartedCount;
    private int _canceledActiveCount;

    public ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior behavior) =>
        _behavior = behavior;

    public ConcurrentQueue<MediaProcessRequest> FfmpegRequests { get; } = new();
    public InvalidOperationException ProcessStartFailure { get; } =
        new("process start failed");
    public int ProbeCount => Volatile.Read(ref _probeCount);
    public int FfmpegStartedCount => Volatile.Read(ref _ffmpegStartedCount);
    public int MaximumActiveFfmpegCalls => Volatile.Read(ref _maximumActiveFfmpegCalls);
    public int CanceledActiveCount => Volatile.Read(ref _canceledActiveCount);
    public Task TwoFfmpegStarted => _twoFfmpegStarted.Task;

    public async Task<MediaProcessResult> RunAsync(
        MediaProcessRequest request,
        Func<Stream, CancellationToken, Task> readOutputAsync,
        CancellationToken cancellationToken)
    {
        if (Path.GetFileName(request.FileName).Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _probeCount);
            await using var output = new MemoryStream(ProbeOutput);
            await readOutputAsync(output, cancellationToken);
            return new MediaProcessResult(0, 0, false);
        }

        FfmpegRequests.Enqueue(request);
        var ordinal = Interlocked.Increment(ref _ffmpegStartedCount);
        var active = Interlocked.Increment(ref _activeFfmpegCalls);
        UpdateMaximum(active);
        if (ordinal == 2)
        {
            _twoFfmpegStarted.TrySetResult();
        }

        try
        {
            if (ordinal <= 2)
            {
                await _twoFfmpegStarted.Task.WaitAsync(cancellationToken);
            }

            if (ordinal == 1)
            {
                switch (_behavior)
                {
                    case ThumbnailRunnerBehavior.ExitFailure:
                        return new MediaProcessResult(23, 0, false);
                    case ThumbnailRunnerBehavior.MissingOutput:
                        return new MediaProcessResult(0, 0, false);
                    case ThumbnailRunnerBehavior.EmptyOutput:
                        await File.WriteAllBytesAsync(request.ArgumentList[^1], [], cancellationToken);
                        return new MediaProcessResult(0, 0, false);
                    case ThumbnailRunnerBehavior.StartFailure:
                        throw ProcessStartFailure;
                }
            }

            if (_behavior != ThumbnailRunnerBehavior.Success)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            await File.WriteAllBytesAsync(request.ArgumentList[^1], [1], cancellationToken);
            return new MediaProcessResult(0, 0, false);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _canceledActiveCount);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _activeFfmpegCalls);
        }
    }

    private void UpdateMaximum(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maximumActiveFfmpegCalls);
            if (candidate <= current ||
                Interlocked.CompareExchange(ref _maximumActiveFfmpegCalls, candidate, current) == current)
            {
                return;
            }
        }
    }
}
