using System.IO;

namespace Airi.Infrastructure;

internal static class PathCleanup
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromMilliseconds(500)
    ];

    internal static Task<Exception?> DeleteFileAsync(string path) =>
        DeleteAsync(path, File.Exists, File.Delete, Task.Delay);

    internal static Task<Exception?> DeleteDirectoryAsync(string path) =>
        DeleteAsync(
            path,
            Directory.Exists,
            candidate => Directory.Delete(candidate, recursive: true),
            Task.Delay);

    internal static async Task<Exception?> DeleteAsync(
        string path,
        Func<string, bool> exists,
        Action<string> delete,
        Func<TimeSpan, Task> delayAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(delete);
        ArgumentNullException.ThrowIfNull(delayAsync);

        Exception? lastFailure = null;
        for (var attempt = 0; attempt <= RetryDelays.Length; attempt++)
        {
            try
            {
                if (!exists(path))
                {
                    return null;
                }

                delete(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastFailure = ex;
            }

            if (attempt < RetryDelays.Length)
            {
                await delayAsync(RetryDelays[attempt]).ConfigureAwait(false);
            }
        }

        return lastFailure;
    }
}
