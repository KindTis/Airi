using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Infrastructure;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class ThumbnailImageLoaderTests
{
    [Fact]
    public Task LoadAsync_LargeJpeg_DecodesAtOrBelowRequestedWidth() => Run(async fixture =>
    {
        var path = fixture.CreateImage("large.jpg", 1200, 800, 0x31);
        var loader = fixture.CreateLoader();
        var result = await loader.LoadAsync(path, 234, CancellationToken.None);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(result.Source);
        Assert.InRange(bitmap.PixelWidth, 1, 234);
        Assert.False(result.IsFallback);
    });

    [Fact]
    public Task LoadAsync_TooltipWidthAboveCardLimit_DecodesRequestedWidth() => Run(async fixture =>
    {
        var path = fixture.CreateImage("tooltip.jpg", 1200, 800, 0x42);
        var loader = fixture.CreateLoader();
        var result = await loader.LoadAsync(path, 720, CancellationToken.None);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(result.Source);
        Assert.InRange(bitmap.PixelWidth, 521, 720);
        Assert.False(result.IsFallback);
    });

    [Fact]
    public Task LoadAsync_ReturnsFrozenSource() => Run(async fixture =>
    {
        var result = await fixture.CreateLoader().LoadAsync(
            fixture.CreateImage("frozen.jpg", 320, 200, 0x32), 200, CancellationToken.None);
        Assert.True(result.Source.IsFrozen);
    });

    [Fact]
    public Task LoadAsync_AfterCompletion_ReleasesSourceFileHandle() => Run(async fixture =>
    {
        var path = fixture.CreateImage("handle.jpg", 320, 200, 0x33);
        await fixture.CreateLoader().LoadAsync(path, 200, CancellationToken.None);
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(exclusive.CanRead);
    });

    [Fact]
    public Task LoadAsync_MissingFile_ReturnsSharedFallback() => Run(async fixture =>
    {
        var loader = fixture.CreateLoader();
        var result = await loader.LoadAsync(fixture.PathFor("missing.jpg"), 200, CancellationToken.None);
        Assert.True(result.IsFallback);
        Assert.Same(loader.FallbackSource, result.Source);
    });

    [Fact]
    public Task LoadAsync_CorruptFile_ReturnsSharedFallback() => Run(async fixture =>
    {
        var path = fixture.CreateBytes("corrupt.jpg", new byte[] { 1, 2, 3, 4 });
        var loader = fixture.CreateLoader();
        var result = await loader.LoadAsync(path, 200, CancellationToken.None);
        Assert.True(result.IsFallback);
        Assert.Same(loader.FallbackSource, result.Source);
    });

    [Fact]
    public Task LoadAsync_Cancelled_ThrowsOperationCanceledException() => Run(async fixture =>
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            fixture.CreateLoader().LoadAsync(fixture.CreateImage("cancel.jpg", 100, 80, 0x34), 100, cts.Token));
        Assert.Empty(fixture.Failures);
    });

    [Fact]
    public Task CreateAsync_FallbackIsFrozenStableAndCreatedOffDispatcher() => WpfTestHost.RunAsync(async () =>
    {
        var callerThread = Environment.CurrentManagedThreadId;
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
        probe.TryMark(StartupTimingMarker.StartupMeasurementBegin);
        var loader = await ThumbnailImageLoader.CreateAsync(probe, CancellationToken.None);
        var diagnostics = loader.GetDiagnostics();
        var snapshot = probe.EndMeasurementPhase();
        Assert.True(loader.FallbackSource.IsFrozen);
        Assert.Same(loader.FallbackSource, loader.FallbackSource);
        Assert.NotEqual(callerThread, diagnostics.FallbackInitializationThreadId);
        Assert.True(diagnostics.FallbackInitializationElapsedTicks > 0);
        Assert.NotNull(snapshot.FallbackInitialization);
        Assert.Equal(diagnostics.FallbackInitializationThreadId, snapshot.FallbackInitialization.Value.ThreadId);
        Assert.Equal(diagnostics.FallbackInitializationElapsedTicks, snapshot.FallbackInitialization.Value.ElapsedTicks);
    });

    [Fact]
    public Task LoadAsync_DecodeRunsOffStaCallerThreadAtNormalOrLowerPriority() => Run(async fixture =>
    {
        var caller = Environment.CurrentManagedThreadId;
        var loader = fixture.CreateLoader();
        await loader.LoadAsync(fixture.CreateImage("thread.jpg", 200, 100, 0x35), 100, CancellationToken.None);
        var diagnostics = loader.GetDiagnostics();
        Assert.NotEqual(caller, diagnostics.LastFileOpenThreadId);
        Assert.Equal(diagnostics.LastFileOpenThreadId, diagnostics.LastDecodeThreadId);
        Assert.True(diagnostics.LastDecodeThreadPriority <= ThreadPriority.Normal);
    });

    [Fact]
    public Task LoadAsync_TwentyConcurrentMisses_MaxDecodeConcurrencyIsFour() => Run(async fixture =>
    {
        using var release = new ManualResetEventSlim(false);
        var decoder = new DelegateDecoder((_, _) =>
        {
            release.Wait(TimeSpan.FromSeconds(10));
            return fixture.CreateFrozenBitmap(16, 10, 0x36);
        });
        var loader = fixture.CreateLoader(decoder);
        var paths = Enumerable.Range(0, 20)
            .Select(index => fixture.CreateBytes($"parallel-{index}.bin", new byte[] { (byte)index, 1 }))
            .ToArray();
        var tasks = paths.Select(path => loader.LoadAsync(path, 100, CancellationToken.None)).ToArray();
        await fixture.WaitUntilAsync(() => loader.GetDiagnostics().MaxConcurrentDecodeCount == 4);
        release.Set();
        await Task.WhenAll(tasks);
        Assert.Equal(4, loader.GetDiagnostics().MaxConcurrentDecodeCount);
    });

    [Fact]
    public Task LoadAsync_SameKeyTwice_SecondCallIsCacheHitWithoutFileOpen() => Run(async fixture =>
    {
        var path = fixture.CreateImage("cache.jpg", 200, 100, 0x37);
        var loader = fixture.CreateLoader();
        var first = await loader.LoadAsync(path, 100, CancellationToken.None);
        var second = await loader.LoadAsync(path, 100, CancellationToken.None);
        Assert.Same(first.Source, second.Source);
        Assert.Equal(1, loader.GetDiagnostics().FileOpenCount);
        Assert.Equal(1, loader.GetDiagnostics().CacheMissRequestCount);
    });

    [Fact]
    public Task LoadAsync_NinetySevenKeys_EvictsToNinetySixEntries() => Run(async fixture =>
    {
        var probe = ThumbnailPerformanceProbe.CreateEnabled();
        var loader = fixture.CreateLoader(
            new DelegateDecoder((_, _) => fixture.CreateFrozenBitmap(8, 8, 0x38)),
            performanceProbe: probe);
        for (var index = 0; index < 97; index++)
        {
            await loader.LoadAsync(fixture.CreateBytes($"lru-{index}.bin", new byte[] { (byte)index, 1 }), 100, CancellationToken.None);
        }
        var diagnostics = loader.GetDiagnostics();
        Assert.Equal(96, diagnostics.CacheEntryCount);
        Assert.Equal(diagnostics.CacheEntryCount, diagnostics.RecencyNodeCount);
        Assert.Equal(96, probe.GetDecodedStrongReferenceOwnerCount());
    });

    [Fact]
    public Task LoadAsync_LastWriteChanges_DoesNotReuseOldEntry() => Run(async fixture =>
    {
        var path = fixture.CreateImage("timestamp.jpg", 200, 100, 0x39);
        var loader = fixture.CreateLoader();
        await loader.LoadAsync(path, 100, CancellationToken.None);
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(5));
        await loader.LoadAsync(path, 100, CancellationToken.None);
        Assert.Equal(2, loader.GetDiagnostics().FileOpenCount);
    });

    [Fact]
    public Task LoadAsync_LengthChangesWithPreservedTimestamp_DoesNotReuseOldEntry() => Run(async fixture =>
    {
        var path = fixture.CreateBytes("length.bin", new byte[] { 1, 2 });
        var stamp = File.GetLastWriteTimeUtc(path);
        var loader = fixture.CreateLoader(new DelegateDecoder((_, _) => fixture.CreateFrozenBitmap(8, 8, 0x3a)));
        await loader.LoadAsync(path, 100, CancellationToken.None);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5 });
        File.SetLastWriteTimeUtc(path, stamp);
        await loader.LoadAsync(path, 100, CancellationToken.None);
        Assert.Equal(2, loader.GetDiagnostics().FileOpenCount);
    });

    [Fact]
    public Task LoadAsync_SourceIsFallbackPath_ReturnsFallbackWithoutSuccessCacheEntry() => Run(async fixture =>
    {
        var loader = fixture.CreateLoader(fallbackPath: fixture.FallbackPath);
        var result = await loader.LoadAsync(fixture.FallbackPath, 100, CancellationToken.None);
        Assert.True(result.IsFallback);
        Assert.Equal(0, loader.GetDiagnostics().CacheEntryCount);
        Assert.Empty(fixture.Failures);
    });

    [Fact]
    public Task LoadAsync_RelativeLibraryPathUsesApplicationBaseDirectory() => Run(async fixture =>
    {
        var name = $"thumbnail-loader-{Guid.NewGuid():N}.jpg";
        var absolute = Path.Combine(AppContext.BaseDirectory, name);
        try
        {
            fixture.CreateImageAt(absolute, 120, 80, 0x3b);
            var result = await fixture.CreateLoader().LoadAsync(name, 100, CancellationToken.None);
            Assert.False(result.IsFallback);
        }
        finally
        {
            File.Delete(absolute);
        }
    });

    [Fact]
    public Task LoadAsync_SameFileDifferentPathCasing_UsesOneCacheEntryAndOneFileOpen() => Run(async fixture =>
    {
        var path = fixture.CreateImage("CaseSensitiveName.jpg", 100, 80, 0x3c);
        var alternate = path.ToUpperInvariant();
        var loader = fixture.CreateLoader();
        await loader.LoadAsync(path, 100, CancellationToken.None);
        await loader.LoadAsync(alternate, 100, CancellationToken.None);
        var diagnostics = loader.GetDiagnostics();
        Assert.Equal(1, diagnostics.FileOpenCount);
        Assert.Equal(1, diagnostics.CacheEntryCount);
    });

    [Fact]
    public Task LoadAsync_SameCorruptPathRepeatedAndConcurrent_LogsOncePerSession() => Run(async fixture =>
    {
        var path = fixture.CreateBytes("bad.bin", new byte[] { 1, 2, 3 });
        var loader = fixture.CreateLoader();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => loader.LoadAsync(path, 100, CancellationToken.None)));
        await loader.LoadAsync(path, 100, CancellationToken.None);
        Assert.Single(fixture.Failures);
        Assert.Equal(ThumbnailFailureReason.CorruptOrUnsupportedImage, fixture.Failures.Single().Reason);
    });

    [Fact]
    public Task LoadAsync_DifferentFailingPaths_LogOnceEach() => Run(async fixture =>
    {
        var loader = fixture.CreateLoader();
        await loader.LoadAsync(fixture.PathFor("missing-a.jpg"), 100, CancellationToken.None);
        await loader.LoadAsync(fixture.PathFor("missing-b.jpg"), 100, CancellationToken.None);
        Assert.Equal(2, fixture.Failures.Count);
    });

    [Fact]
    public Task LoadAsync_TwoHundredParallelKeys_PreservesLruInvariants() => Run(async fixture =>
    {
        var loader = fixture.CreateLoader(new DelegateDecoder((_, _) => fixture.CreateFrozenBitmap(8, 8, 0x3d)));
        var tasks = Enumerable.Range(0, 200)
            .Select(index => loader.LoadAsync(
                fixture.CreateBytes($"stress-{index}.bin", BitConverter.GetBytes(index)), 100, CancellationToken.None))
            .ToArray();
        await Task.WhenAll(tasks);
        var diagnostics = loader.GetDiagnostics();
        Assert.InRange(diagnostics.MaxConcurrentDecodeCount, 1, 4);
        Assert.Equal(diagnostics.CacheEntryCount, diagnostics.RecencyNodeCount);
        Assert.InRange(diagnostics.CacheEntryCount, 1, 96);
    });

    [Fact]
    public Task LoadAsync_FileChangesTwice_ReturnsFallbackWithoutSuccessEntry() => Run(async fixture =>
    {
        var path = fixture.CreateBytes("changing.bin", new byte[] { 1, 1 });
        var decodeCount = 0;
        var decoder = new DelegateDecoder((_, _) =>
        {
            var replacement = fixture.PathFor($"change-{Interlocked.Increment(ref decodeCount)}.tmp");
            File.WriteAllBytes(replacement, Enumerable.Repeat((byte)decodeCount, decodeCount + 3).ToArray());
            ReplaceOpenFile(replacement, path);
            return fixture.CreateFrozenBitmap(8, 8, (byte)decodeCount);
        });
        var loader = fixture.CreateLoader(decoder);
        var result = await loader.LoadAsync(path, 100, CancellationToken.None);
        Assert.True(result.IsFallback);
        Assert.Equal(0, loader.GetDiagnostics().CacheEntryCount);
        Assert.Single(fixture.Failures);
        Assert.Equal(ThumbnailFailureReason.FileChangedTwice, fixture.Failures.Single().Reason);
    });

    [Fact]
    public Task LoadAsync_OpenFileReplacement_WithDifferentStamp_DiscardsOldPixelsAndRetries() => Run(async fixture =>
    {
        var path = fixture.CreateBytes("replace.bin", new byte[] { 0x11, 1 });
        var replacement = fixture.CreateBytes("replace-new.tmp", new byte[] { 0x77, 2, 3, 4, 5 });
        using var opened = new ManualResetEventSlim(false);
        using var resume = new ManualResetEventSlim(false);
        var attempt = 0;
        var decoder = new DelegateDecoder((stream, _) =>
        {
            var value = stream.ReadByte();
            if (Interlocked.Increment(ref attempt) == 1)
            {
                opened.Set();
                resume.Wait(TimeSpan.FromSeconds(10));
            }
            return fixture.CreateFrozenBitmap(1, 1, (byte)value);
        });
        var loader = fixture.CreateLoader(decoder);
        var load = loader.LoadAsync(path, 100, CancellationToken.None);
        Assert.True(opened.Wait(TimeSpan.FromSeconds(10)));
        ReplaceOpenFile(replacement, path);
        resume.Set();
        var result = await load;
        Assert.False(result.IsFallback);
        Assert.Equal(0x77, fixture.ReadFirstPixel(result.Source));
        Assert.Equal(2, loader.GetDiagnostics().FileOpenCount);
    });

    [Fact]
    public Task LoadAsync_FileChangeRetryCountersMatchActualAttempts() => Run(async fixture =>
    {
        var path = fixture.CreateBytes("counter.bin", new byte[] { 1, 2 });
        var changed = 0;
        var decoder = new DelegateDecoder((_, _) =>
        {
            if (Interlocked.Exchange(ref changed, 1) == 0)
            {
                var replacement = fixture.CreateBytes("counter-new.tmp", new byte[] { 9, 8, 7, 6 });
                ReplaceOpenFile(replacement, path);
            }
            return fixture.CreateFrozenBitmap(8, 8, 0x3e);
        });
        var loader = fixture.CreateLoader(decoder);
        await loader.LoadAsync(path, 100, CancellationToken.None);
        var diagnostics = loader.GetDiagnostics();
        Assert.Equal(2, diagnostics.FileOpenCount);
        Assert.Equal(1, diagnostics.FileChangeRetryAttemptCount);
        Assert.Equal(1, diagnostics.CacheMissRequestCount);
    });

    private static Task Run(Func<Fixture, Task> action) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await action(fixture);
    });

    private static void ReplaceOpenFile(string replacement, string destination)
    {
        File.Delete(destination);
        File.Move(replacement, destination);
    }

    private sealed class DelegateDecoder : IThumbnailBitmapDecoder
    {
        private readonly Func<Stream, int, ImageSource> _decode;
        public DelegateDecoder(Func<Stream, int, ImageSource> decode) => _decode = decode;
        public ImageSource Decode(Stream stream, int decodePixelWidth) => _decode(stream, decodePixelWidth);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "AiriThumbnailLoaderTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            FallbackSource = CreateFrozenBitmap(1, 1, 0);
            FallbackPath = CreateImage("fallback.jpg", 4, 4, 0);
        }

        public ImageSource FallbackSource { get; }
        public string FallbackPath { get; }
        public ConcurrentBag<ThumbnailFailureRecord> Failures { get; } = new();

        public ThumbnailImageLoader CreateLoader(
            IThumbnailBitmapDecoder? decoder = null,
            string? fallbackPath = null,
            ThumbnailPerformanceProbe? performanceProbe = null) =>
            new(
                FallbackSource,
                decoder,
                performanceProbe ?? ThumbnailPerformanceProbe.Disabled,
                Failures.Add,
                fallbackPath ?? FallbackPath,
                capacity: 96,
                maxConcurrency: 4);

        public string PathFor(string fileName) => Path.Combine(_root, fileName);

        public string CreateBytes(string fileName, byte[] bytes)
        {
            var path = PathFor(fileName);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public string CreateImage(string fileName, int width, int height, byte value)
        {
            var path = PathFor(fileName);
            CreateImageAt(path, width, height, value);
            return path;
        }

        public void CreateImageAt(string path, int width, int height, byte value)
        {
            var bitmap = CreateFrozenBitmap(width, height, value);
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create((BitmapSource)bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }

        public ImageSource CreateFrozenBitmap(int width, int height, byte value)
        {
            var pixels = Enumerable.Repeat(value, width * height * 4).ToArray();
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        public byte ReadFirstPixel(ImageSource source)
        {
            var bitmap = Assert.IsAssignableFrom<BitmapSource>(source);
            var pixels = new byte[4];
            bitmap.CopyPixels(pixels, 4, 0);
            return pixels[0];
        }

        public async Task WaitUntilAsync(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!predicate() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(10);
            }
            Assert.True(predicate(), "Condition was not met before timeout.");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
