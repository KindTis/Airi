using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class LibraryStoreTests : IAsyncLifetime
{
    private readonly string _tempDirectory;
    private readonly string _libraryPath;

    public LibraryStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AiriTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _libraryPath = Path.Combine(_tempDirectory, "videos.json");
    }

    [Fact]
    public async Task LoadAsync_CreatesDefaultWhenMissing()
    {
        var store = new LibraryStore(_libraryPath);
        var library = await store.LoadAsync();
        Assert.True(File.Exists(_libraryPath));
        Assert.NotNull(library);
#if DEBUG
        Assert.NotEmpty(library.Videos);
#else
        Assert.Empty(library.Videos);
#endif
    }

    [Fact]
    public async Task SaveAsync_WritesTempThenAtomicallyReplacesReadableJson()
    {
        await WriteOldLibraryAsync();
        var stages = new List<LibraryStoreSaveStage>();
        var store = CreateStore((stage, _, _) => stages.Add(stage));
        await store.SaveAsync(CreateLibrary("new"));
        var persisted = await store.LoadAsync();
        Assert.Equal("new", persisted.Videos.Single().Meta.Title);
        Assert.Equal(new[]
        {
            LibraryStoreSaveStage.BeforeTemporaryWrite,
            LibraryStoreSaveStage.AfterTemporaryFlush,
            LibraryStoreSaveStage.BeforeCommit,
            LibraryStoreSaveStage.AfterCommit
        }, stages);
    }

    [Fact]
    public async Task SaveAsync_TemporaryWriteFault_PreservesOriginalBytes()
    {
        await WriteOldLibraryAsync();
        var before = await File.ReadAllBytesAsync(_libraryPath);
        var store = CreateStore((stage, _, _) =>
        {
            if (stage == LibraryStoreSaveStage.BeforeTemporaryWrite) throw new IOException("write fault");
        });
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(CreateLibrary("new")));
        Assert.Equal(before, await File.ReadAllBytesAsync(_libraryPath));
    }

    [Fact]
    public async Task SaveAsync_CommitFault_PreservesOriginalBytes()
    {
        await WriteOldLibraryAsync();
        var before = await File.ReadAllBytesAsync(_libraryPath);
        var store = CreateStore((stage, _, _) =>
        {
            if (stage == LibraryStoreSaveStage.BeforeCommit) throw new IOException("commit fault");
        });
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(CreateLibrary("new")));
        Assert.Equal(before, await File.ReadAllBytesAsync(_libraryPath));
    }

    [Fact]
    public async Task SaveAsync_SuccessAndFailure_RemoveOwnedTemporaryFiles()
    {
        var store = CreateStore();
        await store.SaveAsync(CreateLibrary("success"));
        Assert.Empty(GetOwnedTemps());
        var failing = CreateStore((stage, _, _) =>
        {
            if (stage == LibraryStoreSaveStage.BeforeCommit) throw new IOException("failure");
        });
        await Assert.ThrowsAsync<IOException>(() => failing.SaveAsync(CreateLibrary("failure")));
        Assert.Empty(GetOwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_WritesCurrentVersionWithoutMutatingCallerVersion()
    {
        var library = CreateLibrary("version");
        library.Version = -10;
        await CreateStore().SaveAsync(library);
        Assert.Equal(-10, library.Version);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_libraryPath));
        Assert.Equal(1, json.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task SaveAsync_DoesNotReplaceOrMutateCallerCollections()
    {
        var library = CreateLibrary("caller");
        var targets = library.Targets;
        var videos = library.Videos;
        var entry = videos[0];
        await CreateStore().SaveAsync(library);
        Assert.Same(targets, library.Targets);
        Assert.Same(videos, library.Videos);
        Assert.Same(entry, library.Videos[0]);
        Assert.Equal("caller", entry.Meta.Title);
    }

    [Fact]
    public async Task SaveAsync_PrimaryFailureIsPreservedWhenTemporaryCleanupAlsoFails()
    {
        await WriteOldLibraryAsync();
        FileStream? held = null;
        var store = CreateStore((stage, tempPath, _) =>
        {
            if (stage == LibraryStoreSaveStage.AfterTemporaryFlush)
            {
                held = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            if (stage == LibraryStoreSaveStage.BeforeCommit)
            {
                throw new InvalidOperationException("primary");
            }
        });
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(CreateLibrary("new")));
        Assert.Equal("primary", failure.Message);
        Assert.Single(GetOwnedTemps());
        held!.Dispose();
    }

    [Fact]
    public async Task SaveAsync_CleanupFailureLeavesRecoverableOwnedTemp()
    {
        FileStream? held = null;
        var store = CreateStore((stage, tempPath, _) =>
        {
            if (stage == LibraryStoreSaveStage.AfterTemporaryFlush)
            {
                held = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            if (stage == LibraryStoreSaveStage.BeforeCommit) throw new IOException("primary");
        });
        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(CreateLibrary("new")));
        var temp = Assert.Single(GetOwnedTemps());
        held!.Dispose();
        await File.WriteAllTextAsync(_libraryPath, "null");
        await CreateStore().LoadAsync();
        Assert.False(File.Exists(temp));
    }

    [Fact]
    public async Task SaveAsync_CancelBeforeCommitGate_PreservesOldAndThrows()
    {
        await WriteOldLibraryAsync();
        var before = await File.ReadAllBytesAsync(_libraryPath);
        using var cancellation = new CancellationTokenSource();
        var store = CreateStore((stage, _, _) =>
        {
            if (stage == LibraryStoreSaveStage.AfterTemporaryFlush) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(CreateLibrary("new"), cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(_libraryPath));
        Assert.Empty(GetOwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_CancelAfterCommitGate_CompletesNewAndSucceeds()
    {
        await WriteOldLibraryAsync();
        using var cancellation = new CancellationTokenSource();
        var store = CreateStore((stage, _, _) =>
        {
            if (stage == LibraryStoreSaveStage.BeforeCommit) cancellation.Cancel();
        });
        await store.SaveAsync(CreateLibrary("new"), cancellation.Token);
        Assert.Equal("new", (await store.LoadAsync()).Videos.Single().Meta.Title);
    }

    [Fact]
    public async Task LoadAsync_CleansOnlyValidOwnedStaleTemporaryFiles()
    {
        await WriteOldLibraryAsync();
        var valid = Path.Combine(_tempDirectory, $".videos.json.airi-tmp-{Guid.NewGuid():D}");
        var invalid = Path.Combine(_tempDirectory, ".videos.json.airi-tmp-not-a-guid");
        await File.WriteAllTextAsync(valid, "stale");
        await File.WriteAllTextAsync(invalid, "unrelated");
        await CreateStore().LoadAsync();
        Assert.False(File.Exists(valid));
        Assert.True(File.Exists(invalid));
    }

    [Fact]
    public async Task LoadAsync_CancelledDuringDeserialize_ThrowsWithoutResetOrSave()
    {
        await WriteOldLibraryAsync();
        var before = await File.ReadAllBytesAsync(_libraryPath);
        using var cancellation = new CancellationTokenSource();
        var store = CreateStore(ioStage: (stage, _) =>
        {
            if (stage == LibraryStoreIoStage.BeforeDeserialize) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.LoadAsync(cancellation.Token));
        Assert.Equal(before, await File.ReadAllBytesAsync(_libraryPath));
    }

    [Fact]
    public async Task LoadAsync_PreCancelled_DoesNotCreateMissingDestination()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateStore().LoadAsync(cancellation.Token));
        Assert.False(File.Exists(_libraryPath));
    }

    [Fact]
    public async Task LoadAsync_InvalidJson_AtomicallyResetsToDefaults()
    {
        await File.WriteAllTextAsync(_libraryPath, "{ invalid json");
        var library = await CreateStore().LoadAsync();
        Assert.NotEmpty(library.Targets);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_libraryPath));
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
    }

    [Fact]
    public async Task LoadAsync_NullJson_AtomicallyResetsToDefaults()
    {
        await File.WriteAllTextAsync(_libraryPath, "null");
        var library = await CreateStore().LoadAsync();
        Assert.NotEmpty(library.Targets);
        Assert.DoesNotContain("null", await File.ReadAllTextAsync(_libraryPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadAsync_TransientIoFailure_ThrowsAndPreservesOriginalBytes()
    {
        await WriteOldLibraryAsync();
        var before = await File.ReadAllBytesAsync(_libraryPath);
        await using (var held = new FileStream(_libraryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => CreateStore().LoadAsync());
        }
        Assert.Equal(before, await File.ReadAllBytesAsync(_libraryPath));
    }

    [Fact]
    public Task LibraryStore_LoadFilesystemPreambleRunsOffStaDispatcher() => WpfTestHost.RunAsync(async () =>
    {
        await WriteOldLibraryAsync();
        var caller = Environment.CurrentManagedThreadId;
        var threads = new ConcurrentBag<int>();
        await CreateStore(ioStage: (_, threadId) => threads.Add(threadId)).LoadAsync();
        Assert.NotEmpty(threads);
        Assert.DoesNotContain(caller, threads);
    });

    [Fact]
    public Task LibraryStore_SaveProjectionOpenFlushAndCommitRunOffStaDispatcher() => WpfTestHost.RunAsync(async () =>
    {
        var caller = Environment.CurrentManagedThreadId;
        var threads = new ConcurrentBag<int>();
        await CreateStore(
            (_, _, threadId) => threads.Add(threadId),
            (_, threadId) => threads.Add(threadId)).SaveAsync(CreateLibrary("worker"));
        Assert.NotEmpty(threads);
        Assert.DoesNotContain(caller, threads);
    });

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        return Task.CompletedTask;
    }

    private LibraryStore CreateStore(
        Action<LibraryStoreSaveStage, string, int>? saveStage = null,
        Action<LibraryStoreIoStage, int>? ioStage = null) =>
        new(_libraryPath, saveStage, ioStage);

    private async Task WriteOldLibraryAsync() =>
        await new LibraryStore(_libraryPath).SaveAsync(CreateLibrary("old"));

    private string[] GetOwnedTemps() =>
        Directory.GetFiles(_tempDirectory, ".videos.json.airi-tmp-*");

    private static LibraryData CreateLibrary(string title) => new()
    {
        Version = 42,
        Targets = new List<TargetFolder>
        {
            new("./Videos", new[] { "*.mp4" }, Array.Empty<string>(), null)
        },
        Videos = new List<VideoEntry>
        {
            new("./Videos/test.mp4",
                new VideoMeta(title, null, Array.Empty<string>(), "resources/noimage.jpg", Array.Empty<string>(), string.Empty),
                1,
                DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc),
                DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc))
        }
    };
}
