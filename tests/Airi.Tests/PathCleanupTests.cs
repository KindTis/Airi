using Airi.Infrastructure;
using System.IO;

namespace Airi.Tests;

public sealed class PathCleanupTests
{
    [Fact]
    public async Task DeleteAsync_FirstThreeFailuresThenSuccess_UsesApprovedDelays()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var failure = await PathCleanup.DeleteAsync(
            "owned-path",
            _ => true,
            _ =>
            {
                if (++attempts < 4)
                {
                    throw new IOException($"failure-{attempts}");
                }
            },
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Null(failure);
        Assert.Equal(4, attempts);
        Assert.Equal(
            new[]
            {
                TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(250),
                TimeSpan.FromMilliseconds(500)
            },
            delays);
    }

    [Fact]
    public async Task DeleteAsync_AllFourAttemptsFail_ReturnsLastFailure()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var failure = await PathCleanup.DeleteAsync(
            "owned-path",
            _ => true,
            _ => throw new IOException($"failure-{++attempts}"),
            delay =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        Assert.Equal("failure-4", failure?.Message);
        Assert.Equal(4, attempts);
        Assert.Equal(3, delays.Count);
    }

    [Fact]
    public async Task DeleteAsync_MissingPath_DoesNotDeleteOrDelay()
    {
        var deleted = false;
        var delayed = false;
        var failure = await PathCleanup.DeleteAsync(
            "missing",
            _ => false,
            _ => deleted = true,
            _ =>
            {
                delayed = true;
                return Task.CompletedTask;
            });

        Assert.Null(failure);
        Assert.False(deleted);
        Assert.False(delayed);
    }

    [Fact]
    public async Task DeleteFileAsync_DeletesOwnedFile()
    {
        var root = CreateTemporaryDirectory();
        var path = Path.Combine(root, "owned.jpg");
        await File.WriteAllTextAsync(path, "owned");

        try
        {
            var failure = await PathCleanup.DeleteFileAsync(path);

            Assert.Null(failure);
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DeleteDirectoryAsync_DeletesOwnedRecursiveTree()
    {
        var root = CreateTemporaryDirectory();
        var child = Path.Combine(root, "child");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(Path.Combine(child, "owned.jpg"), "owned");

        var failure = await PathCleanup.DeleteDirectoryAsync(root);

        Assert.Null(failure);
        Assert.False(Directory.Exists(root));
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Airi.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
