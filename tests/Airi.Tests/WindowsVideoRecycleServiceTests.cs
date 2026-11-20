using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Theory]
    [InlineData(0)]
    [InlineData(0x00270008)]
    public void ReceiptCaptureFailure_PreservesObservedMoveAndNativeEvidence_WithoutSuccessOrUndo(int postResult)
    {
        var path = Path.GetFullPath("test-video.mp4");
        var destination = "C:\\exact-observed-recycle-destination";
        var state = new RecycleOperationState(path);
        Assert.True(state.BeforeDelete(path, RecycleOperationState.RecycleIfPossible, 1, 0x40000000));
        state.AfterDelete(path, postResult, true, destination);
        state.CaptureReceipt(() => throw new IOException("injected receipt creation failure"));

        var result = state.Complete(0, 0, false);

        Assert.Equal(VideoRecycleOutcome.Unknown, result.Outcome);
        Assert.Equal(destination, result.ObservedDestinationPath);
        Assert.Contains("injected receipt creation failure", result.Reason);
        Assert.DoesNotContain("작업을 중단", result.Reason);
        Assert.Contains($"postHRESULT=0x{postResult:X8}", result.Diagnostics);
        Assert.Contains("performHRESULT=0x00000000", result.Diagnostics);
        Assert.Contains("abortedQueryHRESULT=0x00000000", result.Diagnostics);
        Assert.Contains("aborted=False", result.Diagnostics);
        Assert.Contains("safetyVeto=False", result.Diagnostics);
        Assert.Contains("receiptError=System.IO.IOException", result.Diagnostics);
        Assert.Contains("receiptHRESULT=0x80131620", result.Diagnostics);
        Assert.Contains("receiptMessage=injected receipt creation failure", result.Diagnostics);
        Assert.Null(result.RecycledPath);
        Assert.Null(result.Receipt);
        Assert.False(result.MovementConfirmed);
        Assert.False(result.SideEffectsKnownAbsent);
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("{\"Version\":2}")]
    public async Task InvalidReceipt_RefusesBeforeNativeEffects(string? receipt)
    {
        var service = new WindowsVideoRecycleService();
        var result = await service.RestoreAsync(receipt!);
        Assert.Equal(VideoRecycleOutcome.Failed, result.Outcome);
        Assert.True(result.SideEffectsKnownAbsent);
        Assert.False(result.MovementConfirmed);
        Assert.Null(result.Receipt);
        var inspection = await service.InspectAsync(Path.GetFullPath("owned-absent.mp4"), receipt ?? "{}");
        Assert.Equal(VideoFileEvidence.Unknown, inspection.SourceState);
        Assert.Equal(VideoFileEvidence.Unknown, inspection.RecycleState);
    }

    [Fact]
    public async Task WithoutReceipt_PresenceDoesNotEstablishIdentityOrSuccess()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriNativeEvidenceTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "owned.mp4");
        try
        {
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
            var service = new WindowsVideoRecycleService();
            var present = await service.InspectAsync(path);
            Assert.Equal(VideoFileEvidence.PresentUnverified, present.SourceState);
            Assert.Equal(VideoFileEvidence.Unknown, present.RecycleState);
            var absent = await service.InspectAsync(path + ".absent");
            Assert.Equal(VideoFileEvidence.Missing, absent.SourceState);
            Assert.Equal(VideoFileEvidence.Unknown, absent.RecycleState);
            var legacyResult = new VideoRecycleResult(VideoRecycleOutcome.Succeeded, RecycledPath: path);
            Assert.Null(legacyResult.Receipt);
            Assert.False(legacyResult.MovementConfirmed);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    [Trait("Category", "NativeRecycle")]
    public async Task NativeRecycle_RoundTripsExactReceipt_RejectsCollisionAndMissingParent_PreservesSidecars()
    {
        var root = Path.Combine(Path.GetTempPath(), "AiriNativeRecycleTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".mp4");
        var subtitle = Path.ChangeExtension(path, ".srt");
        var image = Path.ChangeExtension(path, ".jpg");
        var bytes = Guid.NewGuid().ToByteArray();
        var service = new WindowsVideoRecycleService();
        string? receipt = null;
        var movedRoot = root + "-parent-missing";
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            await File.WriteAllTextAsync(subtitle, "owned subtitle");
            await File.WriteAllTextAsync(image, "owned image");
            var result = await service.RecycleAsync(path);
            output.WriteLine($"Owned source: {path}; outcome: {result.Outcome}; exact recycle destination: {result.RecycledPath}");
            output.WriteLine($"Native completion signals: {result.Diagnostics}");
            receipt = result.Receipt;
            Assert.Equal(VideoRecycleOutcome.Succeeded, result.Outcome);
            Assert.True(result.MovementConfirmed);
            Assert.NotNull(receipt);
            Assert.False(File.Exists(path));
            Assert.NotNull(result.RecycledPath);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.RecycledPath));
            var serialized = JsonSerializer.Serialize(receipt);
            receipt = JsonSerializer.Deserialize<string>(serialized)!;
            var inspection = await service.InspectAsync(path, receipt);
            Assert.Equal(VideoFileEvidence.Missing, inspection.SourceState);
            Assert.Equal(VideoFileEvidence.MatchesReceipt, inspection.RecycleState);

            // A different original file must never be replaced or silently renamed.
            await File.WriteAllBytesAsync(path, [7, 8, 9]);
            var collision = await service.RestoreAsync(receipt);
            Assert.Equal(VideoRecycleOutcome.Failed, collision.Outcome);
            Assert.True(collision.SideEffectsKnownAbsent);
            Assert.Equal(new byte[] { 7, 8, 9 }, await File.ReadAllBytesAsync(path));
            Assert.Equal(VideoFileEvidence.Different, (await service.InspectAsync(path, receipt)).SourceState);
            File.Delete(path); // This is only the test-owned collision fixture, never the recycled target.

            Directory.Move(root, movedRoot);
            var missingParent = await service.RestoreAsync(receipt);
            Assert.Equal(VideoRecycleOutcome.Failed, missingParent.Outcome);
            Assert.True(missingParent.SideEffectsKnownAbsent);
            Assert.False(Directory.Exists(root));
            Directory.Move(movedRoot, root);

            var changedIdentity = JsonNode.Parse(receipt)!;
            changedIdentity["Identity"]!["FileIndex"] = 0;
            var mismatch = await service.RestoreAsync(changedIdentity.ToJsonString());
            Assert.Equal(VideoRecycleOutcome.Failed, mismatch.Outcome);
            Assert.True(mismatch.SideEffectsKnownAbsent);
            Assert.Equal(VideoFileEvidence.Different, (await service.InspectAsync(path, changedIdentity.ToJsonString())).RecycleState);
            var invalidPidl = JsonNode.Parse(receipt)!;
            invalidPidl["ShellPidl"] = "AA==";
            Assert.True((await service.RestoreAsync(invalidPidl.ToJsonString())).SideEffectsKnownAbsent);
            Assert.False(File.Exists(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(result.RecycledPath));

            var restored = await service.RestoreAsync(receipt);
            output.WriteLine($"Exact restore outcome: {restored.Outcome}; signals: {restored.Diagnostics}");
            Assert.Equal(VideoRecycleOutcome.Succeeded, restored.Outcome);
            Assert.True(restored.MovementConfirmed);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.False(File.Exists(result.RecycledPath));
            Assert.Equal("owned subtitle", await File.ReadAllTextAsync(subtitle));
            Assert.Equal("owned image", await File.ReadAllTextAsync(image));
            var restoredInspection = await service.InspectAsync(path, receipt);
            Assert.Equal(VideoFileEvidence.MatchesReceipt, restoredInspection.SourceState);
            Assert.Equal(VideoFileEvidence.Missing, restoredInspection.RecycleState);
            var duplicateRestore = await service.RestoreAsync(receipt);
            Assert.Equal(VideoRecycleOutcome.Failed, duplicateRestore.Outcome);
            Assert.True(duplicateRestore.SideEffectsKnownAbsent);
        }
        finally
        {
            if (Directory.Exists(movedRoot) && !Directory.Exists(root)) Directory.Move(movedRoot, root);
            if (!File.Exists(path) && receipt is not null)
            {
                var recovery = await service.RestoreAsync(receipt);
                output.WriteLine($"Owned fixture final recovery: {recovery.Outcome}; {recovery.Reason}");
            }
            // Never enumerate or permanently remove any Recycle Bin item, including on test failure.
            if (File.Exists(path)) Directory.Delete(root, true);
        }
    }
}
