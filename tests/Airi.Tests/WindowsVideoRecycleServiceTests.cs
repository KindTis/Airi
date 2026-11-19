using System.IO;
using Airi.Services;
using Xunit.Abstractions;

namespace Airi.Tests;

public sealed class WindowsVideoRecycleServiceTests(ITestOutputHelper output)
{
    [Fact]
    public void SafetyGate_RequiresExactFileAndRecycleFlag()
    {
        var path = Path.GetFullPath("test-video.mp4");
        var state = new RecycleOperationState(path);
        Assert.False(state.BeforeDelete(path + ".other", 0x80, 1, 0x40000000));
        Assert.Equal(VideoRecycleOutcome.Failed, state.Complete(0, 0, false).Outcome);
        state = new RecycleOperationState(path);
        Assert.False(state.BeforeDelete(path, 0, 1, 0x40000000));
        Assert.Equal(VideoRecycleOutcome.Failed, state.Complete(0, 0, false).Outcome);
        foreach (var (queryResult, attributes) in new[]
        {
            (0, 0x60000000u), // A filesystem folder is outside the single-file contract.
            (1, 0u), // A non-filesystem shell item.
            (unchecked((int)0x80070005), 0x40000000u), // Failed attribute query.
            (2, 0x40000000u) // An undocumented positive query result is not evidence.
        })
        {
            state = new RecycleOperationState(path);
            Assert.False(state.BeforeDelete(path, 0x80, queryResult, attributes));
            state.AfterDelete(path, 0x00270008, true, "C:\\untrusted-destination");
            var result = state.Complete(0, 0, false);
            Assert.Equal(VideoRecycleOutcome.Failed, result.Outcome);
            Assert.Null(result.ObservedDestinationPath);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x00270008)]
    public void Success_RequiresEveryCompletionSignalAndRecycleDestination(int completedPostResult)
    {
        var path = Path.GetFullPath("test-video.mp4");
        foreach (var mode in new[] { "no-pre", "no-post", "no-destination", "aborted", "query-failed", "perform-failed", "post-failed", "success" })
        {
            var state = new RecycleOperationState(path);
            if (mode != "no-pre") Assert.True(state.BeforeDelete(path, 0x80, 1, 0x40000000));
            if (mode != "no-post") state.AfterDelete(path, mode == "post-failed" ? unchecked((int)0x80070005) : completedPostResult,
                mode != "no-destination", "C:\\test-recycle-destination");
            var result = state.Complete(mode == "perform-failed" ? unchecked((int)0x80004005) : 0,
                mode == "query-failed" ? unchecked((int)0x80004005) : 0, mode == "aborted");
            Assert.Equal(mode == "success", result.Outcome == VideoRecycleOutcome.Succeeded);
            if (mode is "no-pre" or "no-post" or "no-destination" or "aborted" or "query-failed" or "perform-failed")
                Assert.Equal(VideoRecycleOutcome.Unknown, result.Outcome);
        }
    }

    [Fact]
    public void Cancellation_IsDistinctFromSafetyVetoAndUnknown()
    {
        var path = Path.GetFullPath("test-video.mp4");
        var state = new RecycleOperationState(path);
        Assert.True(state.BeforeDelete(path, 0x80, 1, 0x40000000));
        state.AfterDelete(path, unchecked((int)0x800704C7), false, null);
        Assert.Equal(VideoRecycleOutcome.Cancelled, state.Complete(unchecked((int)0x800704C7), 0, true).Outcome);
        Assert.Equal(VideoRecycleOutcome.Unknown, new RecycleOperationState(path).Complete(0, 0, false).Outcome);
    }

    [Theory]
    [InlineData("post", 0x0027000B)]
    [InlineData("post", 0x00270010)]
    [InlineData("post", 1)]
    [InlineData("post", 0x00270005)]
    [InlineData("perform", 0x0027000B)]
    [InlineData("perform", 0x00270010)]
    [InlineData("perform", 1)]
    [InlineData("perform", 0x00270008)]
    [InlineData("perform", 0x00270005)]
    [InlineData("query", 0x0027000B)]
    [InlineData("query", 0x00270010)]
    [InlineData("query", 1)]
    [InlineData("query", 0x00270008)]
    [InlineData("query", 0x00270005)]
    public void NonCompletionPositiveResult_IsUnknownEvenWithRecycleDestination(string signal, int code)
    {
        var path = Path.GetFullPath("test-video.mp4");
        var state = new RecycleOperationState(path);
        Assert.True(state.BeforeDelete(path, RecycleOperationState.RecycleIfPossible, 1, 0x40000000));
        state.AfterDelete(path, signal == "post" ? code : 0, true, "C:\\test-recycle-destination");

        var result = state.Complete(signal == "perform" ? code : 0, signal == "query" ? code : 0, false);

        Assert.Equal(VideoRecycleOutcome.Unknown, result.Outcome);
        Assert.Contains($"0x{code:X8}", result.Diagnostics);
        Assert.Equal("C:\\test-recycle-destination", result.ObservedDestinationPath);
        Assert.Null(result.RecycledPath);
    }

    [Theory]
    [InlineData("wrong-source")]
    [InlineData("duplicate-post")]
    [InlineData("veto")]
    public void UntrustedCallbackDestination_IsNotPreservedAsTargetReceipt(string mode)
    {
        var path = Path.GetFullPath("test-video.mp4");
        var state = new RecycleOperationState(path);
        Assert.True(state.BeforeDelete(path, RecycleOperationState.RecycleIfPossible, 1, 0x40000000));
        state.AfterDelete(mode == "wrong-source" ? path + ".other" : path, 0x00270008, true, "C:\\untrusted-destination");
        if (mode == "duplicate-post") state.AfterDelete(path, 0x00270008, true, "C:\\untrusted-destination");
        if (mode == "veto") state.Veto();

        var result = state.Complete(0, 0, false);

        Assert.Equal(VideoRecycleOutcome.Failed, result.Outcome);
        Assert.Null(result.ObservedDestinationPath);
        Assert.Null(result.RecycledPath);
    }

    [Fact]
    [Trait("Category", "NativeRecycle")]
    public async Task NativeRecycle_OnlyOwnedFile_ExactDestinationContainsOriginalBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriNativeRecycleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp4");
        var subtitle = Path.ChangeExtension(path, ".srt");
        var image = Path.ChangeExtension(path, ".jpg");
        var bytes = Guid.NewGuid().ToByteArray();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllTextAsync(subtitle, "owned subtitle");
            await File.WriteAllTextAsync(image, "owned image");
            var result = await new WindowsVideoRecycleService().RecycleAsync(path);
            output.WriteLine($"Owned source: {path}");
            output.WriteLine($"Outcome: {result.Outcome}; reason: {result.Reason}; exact recycle destination: {result.RecycledPath}");
            output.WriteLine($"Native completion signals: {result.Diagnostics}");
            var observedPath = result.RecycledPath ?? result.ObservedDestinationPath;
            output.WriteLine($"Observed target destination (diagnostic only): {observedPath}");
            byte[]? observedBytes = null;
            if (!string.IsNullOrWhiteSpace(observedPath))
            {
                try
                {
                    observedBytes = await File.ReadAllBytesAsync(observedPath);
                    output.WriteLine($"Observed destination: {observedBytes.Length} bytes, SHA256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(observedBytes))}, owned source bytes match={bytes.SequenceEqual(observedBytes)}. Test-owned recycle item retained at the exact destination above.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    output.WriteLine($"Observed destination could not be read: {ex.Message}");
                }
            }
            Assert.Equal(VideoRecycleOutcome.Succeeded, result.Outcome);
            Assert.False(File.Exists(path));
            Assert.NotNull(result.RecycledPath);
            Assert.Equal(bytes, observedBytes);
            Assert.Equal("owned subtitle", await File.ReadAllTextAsync(subtitle));
            Assert.Equal("owned image", await File.ReadAllTextAsync(image));
            // The service's exact destination is the only recycle item inspected. No bin enumeration/emptying.
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
