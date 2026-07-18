using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;

namespace Airi.Services.VideoPreview;

internal sealed class MediaProcessRunner : IMediaProcessRunner
{
    private readonly IMediaProcessObserver _observer;
    private readonly int _captureByteLimit;

    public MediaProcessRunner(
        IMediaProcessObserver? observer = null,
        int captureByteLimit = 64 * 1024)
    {
        if (captureByteLimit <= 0) throw new ArgumentOutOfRangeException(nameof(captureByteLimit));
        _observer = observer ?? NullMediaProcessObserver.Instance;
        _captureByteLimit = captureByteLimit;
    }

    public async Task<MediaProcessResult> RunAsync(
        MediaProcessRequest request,
        Func<Stream, CancellationToken, Task> readOutputAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(readOutputAsync);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = new Process { StartInfo = CreateStartInfo(request) };
        if (!process.Start())
        {
            throw new InvalidOperationException("Media process could not be started.");
        }

        var identity = new MediaProcessIdentity(
            Path.GetFullPath(process.StartInfo.FileName),
            process.Id,
            process.StartTime.ToUniversalTime());
        _observer.Started(identity);

        var stderr = new CappedDrain(_captureByteLimit);
        var stdoutTask = ConsumeOutputAsync(process, readOutputAsync, cancellationToken);
        var stderrTask = stderr.ReadAsync(process.StandardError.BaseStream);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        using var cancellationRegistration = cancellationToken.Register(
            static state => TryKill((Process)state!),
            process);

        Exception? failure = null;
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask, exitTask).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            if (!process.HasExited)
            {
                TryKill(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            _observer.Exited(identity, process.HasExited ? process.ExitCode : null);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return new MediaProcessResult(process.ExitCode, stderr.CapturedByteCount, stderr.Overflowed);
    }

    private static ProcessStartInfo CreateStartInfo(MediaProcessRequest request)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (request.RawArguments is not null)
        {
            startInfo.Arguments = request.RawArguments;
        }
        else
        {
            foreach (var argument in request.ArgumentList)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        return startInfo;
    }

    private static async Task ConsumeOutputAsync(
        Process process,
        Func<Stream, CancellationToken, Task> readOutputAsync,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await readOutputAsync(process.StandardOutput.BaseStream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
            TryKill(process);
        }

        try
        {
            await process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (IOException) when (process.HasExited)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed class CappedDrain
    {
        private readonly int _limit;

        public CappedDrain(int limit) => _limit = limit;

        public int CapturedByteCount { get; private set; }
        public bool Overflowed { get; private set; }

        public async Task ReadAsync(Stream stream)
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (read == 0) return;
                var remaining = _limit - CapturedByteCount;
                if (remaining > 0)
                {
                    CapturedByteCount += Math.Min(remaining, read);
                }
                if (read > remaining)
                {
                    Overflowed = true;
                }
            }
        }
    }
}
