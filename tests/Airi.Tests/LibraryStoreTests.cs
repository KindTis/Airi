using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly string _videoPath;

    public LibraryStoreTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "AiriTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
        _libraryPath = Path.Combine(_tempDirectory, "videos.json");
        _videoPath = Path.Combine(_tempDirectory, "test.mp4");
        File.WriteAllBytes(_videoPath, new byte[] { 0 });
    }

    [Fact]
    public async Task LoadAsync_CreatesDefaultWhenMissing()
    {
        var store = new LibraryStore(_libraryPath);
        var library = await store.LoadAsync();
        Assert.True(File.Exists(_libraryPath));
        Assert.NotNull(library);
        Assert.Empty(library.Videos);
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
        Assert.Equal(2, json.RootElement.GetProperty("Version").GetInt32());
    }

    [Fact]
    public async Task SaveAsync_WritesWholeVersionTwoEnvelope()
    {
        await CreateStore().SaveAsync(CreateLibrary("projection"));

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_libraryPath));
        Assert.Equal(
            new[] { "Version", "Videos", "Journal" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.False(json.RootElement.TryGetProperty("Targets", out _));
        Assert.DoesNotContain(
            "LastScanUtc",
            await File.ReadAllTextAsync(_libraryPath),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LoadAsync_LegacyTargetsAreIgnoredWithoutCreatingSettingsFile()
    {
        await File.WriteAllTextAsync(_libraryPath, """
        {
          "Version": 1,
          "Targets": [
            {
              "Root": "x:/legacy",
              "IncludePatterns": ["*.mp4"],
              "ExcludePatterns": [],
              "LastScanUtc": "2026-07-25T00:00:00Z"
            }
          ],
          "Videos": []
        }
        """);

        var library = await CreateStore().LoadAsync();
        await CreateStore().SaveAsync(library);

        Assert.False(File.Exists(Path.Combine(_tempDirectory, "settings.json")));
        var persisted = await File.ReadAllTextAsync(_libraryPath);
        Assert.DoesNotContain("Targets", persisted, StringComparison.Ordinal);
        Assert.DoesNotContain("LastScanUtc", persisted, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAsync_DoesNotReplaceOrMutateCallerCollections()
    {
        var library = CreateLibrary("caller");
        var videos = library.Videos;
        var entry = videos[0];
        await CreateStore().SaveAsync(library);
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
        await Assert.ThrowsAnyAsync<JsonException>(() => CreateStore().LoadAsync());
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
    public async Task LoadAsync_InvalidJson_PreservesOriginal()
    {
        await File.WriteAllTextAsync(_libraryPath, "{ invalid json");
        await Assert.ThrowsAnyAsync<JsonException>(() => CreateStore().LoadAsync());
        Assert.Equal("{ invalid json", await File.ReadAllTextAsync(_libraryPath));
    }

    [Fact]
    public async Task LoadAsync_NullJson_PreservesOriginal()
    {
        await File.WriteAllTextAsync(_libraryPath, "null");
        await Assert.ThrowsAnyAsync<JsonException>(() => CreateStore().LoadAsync());
        Assert.Equal("null", await File.ReadAllTextAsync(_libraryPath));
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

    [Fact]
    public async Task LoadAsync_V1MigrationPreservesRawValuesAndBackupAndStableIdentity()
    {
        var original = """
        {"Version":1,"Videos":[{"Path":" ./missing video.mp4 ","Meta":{"Title":"  ","Date":"not-a-date","Actors":[" A "," A ",""],"Thumbnail":" ../old cover.jpg ","Tags":[" x "," x "],"Description":"  original description  "},"SizeBytes":-7,"LastModifiedUtc":"2020-01-02T03:04:05","CreatedUtc":"0001-01-01T00:00:00"}]}
        """;
        await File.WriteAllTextAsync(_libraryPath, original);
        var bytes = await File.ReadAllBytesAsync(_libraryPath);
        var loaded = await CreateStore().LoadAsync();
        var entry = Assert.Single(loaded.Videos);
        Assert.Equal(2, loaded.Version);
        Assert.NotEqual(Guid.Empty, entry.Id);
        Assert.Equal(" ./missing video.mp4 ", entry.Path);
        Assert.Equal("  ", entry.Meta.Title);
        Assert.Equal(new[] { " A ", " A ", "" }, entry.Meta.Actors);
        Assert.Equal(new[] { " x ", " x " }, entry.Meta.Tags);
        Assert.Equal(" ../old cover.jpg ", entry.Meta.Thumbnail);
        Assert.Equal("  original description  ", entry.Meta.Description);
        Assert.Equal(-7, entry.SizeBytes);
        Assert.Equal(DateTimeKind.Unspecified, entry.LastModifiedUtc.Kind);
        Assert.Equal(default, entry.CreatedUtc);
        Assert.Null(entry.Meta.Date);
        Assert.Equal("not-a-date", entry.Meta.RawDate);
        Assert.Contains(MetadataField.Date, entry.Meta.ManualFieldLocks);
        Assert.DoesNotContain(MetadataField.Title, entry.Meta.ManualFieldLocks);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_tempDirectory, "*.v1-backup-*.json"))));
        var reread = await CreateStore().LoadAsync();
        Assert.Equal(entry.Id, Assert.Single(reread.Videos).Id);
        Assert.Equal(entry.Meta.RawDate, reread.Videos[0].Meta.RawDate);
        Assert.Equal(entry.Meta.ManualFieldLocks, reread.Videos[0].Meta.ManualFieldLocks);
    }

    [Fact]
    public async Task LoadAsync_V1BomAndOffsetTimestampsPreserveOriginalTextAndBytes()
    {
        const string raw = """
        {"Version":1,"Videos":[{"Path":"./missing.mp4","Meta":{"Title":"title","Date":null,"Actors":[],"Thumbnail":"","Tags":[],"Description":""},"LastModifiedUtc":"2020-01-02T03:04:05+05:30","CreatedUtc":"2020-01-02T03:04:05.1200Z"}]}
        """;
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(raw)).ToArray();
        await File.WriteAllBytesAsync(_libraryPath, bytes);
        await CreateStore().LoadAsync();
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(_libraryPath));
        var entry = json.RootElement.GetProperty("Videos")[0];
        Assert.Equal("2020-01-02T03:04:05+05:30", entry.GetProperty("LastModifiedUtc").GetString());
        Assert.Equal("2020-01-02T03:04:05.1200Z", entry.GetProperty("CreatedUtc").GetString());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_tempDirectory, "*.v1-backup-*.json"))));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{broken")]
    [InlineData("{\"Version\":999,\"Videos\":[]}")]
    [InlineData("{\"Version\":2,\"Videos\":[{\"Path\":\"x\",\"Meta\":{}}],\"Journal\":[]}")]
    public async Task LoadAsync_UnsupportedOrDamagedSchemaPreservesBytesAndBlocksSave(string raw)
    {
        await File.WriteAllTextAsync(_libraryPath, raw);
        var bytes = await File.ReadAllBytesAsync(_libraryPath);
        var store = CreateStore();
        await Assert.ThrowsAnyAsync<JsonException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(CreateLibrary("replacement")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_libraryPath));
    }

    [Fact]
    public async Task LoadAsync_MigrationCommitFailureKeepsV1BytesAndBackup()
    {
        const string original = "{\"Version\":1,\"Videos\":[]}";
        await File.WriteAllTextAsync(_libraryPath, original);
        var bytes = await File.ReadAllBytesAsync(_libraryPath);
        var store = CreateStore((stage, _, _) =>
        {
            if (stage == LibraryStoreSaveStage.BeforeCommit) throw new IOException("migration failure");
        });
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync());
        Assert.Equal(bytes, await File.ReadAllBytesAsync(_libraryPath));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(_tempDirectory, "*.v1-backup-*.json"))));
    }

    [Fact]
    public async Task SaveAsync_RoundTripsJournalManualEmptyAndMissingEntries()
    {
        var library = CreateLibrary("");
        var entry = library.Videos[0] with
        {
            Revision = 4,
            Meta = library.Videos[0].Meta with { Studio = " studio ", Genres = new[] { "genre", "genre" },
                ManualFieldLocks = new[] { MetadataField.Title } }
        };
        library.Videos[0] = entry;
        library.Journal.Add(new LibraryJournalEntry { EntryId = entry.Id, BaseRevision = entry.Revision, State = LibraryOperationState.Pending,
            OriginalPath = entry.Path, Snapshot = entry, ReceiptJson = "{\"Version\":1,\"opaque\":true}" });
        await CreateStore().SaveAsync(library);
        var loaded = await CreateStore().LoadAsync();
        Assert.Equal(entry.Id, Assert.Single(loaded.Videos).Id);
        Assert.Equal(entry.Revision, loaded.Videos[0].Revision);
        Assert.Equal("", loaded.Videos[0].Meta.Title);
        Assert.Equal(entry.Meta.Genres, loaded.Videos[0].Meta.Genres);
        Assert.Equal(entry.Meta.ManualFieldLocks, loaded.Videos[0].Meta.ManualFieldLocks);
        Assert.Equal(library.Journal[0].ReceiptJson, Assert.Single(loaded.Journal).ReceiptJson);
        Assert.True(loaded.IsInRecycleBin(entry.Id));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("missing-entry")]
    [InlineData("missing-state")]
    [InlineData("bad-state")]
    [InlineData("bad-intent")]
    [InlineData("numeric-state")]
    [InlineData("duplicate-operation")]
    [InlineData("empty-operation")]
    [InlineData("unreferenced-entry")]
    [InlineData("wrong-snapshot")]
    [InlineData("missing-snapshot")]
    [InlineData("wrong-intent-state")]
    [InlineData("unproved-restored")]
    public async Task LoadAsync_DamagedJournalProtectsOriginalBytesAndRejectsSave(string corruption)
    {
        var library = CreateLibrary("journal"); var entry = library.Videos[0];
        library.Journal.Add(new LibraryJournalEntry { EntryId = entry.Id, BaseRevision = entry.Revision,
            OriginalPath = entry.Path, Snapshot = entry, Intent = LibraryOperationIntent.Recycle, State = LibraryOperationState.Pending });
        await CreateStore().SaveAsync(library);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(_libraryPath))!;
        var records = root["Journal"]!.AsArray(); var record = records[0]!.AsObject();
        switch (corruption)
        {
            case "empty": records[0] = new JsonObject(); break;
            case "missing-entry": record.Remove("EntryId"); break;
            case "missing-state": record.Remove("State"); break;
            case "bad-state": record["State"] = "unknown-state"; break;
            case "bad-intent": record["Intent"] = "unknown-intent"; break;
            case "numeric-state": record["State"] = 987; break;
            case "duplicate-operation": records.Add(record.DeepClone()); break;
            case "empty-operation": record["OperationId"] = Guid.Empty; break;
            case "unreferenced-entry": record["EntryId"] = Guid.NewGuid(); break;
            case "wrong-snapshot": record["Snapshot"]!["Id"] = Guid.NewGuid(); break;
            case "missing-snapshot": record.Remove("Snapshot"); break;
            case "wrong-intent-state": record["Intent"] = "Restore"; break;
            case "unproved-restored": record["State"] = "Restored"; break;
        }
        await File.WriteAllTextAsync(_libraryPath, root.ToJsonString());
        var original = await File.ReadAllBytesAsync(_libraryPath); var store = CreateStore();
        await Assert.ThrowsAnyAsync<JsonException>(() => store.LoadAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveAsync(CreateLibrary("replacement")));
        Assert.Equal(original, await File.ReadAllBytesAsync(_libraryPath));
    }

    private LibraryStore CreateStore(
        Action<LibraryStoreSaveStage, string, int>? saveStage = null,
        Action<LibraryStoreIoStage, int>? ioStage = null) =>
        new(_libraryPath, saveStage, ioStage);

    private async Task WriteOldLibraryAsync() =>
        await new LibraryStore(_libraryPath).SaveAsync(CreateLibrary("old"));

    private string[] GetOwnedTemps() =>
        Directory.GetFiles(_tempDirectory, ".videos.json.airi-tmp-*");

    private LibraryData CreateLibrary(string title) => new()
    {
        Version = 2,
        Videos = new List<VideoEntry>
        {
            new(LibraryPathHelper.NormalizeLibraryPath(_videoPath),
                new VideoMeta(title, null, Array.Empty<string>(), "resources/noimage.jpg", Array.Empty<string>(), string.Empty),
                1,
                DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc),
                DateTime.SpecifyKind(new DateTime(2026, 1, 1), DateTimeKind.Utc))
        }
    };
}
