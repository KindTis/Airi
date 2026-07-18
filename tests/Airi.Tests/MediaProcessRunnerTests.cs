using System.Diagnostics;
using System.IO;
using Airi.Services.VideoPreview;

namespace Airi.Tests;

public sealed class MediaProcessRunnerTests
{
    [Fact]
    public async Task Cancellation_ObservesProcessExitBeforeRunCompletes()
    {
        var observer = new RecordingProcessObserver();
        var runner = new MediaProcessRunner(observer, captureByteLimit: 4096);
        using var cancellation = new CancellationTokenSource();
        var request = MediaProcessRequest.Create(
            PowerShellPath,
            new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" });

        var run = runner.RunAsync(request, DrainAsync, cancellation.Token);
        var started = await observer.StartedTask.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var exited = await observer.ExitedTask.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await run);
        Assert.Equal(started, exited.Identity);
        Assert.False(IsRunning(started.ProcessId, started.StartTimeUtc));
    }

    [Fact]
    public async Task Overflow_DrainsBothPipesAndCapsCapturedStderr()
    {
        var observer = new RecordingProcessObserver();
        var runner = new MediaProcessRunner(observer, captureByteLimit: 4096);
        var request = MediaProcessRequest.Create(
            PowerShellPath,
            new[]
            {
                "-NoProfile",
                "-Command",
                "$s='AIRI_SENTINEL_' + ('X' * 200000); [Console]::Out.Write($s); [Console]::Error.Write($s)"
            });

        var result = await runner.RunAsync(request, DrainAsync, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StderrOverflowed);
        Assert.Equal(4096, result.CapturedStderrByteCount);
        Assert.Equal(1, observer.StartCount);
        Assert.Equal(1, observer.ExitCount);
    }

    [Fact]
    public async Task OutputReaderFailure_KillsProcessAndObservesExitBeforeCompletion()
    {
        var observer = new RecordingProcessObserver();
        var runner = new MediaProcessRunner(observer, captureByteLimit: 4096);
        var request = MediaProcessRequest.Create(
            PowerShellPath,
            new[]
            {
                "-NoProfile",
                "-Command",
                "$chunk='X' * 8192; while ($true) { [Console]::Out.Write($chunk); [Console]::Error.Write($chunk) }"
            });

        var run = runner.RunAsync(request, RejectAfterFirstReadAsync, CancellationToken.None);
        var started = await observer.StartedTask.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await run.WaitAsync(TimeSpan.FromSeconds(5)));
        var exited = await observer.ExitedTask.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(started, exited.Identity);
        Assert.False(IsRunning(started.ProcessId, started.StartTimeUtc));
    }

    private static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell",
        "v1.0",
        "powershell.exe");

    private static Task DrainAsync(Stream stream, CancellationToken cancellationToken) =>
        stream.CopyToAsync(Stream.Null, cancellationToken);

    private static async Task RejectAfterFirstReadAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        if (await stream.ReadAsync(buffer, cancellationToken) == 0)
        {
            throw new EndOfStreamException();
        }
        throw new InvalidDataException("Output exceeded the test limit.");
    }

    private static bool IsRunning(int processId, DateTime startTimeUtc)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime() == startTimeUtc && !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class RecordingProcessObserver : IMediaProcessObserver
    {
        private readonly TaskCompletionSource<MediaProcessIdentity> _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<ExitObservation> _exited =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MediaProcessIdentity> StartedTask => _started.Task;
        public Task<ExitObservation> ExitedTask => _exited.Task;
        public int StartCount { get; private set; }
        public int ExitCount { get; private set; }

        public void Started(MediaProcessIdentity identity)
        {
            StartCount++;
            _started.TrySetResult(identity);
        }

        public void Exited(MediaProcessIdentity identity, int? exitCode)
        {
            ExitCount++;
            _exited.TrySetResult(new ExitObservation(identity, exitCode));
        }
    }

    private sealed record ExitObservation(MediaProcessIdentity Identity, int? ExitCode);
}
