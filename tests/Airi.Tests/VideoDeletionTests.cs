using System.IO;
using System.Text.Json;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class VideoDeletionTests
{
    [Fact]
    public Task CancelMissingAndBusy_DoNotRecycleOrSave() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(false), _ => throw new Exception("stop"));
        Assert.Same(item, vm.SelectedVideo);
        item.Presence = VideoPresenceState.Missing;
        await vm.DeleteSelectedVideoAsync(_ => throw new Exception("confirm"), _ => Task.CompletedTask);
        item.Presence = VideoPresenceState.Available;
        var lease = vm.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch)!;
        await vm.DeleteSelectedVideoAsync(_ => throw new Exception("confirm"), _ => Task.CompletedTask);
        vm.ReleaseLibraryMutation(lease);
        Assert.Empty(f.Recycler.Paths);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.True(vm.CanMutateLibrary);
    });

    [Theory]
    [InlineData(VideoRecycleOutcome.Cancelled)]
    [InlineData(VideoRecycleOutcome.Failed)]
    [InlineData(VideoRecycleOutcome.Unknown)]
    public Task RecycleNotSuccessful_PreservesItemAndJson(VideoRecycleOutcome outcome) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        f.Recycler.Result = new(outcome, "injected result");
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.Contains(item, vm.Videos);
        Assert.Same(item, vm.SelectedVideo);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.True(vm.CanMutateLibrary);
    });

    [Fact]
    public Task CapturesTarget_WaitsForPreview_RejectsReentry_ThenPersistsCandidate() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var target = vm.SelectedVideo!;
        var visible = vm.FilteredVideos.Cast<VideoItem>().ToList();
        var index = visible.IndexOf(target);
        var next = visible[index + 1];
        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = vm.DeleteSelectedVideoAsync(captured =>
        {
            Assert.Same(target, captured.Item);
            Assert.Equal(Path.GetFileName(target.SourcePath), captured.FileName);
            Assert.False(vm.CanMutateLibrary);
            vm.SelectedVideo = next;
            return Task.FromResult(true);
        }, item =>
        {
            Assert.Same(target, item);
            stopped.SetResult();
            return stop.Task;
        });
        await stopped.Task;
        Assert.Same(task, vm.VideoDeletionTask);
        Assert.True(vm.IsVideoDeletionTarget(target));
        await vm.DeleteSelectedVideoAsync(_ => throw new Exception("reentry"), _ => Task.CompletedTask);
        Assert.Empty(f.Recycler.Paths);
        stop.SetResult();
        await task;
        Assert.Equal(new[] { target.SourcePath }, f.Recycler.Paths);
        Assert.DoesNotContain(target, vm.Videos);
        Assert.Same(next, vm.SelectedVideo);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(f.Store.FilePath));
        var entries = json.RootElement.GetProperty("Videos").EnumerateArray().ToList();
        Assert.Equal(2, entries.Count);
        Assert.DoesNotContain(entries, entry => entry.GetProperty("Path").GetString() == target.LibraryPath);
        Assert.All(entries, entry => Assert.Equal("preserved description", entry.GetProperty("Meta").GetProperty("Description").GetString()));
        Assert.True(File.Exists(f.SidecarPath));
        Assert.True(File.Exists(f.ThumbnailPath));
        var reloaded = await f.Store.LoadAsync();
        Assert.Equal(2, reloaded.Videos.Count);
        Assert.True(vm.CanMutateLibrary);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ChangedPathOrDisappearedFile_StopsBeforeRecycle(bool disappear) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        await vm.DeleteSelectedVideoAsync(_ =>
        {
            if (disappear) File.Move(item.SourcePath, item.SourcePath + ".gone");
            else item.UpdateFileState(vm.Videos.First(v => !ReferenceEquals(v, item)).SourcePath,
                item.SizeBytes, item.LastModifiedUtc, item.Presence, item.CreatedUtc);
            return Task.FromResult(true);
        }, _ => Task.CompletedTask);
        Assert.Empty(f.Recycler.Paths);
        Assert.Contains(item, vm.Videos);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Contains("실패", vm.StatusMessage);
    });

    [Fact]
    public Task SaveFailure_AfterMove_PreservesMetadataAndMarksMissing() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        var dispatcherThread = Environment.CurrentManagedThreadId;
        var probeThreads = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var vm = await f.CreateAsync(path =>
        {
            probeThreads.Add(Environment.CurrentManagedThreadId);
            return File.Exists(path) && !Directory.Exists(path);
        });
        var item = vm.SelectedVideo!;
        f.FailSave = true;
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.False(File.Exists(item.SourcePath));
        Assert.Contains(item, vm.Videos);
        Assert.Equal("preserved description", item.Description);
        Assert.Equal(VideoPresenceState.Missing, item.Presence);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Contains("휴지통 이동은 완료되었지만 라이브러리 저장에 실패했습니다", vm.StatusMessage);
        Assert.True(vm.CanMutateLibrary);
        Assert.True(probeThreads.Count >= 4); // Before confirmation, after confirmation, after stop, and recovery.
        Assert.DoesNotContain(dispatcherThread, probeThreads);
    });

    [Fact]
    public Task FileProbe_DoesNotBlockDispatcher_AndRechecksIdentityAfterReturning() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var releaseProbe = new ManualResetEventSlim();
        var probeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var vm = await f.CreateAsync(path =>
        {
            if (Interlocked.Increment(ref count) == 2)
            {
                probeStarted.TrySetResult();
                if (!releaseProbe.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Probe was not released by dispatcher.");
            }
            return File.Exists(path) && !Directory.Exists(path);
        });
        var item = vm.SelectedVideo!;
        var deletion = vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        try
        {
            await probeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(deletion.IsCompleted);
            var other = vm.Videos.First(v => !ReferenceEquals(v, item));
            item.UpdateFileState(other.SourcePath, item.SizeBytes, item.LastModifiedUtc, item.Presence, item.CreatedUtc);
        }
        finally { releaseProbe.Set(); }
        await deletion;
        Assert.Empty(f.Recycler.Paths);
        Assert.Contains(item, vm.Videos);
        Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        Assert.Contains("실패", vm.StatusMessage);
        Assert.True(vm.CanMutateLibrary);
    });

    [Fact]
    public Task LastAndOnlyVisibleItem_SelectPreviousThenNone_ResetsActorFilter() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var visible = vm.FilteredVideos.Cast<VideoItem>().ToList();
        vm.SelectedVideo = visible[^1];
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.Same(visible[^2], vm.SelectedVideo);
        vm.SelectedActor = vm.SelectedVideo!.Actors[0];
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.Equal("All Actors", vm.SelectedActor);
        await vm.DeleteSelectedVideoAsync(_ => Task.FromResult(true), _ => Task.CompletedTask);
        Assert.Null(vm.SelectedVideo);
        Assert.Empty(vm.Videos);
    });
}

internal sealed class VideoDeletionFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AiriVideoDeletionTests", Guid.NewGuid().ToString("N"));
    internal LibraryStore Store { get; }
    internal FakeRecycler Recycler { get; } = new();
    internal bool FailSave { get; set; }
    internal string InitialJson { get; private set; } = "";
    internal string SidecarPath => Path.Combine(Root, "video-0.srt");
    internal string ThumbnailPath => Path.Combine(Root, "cover.jpg");

    internal VideoDeletionFixture()
    {
        Directory.CreateDirectory(Root);
        Store = new LibraryStore(Path.Combine(Root, "videos.json"), (stage, _, _) =>
        {
            if (FailSave && stage == LibraryStoreSaveStage.BeforeCommit) throw new IOException("injected save failure");
        }, null);
        Recycler.MoveOnSuccess = path => File.Move(path, path + ".test-recycled");
    }

    internal async Task<MainViewModel> CreateAsync(Func<string, bool>? deletionFileProbe = null)
    {
        var library = new LibraryData();
        for (var i = 0; i < 3; i++)
        {
            var path = Path.Combine(Root, $"video-{i}.mp4");
            await File.WriteAllBytesAsync(path, new byte[] { 1, 2, (byte)i });
            library.Videos.Add(new VideoEntry(LibraryPathHelper.NormalizeLibraryPath(path),
                new VideoMeta($"Title {i}", null, new[] { $"Actor {i}" }, ThumbnailPath, Array.Empty<string>(), "preserved description")));
        }
        await File.WriteAllTextAsync(SidecarPath, "test subtitle");
        await File.WriteAllTextAsync(ThumbnailPath, "test thumbnail");
        await Store.SaveAsync(library);
        var provider = new CrawlerSessionProvider();
        var vm = new MainViewModel(Store, new Scanner(library), new WebMetadataService(Array.Empty<IWebVideoMetaSource>(),
            new ThumbnailCache(Root), NullTranslationService.Instance, "KO"), provider, new OneFourOneJavMetaSource(provider),
            new NoCrawler(), ThumbnailPerformanceProbe.Disabled, new TestThumbnailImageLoader(),
            recycleService: Recycler, deletionFileProbe: deletionFileProbe);
        await vm.InitializeAsync();
        InitialJson = await File.ReadAllTextAsync(Store.FilePath);
        vm.SelectedSortOption = vm.SortOptions.First(o => o.Field == MainViewModel.SortField.Title && o.Direction == System.ComponentModel.ListSortDirection.Ascending);
        await vm.SettingsSaveTask;
        vm.SelectedVideo = vm.FilteredVideos.Cast<VideoItem>().First();
        return vm;
    }

    public void Dispose() => Directory.Delete(Root, true);

    internal sealed class FakeRecycler : IVideoRecycleService
    {
        internal List<string> Paths { get; } = new();
        internal VideoRecycleResult Result { get; set; } = new(VideoRecycleOutcome.Succeeded);
        internal Action<string>? MoveOnSuccess { get; set; }
        internal Func<string, Task<VideoRecycleResult>>? Override { get; set; }
        public Task<VideoRecycleResult> RecycleAsync(string absolutePath)
        {
            Paths.Add(absolutePath);
            if (Override is not null) return Override(absolutePath);
            if (Result.Outcome == VideoRecycleOutcome.Succeeded) MoveOnSuccess?.Invoke(absolutePath);
            return Task.FromResult(Result);
        }
    }

    private sealed class Scanner(LibraryData library) : ILibraryScanner
    {
        public Task<LibraryScanResult> ScanAsync(LibraryData _, IReadOnlyList<TargetFolder> targets, CancellationToken cancellationToken) =>
            Task.FromResult(new LibraryScanResult(library.Videos.Select(v => new FileSnapshot(v.Path,
                LibraryPathHelper.ResolveToAbsolute(v.Path), v.SizeBytes, v.LastModifiedUtc, v.CreatedUtc)).ToArray(),
                Array.Empty<FileSnapshot>(), Array.Empty<VideoEntry>(), Array.Empty<UpdatedFile>()));
    }

    private sealed class NoCrawler : IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Deletion tests do not use a crawler.");
    }
}
