using System.Diagnostics;
using System.IO;
using Airi.Services;

namespace Airi.Tests;

public sealed class WindowsVideoLauncherTests
{
    [Fact]
    public void PlayAndFolder_UseExactPath_OneLaunchAndMissingFolderFallback()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "소유 영상 & sample.mp4");
        var requests = new List<ProcessStartInfo>();
        var launcher = new WindowsVideoLauncher(info => { requests.Add(info); return null; });
        try
        {
            Assert.False(launcher.Play(root).Succeeded);
            Assert.False(launcher.ShowInFolder(root).Succeeded);
            Assert.Empty(requests);
            File.WriteAllBytes(path, [1, 2, 3]);
            Assert.True(launcher.Play(path).Succeeded);
            Assert.Single(requests);
            Assert.Equal(path, requests[0].FileName);
            Assert.True(requests[0].UseShellExecute);
            requests.Clear();
            Assert.True(launcher.ShowInFolder(path).Succeeded);
            Assert.Single(requests);
            Assert.Equal("explorer.exe", requests[0].FileName);
            // Explorer parses /select, itself; only the pathname is enclosed in quotes.
            Assert.Equal("/select,\"" + path + "\"", requests[0].Arguments);
            Assert.Empty(requests[0].ArgumentList);
            requests.Clear();
            File.Delete(path);
            Assert.True(launcher.ShowInFolder(path).Succeeded);
            Assert.Single(requests);
            Assert.Equal(root, requests[0].FileName);
            requests.Clear();
            Assert.False(launcher.Play(path).Succeeded);
            Assert.Empty(requests);
            Directory.Delete(root);
            Assert.False(launcher.ShowInFolder(path).Succeeded);
            Assert.Empty(requests);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void OpenFolder_ExistingAbsoluteDirectory_LaunchesExactlyOnce_RejectsFileOrMissingFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriLauncherTests", "소유 폴더 & " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owned.mp4");
        var requests = new List<ProcessStartInfo>();
        var launcher = new WindowsVideoLauncher(info => { requests.Add(info); return null; });
        try
        {
            File.WriteAllBytes(path, [1]);
            Assert.True(launcher.OpenFolder(root).Succeeded);
            var request = Assert.Single(requests);
            Assert.Equal(root, request.FileName);
            Assert.True(request.UseShellExecute);
            Assert.Empty(request.ArgumentList);
            requests.Clear();
            Assert.False(launcher.OpenFolder(path).Succeeded);
            Assert.False(launcher.OpenFolder(Path.Combine(root, "absent")).Succeeded);
            Assert.Empty(requests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.mp4")]
    public void InvalidPath_NeverLaunches(string path)
    {
        var count = 0;
        var launcher = new WindowsVideoLauncher(_ => { count++; return null; });
        Assert.False(launcher.Play(path).Succeeded);
        Assert.False(launcher.ShowInFolder(path).Succeeded);
        Assert.False(launcher.OpenFolder(path).Succeeded);
        Assert.Equal(0, count);
    }

    [Fact]
    public void LaunchException_IsOneFailedAttempt_WithReason()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriLauncherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owned.mp4");
        var count = 0;
        try
        {
            File.WriteAllBytes(path, [1]);
            var launcher = new WindowsVideoLauncher(_ => { count++; throw new InvalidOperationException("owned launch failure"); });
            var result = launcher.Play(path);
            Assert.False(result.Succeeded);
            Assert.Equal("owned launch failure", result.Reason);
            Assert.Equal(1, count);
            var folderResult = launcher.OpenFolder(root);
            Assert.False(folderResult.Succeeded);
            Assert.Equal("owned launch failure", folderResult.Reason);
            Assert.Equal(2, count);
        }
        finally { Directory.Delete(root, true); }
    }
}
