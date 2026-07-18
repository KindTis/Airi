using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Airi.Infrastructure;

namespace Airi.Services.VideoPreview;

public sealed class FfmpegVideoPreviewService : IVideoPreviewService
{
    private readonly IFfmpegPreviewBackend _backend;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly VideoPreviewLogFormatter _logFormatter;

    internal FfmpegVideoPreviewService(FfmpegCorePreviewBackend backend)
        : this((IFfmpegPreviewBackend)backend)
    {
    }

    internal FfmpegVideoPreviewService(
        IFfmpegPreviewBackend backend,
        VideoPreviewLogFormatter? logFormatter = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _logFormatter = logFormatter ?? new VideoPreviewLogFormatter(RandomNumberGenerator.GetBytes(32));
    }

    public async Task<IPreparedVideoPreview> PrepareAsync(
        VideoPreviewRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var lease = await ProcessGateLease.AcquireAsync(_processGate, cancellationToken)
            .ConfigureAwait(false);
        FfmpegPreparedVideoPreview? prepared = null;
        VideoProbeResult? probe = null;
        try
        {
            probe = await _backend.ProbeAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
            if (!VideoPreviewMath.TryCreatePlan(request, probe, out var plan))
            {
                throw new VideoPreviewUnavailableException(
                    "Invalid media duration or video stream.",
                    "Plan",
                    "InvalidMedia",
                    null);
            }

            prepared = new FfmpegPreparedVideoPreview(
                _backend,
                request.SourcePath,
                plan,
                lease,
                cancellationToken);
            await prepared.StartAsync(cancellationToken).ConfigureAwait(false);
            return prepared;
        }
        catch (OperationCanceledException)
        {
            if (prepared is not null)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (prepared is not null)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            var unavailable = ex as VideoPreviewUnavailableException ??
                new VideoPreviewUnavailableException("Video preview preparation failed.", ex);
            var elapsed = Stopwatch.GetElapsedTime(started);
            AppLogger.Error(_logFormatter.FormatFailure(new VideoPreviewFailure(
                request.SourcePath,
                Path.GetExtension(request.SourcePath),
                probe?.Container ?? "unknown",
                probe?.Codec ?? "unknown",
                unavailable.Stage,
                unavailable.Result,
                unavailable.ExitCode,
                elapsed,
                TimeSpan.Zero,
                null)));
            throw unavailable;
        }
    }

    private sealed class FfmpegPreparedVideoPreview : IPreparedVideoPreview
    {
        private readonly IFfmpegPreviewBackend _backend;
        private readonly string _sourcePath;
        private readonly VideoPreviewPlan _plan;
        private readonly ProcessGateLease _lease;
        private readonly CancellationTokenSource _lifetime;
        private readonly RawVideoFramePipe _pipe;
        private readonly object _disposeSync = new();
        private Task? _processTask;
        private Task? _disposeTask;

        public FfmpegPreparedVideoPreview(
            IFfmpegPreviewBackend backend,
            string sourcePath,
            VideoPreviewPlan plan,
            ProcessGateLease lease,
            CancellationToken preparationToken)
        {
            _backend = backend;
            _sourcePath = sourcePath;
            _plan = plan;
            _lease = lease;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(preparationToken);
            _pipe = new RawVideoFramePipe(plan.FrameByteCount, plan.FrameRate, 15, 3);
        }

        public int PixelWidth => _plan.PixelWidth;
        public int PixelHeight => _plan.PixelHeight;
        public int FrameRate => _plan.FrameRate;
        public TimeSpan PlaybackDuration => _plan.PlaybackDuration;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _processTask = _backend.RunAsync(
                _sourcePath,
                _plan,
                _pipe.ReadAsync,
                _lifetime.Token);
            var completed = await Task.WhenAny(_pipe.Ready, _processTask).ConfigureAwait(false);
            if (completed == _processTask)
            {
                await _processTask.ConfigureAwait(false);
            }
            await _pipe.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        public IAsyncEnumerable<VideoPreviewFrame> ReadFramesAsync(CancellationToken cancellationToken) =>
            _pipe.ReadAllAsync(cancellationToken);

        public ValueTask DisposeAsync()
        {
            lock (_disposeSync)
            {
                _disposeTask ??= DisposeCoreAsync();
                return new ValueTask(_disposeTask);
            }
        }

        private async Task DisposeCoreAsync()
        {
            _lifetime.Cancel();
            _pipe.Cancel();
            if (_processTask is not null)
            {
                try
                {
                    await _processTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch (VideoPreviewUnavailableException)
                {
                }
                catch (EndOfStreamException)
                {
                }
                catch (IOException)
                {
                }
            }
            _lifetime.Dispose();
            await _lease.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class ProcessGateLease : IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        private ProcessGateLease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public static async Task<ProcessGateLease> AcquireAsync(
            SemaphoreSlim semaphore,
            CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProcessGateLease(semaphore);
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Exchange(ref _semaphore, null)?.Release();
            return ValueTask.CompletedTask;
        }
    }
}
