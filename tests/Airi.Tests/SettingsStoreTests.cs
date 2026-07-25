using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Services;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class SettingsStoreTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "AiriSettingsTests", Guid.NewGuid().ToString("N"));

    private string LibraryPath => Path.Combine(_root, "videos.json");
    private string SettingsPath => Path.Combine(_root, "settings.json");

    [Fact]
    public async Task LoadAsync_MissingFileCreatesIndependentDefaultsBesideLibrary()
    {
        var store = new SettingsStore(LibraryPath);

        var settings = await store.LoadAsync();

        Assert.Equal(SettingsPath, store.FilePath);
        Assert.True(File.Exists(SettingsPath));
        Assert.False(File.Exists(LibraryPath));
        Assert.Equal(1, settings.Version);
        var target = Assert.Single(settings.Targets);
        Assert.Equal("./Videos", target.Root);
        Assert.Equal(new[] { "*.mp4", "*.mkv", "*.avi", "*.wmv" }, target.IncludePatterns);
        Assert.Empty(target.ExcludePatterns);
        Assert.Equal("ReleaseDate", settings.Sort.Field);
        Assert.Equal("Descending", settings.Sort.Direction);
    }

    [Fact]
    public async Task LoadAsync_UnknownSortNormalizesInMemoryWithoutRewritingFile()
    {
        var json = CreateSettingsJson(
            rootJson: "\"x:/missing-but-valid\"",
            field: "UnknownField",
            direction: "Sideways");
        await File.WriteAllTextAsync(SettingsPath, json);
        var before = await File.ReadAllBytesAsync(SettingsPath);

        var settings = await new SettingsStore(LibraryPath).LoadAsync();

        Assert.Equal("x:/missing-but-valid", Assert.Single(settings.Targets).Root);
        Assert.Equal("ReleaseDate", settings.Sort.Field);
        Assert.Equal("Descending", settings.Sort.Direction);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task LoadAsync_MissingMembersUseDefaultsWithoutRewritingFile()
    {
        await File.WriteAllTextAsync(SettingsPath, """{ "Version": 1 }""");
        var before = await File.ReadAllBytesAsync(SettingsPath);

        var settings = await new SettingsStore(LibraryPath).LoadAsync();

        Assert.Equal("./Videos", Assert.Single(settings.Targets).Root);
        Assert.Equal("ReleaseDate", settings.Sort.Field);
        Assert.Equal("Descending", settings.Sort.Direction);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task LoadAsync_InvalidJsonThrowsWithoutChangingEitherFile()
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            """{ "Targets": "wrong-type" }""");
    }

    [Fact]
    public async Task LoadAsync_EmptyTargetsThrowsWithoutChangingEitherFile()
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            """{ "Version": 1, "Targets": [], "Sort": {} }""");
    }

    [Fact]
    public async Task LoadAsync_MissingRootThrowsWithoutChangingEitherFile()
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            CreateSettingsJson(rootJson: null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"\"")]
    [InlineData("\"\\u0000\"")]
    public async Task LoadAsync_InvalidRootThrowsWithoutChangingEitherFile(string rootJson)
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            CreateSettingsJson(rootJson: rootJson));
    }

    [Fact]
    public async Task LoadAsync_MissingIncludePatternsThrowsWithoutChangingEitherFile()
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            CreateSettingsJson(includeJson: null));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("[\"\"]")]
    public async Task LoadAsync_InvalidIncludePatternsThrowsWithoutChangingEitherFile(
        string includeJson)
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            CreateSettingsJson(includeJson: includeJson));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task LoadAsync_MissingOrEmptyExcludePatternsNormalizesToEmptyWithoutRewritingFile(
        string? excludeJson)
    {
        var json = CreateSettingsJson(excludeJson: excludeJson);
        await File.WriteAllTextAsync(SettingsPath, json);
        var before = await File.ReadAllBytesAsync(SettingsPath);

        var settings = await new SettingsStore(LibraryPath).LoadAsync();

        Assert.Empty(Assert.Single(settings.Targets).ExcludePatterns);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task LoadAsync_BlankExcludePatternThrowsWithoutChangingEitherFile()
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            CreateSettingsJson(excludeJson: "[\"\"]"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task LoadAsync_NonPositiveVersionNormalizesToOneWithoutRewritingFile(int version)
    {
        var json = CreateSettingsJson(version: version);
        await File.WriteAllTextAsync(SettingsPath, json);
        var before = await File.ReadAllBytesAsync(SettingsPath);

        var settings = await new SettingsStore(LibraryPath).LoadAsync();

        Assert.Equal(1, settings.Version);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task LoadAsync_MissingVersionNormalizesToOneWithoutRewritingFile()
    {
        var json = CreateSettingsJson(version: null);
        await File.WriteAllTextAsync(SettingsPath, json);
        var before = await File.ReadAllBytesAsync(SettingsPath);

        var settings = await new SettingsStore(LibraryPath).LoadAsync();

        Assert.Equal(1, settings.Version);
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task LoadAsync_FutureVersionThrowsWithoutChangingEitherFile()
    {
        await AssertLoadThrowsWithoutChangingEitherFileAsync(
            CreateSettingsJson(version: 2));
    }

    [Fact]
    public async Task SaveAsync_CancelBeforeCommitPreservesPreviousSettings()
    {
        var normal = new SettingsStore(LibraryPath);
        await normal.SaveAsync(CreateSettings("ReleaseDate", "Descending"));
        var before = await File.ReadAllBytesAsync(SettingsPath);
        using var cancellation = new CancellationTokenSource();
        var store = new SettingsStore(LibraryPath, (stage, _, _) =>
        {
            if (stage == SettingsStoreSaveStage.AfterTemporaryFlush)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.SaveAsync(CreateSettings("Title", "Ascending"), cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath));
    }

    [Fact]
    public async Task SaveAsync_CancelAfterCommitGatePersistsNewSettings()
    {
        await new SettingsStore(LibraryPath)
            .SaveAsync(CreateSettings("ReleaseDate", "Descending"));
        using var cancellation = new CancellationTokenSource();
        var store = new SettingsStore(LibraryPath, (stage, _, _) =>
        {
            if (stage == SettingsStoreSaveStage.BeforeCommit)
            {
                cancellation.Cancel();
            }
        });

        await store.SaveAsync(CreateSettings("Title", "Ascending"), cancellation.Token);

        var loaded = await new SettingsStore(LibraryPath).LoadAsync();
        Assert.Equal("Title", loaded.Sort.Field);
        Assert.Equal("Ascending", loaded.Sort.Direction);
    }

    [Fact]
    public async Task SaveAsync_OverlappingCallsCommitInSelectionOrder()
    {
        var firstAtCommit = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var commits = 0;
        var store = new SettingsStore(LibraryPath, (stage, _, _) =>
        {
            if (stage == SettingsStoreSaveStage.BeforeCommit &&
                Interlocked.Increment(ref commits) == 1)
            {
                firstAtCommit.TrySetResult();
                releaseFirst.Task.GetAwaiter().GetResult();
            }
        });

        var first = store.SaveAsync(CreateSettings("ReleaseDate", "Ascending"));
        await firstAtCommit.Task;
        var second = store.SaveAsync(CreateSettings("Title", "Descending"));
        releaseFirst.TrySetResult();
        await Task.WhenAll(first, second);

        var loaded = await new SettingsStore(LibraryPath).LoadAsync();
        Assert.Equal("Title", loaded.Sort.Field);
        Assert.Equal("Descending", loaded.Sort.Direction);
    }

    [Fact]
    public async Task SaveAsync_LatestFailureLeavesEarlierSuccessfulSelection()
    {
        var attempts = 0;
        var store = new SettingsStore(LibraryPath, (stage, _, _) =>
        {
            if (stage == SettingsStoreSaveStage.BeforeTemporaryWrite &&
                Interlocked.Increment(ref attempts) == 2)
            {
                throw new IOException("latest failed");
            }
        });

        var first = store.SaveAsync(CreateSettings("ReleaseDate", "Ascending"));
        var second = store.SaveAsync(CreateSettings("Title", "Descending"));
        await first;
        await Assert.ThrowsAsync<IOException>(() => second);

        var loaded = await new SettingsStore(LibraryPath).LoadAsync();
        Assert.Equal("ReleaseDate", loaded.Sort.Field);
        Assert.Equal("Ascending", loaded.Sort.Direction);
    }

    [Fact]
    public async Task SaveAsync_SuccessAndFailureRemoveOwnedTemporaryFiles()
    {
        await new SettingsStore(LibraryPath)
            .SaveAsync(CreateSettings("ReleaseDate", "Descending"));
        var failing = new SettingsStore(LibraryPath, (stage, _, _) =>
        {
            if (stage == SettingsStoreSaveStage.BeforeCommit)
            {
                throw new IOException("commit failed");
            }
        });

        await Assert.ThrowsAsync<IOException>(
            () => failing.SaveAsync(CreateSettings("Title", "Ascending")));

        Assert.Empty(Directory.GetFiles(_root, ".settings.json.airi-tmp-*"));
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task AssertLoadThrowsWithoutChangingEitherFileAsync(string json)
    {
        await File.WriteAllTextAsync(SettingsPath, json);
        await File.WriteAllTextAsync(LibraryPath, """{ "sentinel": true }""");
        var settingsBefore = await File.ReadAllBytesAsync(SettingsPath);
        var videosBefore = await File.ReadAllBytesAsync(LibraryPath);

        await Assert.ThrowsAsync<JsonException>(
            () => new SettingsStore(LibraryPath).LoadAsync());

        Assert.Equal(settingsBefore, await File.ReadAllBytesAsync(SettingsPath));
        Assert.Equal(videosBefore, await File.ReadAllBytesAsync(LibraryPath));
    }

    private static AppSettings CreateSettings(string field, string direction) => new()
    {
        Targets = new List<TargetFolder>
        {
            new("./Videos", new[] { "*.mp4" }, Array.Empty<string>())
        },
        Sort = new SortSettings
        {
            Field = field,
            Direction = direction
        }
    };

    private static string CreateSettingsJson(
        int? version = 1,
        string? rootJson = "\"./Videos\"",
        string? includeJson = "[\"*.mp4\"]",
        string? excludeJson = "[]",
        string field = "ReleaseDate",
        string direction = "Descending")
    {
        var targetProperties = new List<string>();
        if (rootJson is not null)
        {
            targetProperties.Add($"\"Root\": {rootJson}");
        }
        if (includeJson is not null)
        {
            targetProperties.Add($"\"IncludePatterns\": {includeJson}");
        }
        if (excludeJson is not null)
        {
            targetProperties.Add($"\"ExcludePatterns\": {excludeJson}");
        }

        var versionProperty = version is null ? string.Empty : $"\"Version\": {version},";
        return $$"""
        {
          {{versionProperty}}
          "Targets": [
            {
              {{string.Join("," + Environment.NewLine, targetProperties)}}
            }
          ],
          "Sort": {
            "Field": "{{field}}",
            "Direction": "{{direction}}"
          }
        }
        """;
    }
}
