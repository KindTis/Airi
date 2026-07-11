using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Airi.Infrastructure;

internal interface IThumbnailBitmapDecoder
{
    ImageSource Decode(Stream stream, int decodePixelWidth);
}

internal readonly record struct ThumbnailImageLoaderDiagnostics(
    int CacheMissRequestCount,
    int FileOpenCount,
    int LastFileOpenThreadId,
    int LastDecodeThreadId,
    ThreadPriority LastDecodeThreadPriority,
    int ActiveDecodeCount,
    int MaxConcurrentDecodeCount,
    int CacheEntryCount,
    int RecencyNodeCount,
    int FileChangeRetryAttemptCount,
    int FallbackInitializationThreadId,
    long FallbackInitializationElapsedTicks);

internal readonly record struct ThumbnailFileStamp(DateTime LastWriteUtc, long Length);

internal readonly record struct ThumbnailCacheKey(
    string AbsolutePath,
    int DecodePixelWidth,
    DateTime LastWriteUtc,
    long Length);

internal sealed class ThumbnailCacheKeyComparer : IEqualityComparer<ThumbnailCacheKey>
{
    public static ThumbnailCacheKeyComparer Instance { get; } = new();

    public bool Equals(ThumbnailCacheKey x, ThumbnailCacheKey y) =>
        StringComparer.OrdinalIgnoreCase.Equals(x.AbsolutePath, y.AbsolutePath) &&
        x.DecodePixelWidth == y.DecodePixelWidth &&
        x.LastWriteUtc == y.LastWriteUtc &&
        x.Length == y.Length;

    public int GetHashCode(ThumbnailCacheKey key) =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(key.AbsolutePath),
            key.DecodePixelWidth,
            key.LastWriteUtc,
            key.Length);
}

internal enum ThumbnailFailureReason
{
    MissingFile,
    CorruptOrUnsupportedImage,
    UnsupportedUri,
    FileChangedTwice
}

internal readonly record struct ThumbnailFailureRecord(
    string FailureKey,
    ThumbnailFailureReason Reason,
    Exception? Exception);

public sealed class ThumbnailImageLoader : IThumbnailImageLoader
{
    private sealed record CacheEntry(
        ImageSource Source,
        LinkedListNode<ThumbnailCacheKey> Node,
        long OwnerIdentity);

    private sealed class BitmapDecoder : IThumbnailBitmapDecoder
    {
        public ImageSource Decode(Stream stream, int decodePixelWidth)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = decodePixelWidth;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
    }

    private readonly object _cacheSync = new();
    private readonly object _failureSync = new();
    private readonly Dictionary<ThumbnailCacheKey, CacheEntry> _cache = new(ThumbnailCacheKeyComparer.Instance);
    private readonly LinkedList<ThumbnailCacheKey> _recency = new();
    private readonly HashSet<string> _loggedFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _decodeGate;
    private readonly IThumbnailBitmapDecoder _decoder;
    private readonly ThumbnailPerformanceProbe _performanceProbe;
    private readonly Action<ThumbnailFailureRecord>? _failureSink;
    private readonly string _fallbackPath;
    private readonly int _capacity;
    private readonly int _fallbackInitializationThreadId;
    private readonly long _fallbackInitializationElapsedTicks;
    private long _nextDecodedOwnerIdentity;
    private int _cacheMissRequestCount;
    private int _fileOpenCount;
    private int _lastFileOpenThreadId;
    private int _lastDecodeThreadId;
    private int _lastDecodeThreadPriority;
    private int _activeDecodeCount;
    private int _maxConcurrentDecodeCount;
    private int _fileChangeRetryAttemptCount;

    internal ThumbnailImageLoader(
        ImageSource fallbackSource,
        IThumbnailBitmapDecoder? decoder,
        ThumbnailPerformanceProbe performanceProbe,
        Action<ThumbnailFailureRecord>? failureSink,
        string fallbackPath,
        int capacity,
        int maxConcurrency,
        int fallbackInitializationThreadId = 0,
        long fallbackInitializationElapsedTicks = 0)
    {
        ArgumentNullException.ThrowIfNull(fallbackSource);
        ArgumentNullException.ThrowIfNull(performanceProbe);
        if (!fallbackSource.IsFrozen)
        {
            throw new ArgumentException("Thumbnail fallback source must be frozen.", nameof(fallbackSource));
        }
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        }

        FallbackSource = fallbackSource;
        _decoder = decoder ?? new BitmapDecoder();
        _performanceProbe = performanceProbe;
        _failureSink = failureSink;
        _fallbackPath = NormalizeLocalPath(fallbackPath);
        _capacity = capacity;
        _decodeGate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        _fallbackInitializationThreadId = fallbackInitializationThreadId;
        _fallbackInitializationElapsedTicks = fallbackInitializationElapsedTicks;
    }

    public ImageSource FallbackSource { get; }

    public static Task<ThumbnailImageLoader> CreateAsync(CancellationToken cancellationToken) =>
        CreateAsync(ThumbnailPerformanceProbe.Disabled, cancellationToken);

    internal static Task<ThumbnailImageLoader> CreateAsync(
        ThumbnailPerformanceProbe performanceProbe,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(performanceProbe);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            var workerThreadId = Environment.CurrentManagedThreadId;
            var fallbackPath = LibraryPathHelper.ResolveToAbsolute("resources/noimage.jpg");
            ImageSource fallback;
            try
            {
                using var stream = new FileStream(
                    fallbackPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                fallback = new BitmapDecoder().Decode(stream, 520);
            }
            catch (Exception ex) when (IsExpectedImageFailure(ex))
            {
                fallback = CreateGeneratedFallback();
            }

            var elapsedTicks = Math.Max(1, Stopwatch.GetTimestamp() - started);
            performanceProbe.RecordFallbackInitialization(elapsedTicks, workerThreadId);

            return new ThumbnailImageLoader(
                fallback,
                decoder: null,
                performanceProbe,
                failureSink: failure => AppLogger.Error(
                    $"Thumbnail load failed ({failure.Reason}): {failure.FailureKey}",
                    failure.Exception),
                fallbackPath,
                capacity: 96,
                maxConcurrency: 4,
                fallbackInitializationThreadId: workerThreadId,
                fallbackInitializationElapsedTicks: elapsedTicks);
        }, cancellationToken);
    }

    internal static ThumbnailImageLoader CreateWithGeneratedFallback() =>
        new(
            CreateGeneratedFallback(),
            decoder: null,
            ThumbnailPerformanceProbe.Disabled,
            failureSink: null,
            LibraryPathHelper.ResolveToAbsolute("resources/noimage.jpg"),
            capacity: 96,
            maxConcurrency: 4,
            fallbackInitializationThreadId: Environment.CurrentManagedThreadId);

    public async Task<ThumbnailImageResult> LoadAsync(
        string thumbnailPath,
        int decodePixelWidth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var absolutePath = TryNormalizeSourcePath(thumbnailPath, out var unsupportedFailureKey);
        if (absolutePath is null)
        {
            if (unsupportedFailureKey is not null)
            {
                RecordFailure(unsupportedFailureKey, ThumbnailFailureReason.UnsupportedUri, null);
            }
            return FallbackResult;
        }

        if (StringComparer.OrdinalIgnoreCase.Equals(absolutePath, _fallbackPath))
        {
            return FallbackResult;
        }

        decodePixelWidth = Math.Clamp(decodePixelWidth, 64, 960);
        var initialStamp = ReadMetadata(absolutePath);
        if (initialStamp is null)
        {
            RecordFailure(absolutePath, ThumbnailFailureReason.MissingFile, null);
            return FallbackResult;
        }

        var initialKey = CreateKey(absolutePath, decodePixelWidth, initialStamp.Value);
        if (TryGetAndTouch(initialKey, out var initialCached))
        {
            return new ThumbnailImageResult(initialCached, false);
        }

        Interlocked.Increment(ref _cacheMissRequestCount);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _decodeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var startStamp = ReadMetadata(absolutePath);
                if (startStamp is null)
                {
                    RecordFailure(absolutePath, ThumbnailFailureReason.MissingFile, null);
                    return FallbackResult;
                }

                var key = CreateKey(absolutePath, decodePixelWidth, startStamp.Value);
                if (TryGetAndTouch(key, out var cached))
                {
                    return new ThumbnailImageResult(cached, false);
                }

                ImageSource decoded;
                try
                {
                    decoded = await DecodeOffUiThreadAsync(
                        absolutePath,
                        decodePixelWidth,
                        isFileChangeRetryAttempt: attempt > 0,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (IsExpectedImageFailure(ex))
                {
                    var reason = ex is FileNotFoundException or DirectoryNotFoundException
                        ? ThumbnailFailureReason.MissingFile
                        : ThumbnailFailureReason.CorruptOrUnsupportedImage;
                    RecordFailure(absolutePath, reason, ex);
                    return FallbackResult;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var endStamp = ReadMetadata(absolutePath);
                if (endStamp == startStamp)
                {
                    var selected = InsertOrGetExistingAndEvict(key, decoded);
                    return new ThumbnailImageResult(selected, false);
                }
            }
            finally
            {
                _decodeGate.Release();
            }
        }

        RecordFailure(absolutePath, ThumbnailFailureReason.FileChangedTwice, null);
        return FallbackResult;
    }

    internal ThumbnailImageLoaderDiagnostics GetDiagnostics()
    {
        int cacheCount;
        int recencyCount;
        lock (_cacheSync)
        {
            cacheCount = _cache.Count;
            recencyCount = _recency.Count;
        }

        return new ThumbnailImageLoaderDiagnostics(
            Volatile.Read(ref _cacheMissRequestCount),
            Volatile.Read(ref _fileOpenCount),
            Volatile.Read(ref _lastFileOpenThreadId),
            Volatile.Read(ref _lastDecodeThreadId),
            (ThreadPriority)Volatile.Read(ref _lastDecodeThreadPriority),
            Volatile.Read(ref _activeDecodeCount),
            Volatile.Read(ref _maxConcurrentDecodeCount),
            cacheCount,
            recencyCount,
            Volatile.Read(ref _fileChangeRetryAttemptCount),
            _fallbackInitializationThreadId,
            _fallbackInitializationElapsedTicks);
    }

    private ThumbnailImageResult FallbackResult => new(FallbackSource, true);

    private async Task<ImageSource> DecodeOffUiThreadAsync(
        string absolutePath,
        int decodePixelWidth,
        bool isFileChangeRetryAttempt,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isFileChangeRetryAttempt)
            {
                Interlocked.Increment(ref _fileChangeRetryAttemptCount);
            }

            Interlocked.Increment(ref _fileOpenCount);
            Volatile.Write(ref _lastFileOpenThreadId, Environment.CurrentManagedThreadId);
            using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            var active = Interlocked.Increment(ref _activeDecodeCount);
            UpdateMaximum(ref _maxConcurrentDecodeCount, active);
            try
            {
                Volatile.Write(ref _lastDecodeThreadId, Environment.CurrentManagedThreadId);
                Volatile.Write(ref _lastDecodeThreadPriority, (int)Thread.CurrentThread.Priority);
                var source = _decoder.Decode(stream, decodePixelWidth);
                if (!source.IsFrozen)
                {
                    if (!source.CanFreeze)
                    {
                        throw new InvalidOperationException("Decoded thumbnail source cannot be frozen.");
                    }
                    source.Freeze();
                }
                return source;
            }
            finally
            {
                Interlocked.Decrement(ref _activeDecodeCount);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ThumbnailCacheKey CreateKey(
        string absolutePath,
        int decodePixelWidth,
        ThumbnailFileStamp stamp) =>
        new(absolutePath, decodePixelWidth, stamp.LastWriteUtc, stamp.Length);

    private ThumbnailFileStamp? ReadMetadata(string absolutePath)
    {
        try
        {
            var file = new FileInfo(absolutePath);
            if (!file.Exists)
            {
                return null;
            }
            return new ThumbnailFileStamp(file.LastWriteTimeUtc, file.Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private bool TryGetAndTouch(ThumbnailCacheKey key, out ImageSource source)
    {
        lock (_cacheSync)
        {
            if (!_cache.TryGetValue(key, out var entry))
            {
                source = null!;
                return false;
            }

            _recency.Remove(entry.Node);
            _recency.AddFirst(entry.Node);
            AssertCacheInvariants();
            source = entry.Source;
            return true;
        }
    }

    private ImageSource InsertOrGetExistingAndEvict(ThumbnailCacheKey key, ImageSource source)
    {
        lock (_cacheSync)
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                _recency.Remove(existing.Node);
                _recency.AddFirst(existing.Node);
                AssertCacheInvariants();
                return existing.Source;
            }

            var node = _recency.AddFirst(key);
            var ownerIdentity = Interlocked.Increment(ref _nextDecodedOwnerIdentity);
            _cache.Add(key, new CacheEntry(source, node, ownerIdentity));
            _performanceProbe.EnterDecodedStrongReferenceOwner(
                ThumbnailDecodedOwnerKind.LoaderCache,
                ownerIdentity);
            while (_cache.Count > _capacity)
            {
                var tail = _recency.Last ?? throw new InvalidOperationException("LRU recency list is empty during eviction.");
                _recency.RemoveLast();
                if (_cache.Remove(tail.Value, out var evicted))
                {
                    _performanceProbe.LeaveDecodedStrongReferenceOwner(
                        ThumbnailDecodedOwnerKind.LoaderCache,
                        evicted.OwnerIdentity);
                }
            }
            AssertCacheInvariants();
            return source;
        }
    }

    private void AssertCacheInvariants()
    {
        Debug.Assert(_cache.Count == _recency.Count);
        Debug.Assert(_cache.Count <= _capacity);
        if (_cache.Count != _recency.Count || _cache.Count > _capacity)
        {
            throw new InvalidOperationException("Thumbnail LRU invariants were violated.");
        }
    }

    private string? TryNormalizeSourcePath(string? thumbnailPath, out string? unsupportedFailureKey)
    {
        unsupportedFailureKey = null;
        if (string.IsNullOrWhiteSpace(thumbnailPath))
        {
            return null;
        }

        var trimmed = thumbnailPath.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            unsupportedFailureKey = "uri:" + trimmed.ToUpperInvariant();
            return null;
        }

        try
        {
            return NormalizeLocalPath(trimmed);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            unsupportedFailureKey = "path:" + trimmed;
            return null;
        }
    }

    private static string NormalizeLocalPath(string path) =>
        Path.GetFullPath(LibraryPathHelper.ResolveToAbsolute(path));

    private void RecordFailure(string key, ThumbnailFailureReason reason, Exception? exception)
    {
        bool added;
        lock (_failureSync)
        {
            added = _loggedFailures.Add(key);
        }
        if (added)
        {
            _failureSink?.Invoke(new ThumbnailFailureRecord(key, reason, exception));
        }
    }

    private static ImageSource CreateGeneratedFallback()
    {
        var bitmap = BitmapSource.Create(
            1,
            1,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            new byte[4],
            4);
        bitmap.Freeze();
        return bitmap;
    }

    private static bool IsExpectedImageFailure(Exception exception) =>
        exception is IOException or
        UnauthorizedAccessException or
        NotSupportedException or
        ArgumentException or
        FileFormatException;

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
            {
                return;
            }
        }
    }
}
