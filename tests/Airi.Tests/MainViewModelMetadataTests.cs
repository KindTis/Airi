using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainViewModelMetadataTests
{
    [MetadataFixtureFact]
    public Task Startup_EnrichesOnlyNewIdentifiedFilesAndRegistersUnidentifiedFilesOnce() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Copy("ABP-088.mp4");
        fixture.Copy("GsVFgYVFMQgsBLAe.mp4");
        fixture.Copy("ABP-383.mp4", "010203_004.mp4");
        fixture.Copy("ABP-383.mp4", "ABP-383 JUR-459.mp4");
        fixture.Copy("ABP-383.mp4", "ABP-383A.mp4");
        var cover = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"));
        await fixture.ConfigureScanAsync();
        Assert.Empty((await fixture.Store.LoadAsync()).Videos);
        var baselineCommits = fixture.Commits;
        var calls = new ConcurrentQueue<string>();
        WebVideoMetaResult FullResult() => new(Candidate("Provider title"), cover, ".jpg");
        var libre = new Source(VideoMetadataSourceKind.LibreDmm, calls,
            code => code.Value == "ABP-088" ? FullResult() : new(null));
        var regular = new Source(VideoMetadataSourceKind.JavBus, calls, _ => FullResult());
        var uncensored = new Source(VideoMetadataSourceKind.JavBusUncensored, calls, _ => new(null));

        using (var viewModel = fixture.Create(libre, regular, uncensored))
        {
            await viewModel.InitializeAsync();
            await WaitForAutoMetadataAsync(viewModel);
            var stored = (await fixture.Store.LoadAsync()).Videos;
            Assert.Equal(5, stored.Count);
            Assert.Equal(baselineCommits + 3, fixture.Commits);
            Assert.Equal(1, Entry(stored, "ABP-088.mp4").Revision);
            Assert.Equal("Provider title", Entry(stored, "ABP-088.mp4").Meta.Title);
            Assert.Empty(Entry(stored, "ABP-088.mp4").Meta.ManualFieldLocks);
            Assert.Equal("Studio", Entry(stored, "ABP-088.mp4").Meta.Studio);
            Assert.Equal("Studio", viewModel.Videos.Single(item => item.Id == Entry(stored, "ABP-088.mp4").Id).Studio);
            Assert.Equal(1, Entry(stored, "010203_004.mp4").Revision);
            Assert.All(stored.Where(entry => entry.Revision == 0), entry => Assert.Null(entry.Meta.Studio));
            Assert.Equal(0, Entry(stored, "GsVFgYVFMQgsBLAe.mp4").Revision);
            Assert.Equal(0, Entry(stored, "ABP-383 JUR-459.mp4").Revision);
            Assert.Equal(0, Entry(stored, "ABP-383A.mp4").Revision);
            Assert.Equal(new[] { "LibreDmm:ABP-088" }, calls.Where(call => call.EndsWith(":ABP-088", StringComparison.Ordinal)));
            Assert.Equal(new[] { "LibreDmm:010203-004", "JavBusUncensored:010203-004", "JavBus:010203-004" },
                calls.Where(call => call.EndsWith(":010203-004", StringComparison.Ordinal)));
            Assert.Equal(4, calls.Count);
        }

        calls.Clear();
        using var reopened = fixture.Create(libre, regular, uncensored);
        await reopened.InitializeAsync();
        await WaitForAutoMetadataAsync(reopened);
        Assert.Empty(calls);
        Assert.Equal(1, Entry((await fixture.Store.LoadAsync()).Videos, "ABP-088.mp4").Revision);
        Assert.Equal(0, Entry((await fixture.Store.LoadAsync()).Videos, "GsVFgYVFMQgsBLAe.mp4").Revision);
    });

    [MetadataFixtureFact]
    public Task Gallery_UsesCheckedAndCardTargetsAndFillsSingleRefreshFields() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var failed = fixture.Copy("ABP-383.mp4");
        var checkedVideo = fixture.Copy("ABP-398.mp4");
        var card = fixture.Copy("ABP-445.mp4");
        await fixture.ConfigureScanAsync();
        await fixture.Store.SaveAsync(new LibraryData
        {
            Videos = new()
            {
                LocalEntry(failed, "Unavailable title"),
                LocalEntry(checkedVideo, "ABP-398") with
                {
                    Meta = new("ABP-398", null, [], "", [], "Existing notes")
                    { RawDate = "legacy date", ManualFieldLocks = [MetadataField.Studio] }
                },
                LocalEntry(card, "Card title")
            }
        });
        var calls = new ConcurrentQueue<string>();
        var source = new Source(VideoMetadataSourceKind.LibreDmm, calls,
            code => code.Value == "ABP-383"
                ? new(null, Detail: "fixture unavailable", Failed: true)
                : new(Candidate($"New {code.Value}") with { Description = "" }));
        var translation = new RecordingTranslation();
        using var viewModel = fixture.Create(translation, source);
        var window = new MainWindow(viewModel);
        try
        {
            await viewModel.InitializeAsync();
            var initialJson = await File.ReadAllTextAsync(fixture.Store.FilePath);
            var initialCommits = fixture.Commits;
            var registered = (await fixture.Store.LoadAsync()).Videos;
            var failedEntry = Entry(registered, "ABP-383.mp4");
            var checkedEntry = Entry(registered, "ABP-398.mp4");
            var cardEntry = Entry(registered, "ABP-445.mp4");
            var failedItem = viewModel.Videos.Single(item => item.Id == failedEntry.Id);
            var checkedItem = viewModel.Videos.Single(item => item.Id == checkedEntry.Id);
            var cardItem = viewModel.Videos.Single(item => item.Id == cardEntry.Id);
            failedItem.IsChecked = true;
            checkedItem.IsChecked = true;
            viewModel.FetchMetadataCommand.Execute(null);
            await viewModel.GalleryActionTask;
            Assert.Equal(new[] { "LibreDmm:ABP-383", "LibreDmm:ABP-398" }, calls);
            Assert.Collection(viewModel.EditorDraft!.TargetResults,
                result => Assert.Equal("조회 실패", result.Outcome),
                result => Assert.Equal("부분 후보", result.Outcome));
            Assert.Equal(initialJson, await File.ReadAllTextAsync(fixture.Store.FilePath));
            viewModel.ApplyChangesCommand.Execute(null);
            await viewModel.GalleryActionTask;
            Assert.Equal(initialCommits + 1, fixture.Commits);
            var afterBatch = (await fixture.Store.LoadAsync()).Videos;
            var updated = Entry(afterBatch, "ABP-398.mp4");
            Assert.Equal(checkedEntry.Revision + 1, updated.Revision);
            Assert.Equal("New ABP-398", updated.Meta.Title);
            Assert.Null(updated.Meta.RawDate);
            Assert.Equal(new DateOnly(2024, 1, 2), updated.Meta.Date);
            Assert.Null(updated.Meta.Studio);
            Assert.Contains(MetadataField.Studio, updated.Meta.ManualFieldLocks);
            Assert.Equal(new[] { "Actor" }, updated.Meta.Actors);
            Assert.Equal(failedEntry.Revision, Entry(afterBatch, "ABP-383.mp4").Revision);
            Assert.Equal(cardEntry.Revision, Entry(afterBatch, "ABP-445.mp4").Revision);

            failedItem.IsChecked = true;
            checkedItem.IsChecked = true;
            calls.Clear();
            var menu = window.CreateGalleryCardMenu(cardItem);
            var enrich = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, "메타데이터 갱신"));
            enrich.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent, enrich));
            await viewModel.GalleryActionTask;
            Assert.Equal(new[] { "LibreDmm:ABP-445" }, calls);
            Assert.Equal(cardEntry.Id, Assert.Single(viewModel.EditorDraft!.Targets).Id);
            Assert.False(viewModel.IsChangePreview);
            Assert.Equal(initialCommits + 1, fixture.Commits);
            viewModel.ApplyChangesCommand.Execute(null);
            await viewModel.GalleryActionTask;
            Assert.Equal(cardEntry.Revision + 1, Entry((await fixture.Store.LoadAsync()).Videos, "ABP-445.mp4").Revision);

            viewModel.SelectedVideo = checkedItem;
            await viewModel.BeginGalleryEditAsync();
            viewModel.FetchMetadataCommand.Execute(null);
            await viewModel.GalleryActionTask;
            var fields = viewModel.EditorDraft!.Fields;
            var title = fields.Single(field => field.Field == MetadataField.Title);
            var studio = fields.Single(field => field.Field == MetadataField.Studio);
            Assert.Equal("New ABP-398", title.Value);
            Assert.Empty(studio.Value);
            Assert.True(studio.IsLocked);
            var description = fields.Single(field => field.Field == MetadataField.Description);
            Assert.Equal("Existing notes", description.Value);
            description.Value = "Typed description";
            Assert.True(description.IsLocked);
            viewModel.TranslateDraftDescriptionCommand.Execute(null);
            await viewModel.GalleryActionTask;
            Assert.Equal(new[] { ("Typed description", "KO") }, translation.Calls);
            Assert.Equal("Translated description", description.Value);
            Assert.False(description.IsLocked);
            viewModel.CancelEditCommand.Execute(null);
            Assert.Equal(initialCommits + 2, fixture.Commits);

            viewModel.SelectedVideo = checkedItem;
            await viewModel.BeginGalleryEditAsync();
            viewModel.EditorDraft!.Fields.Single(field => field.Field == MetadataField.Studio).IsLocked = false;
            viewModel.FetchMetadataCommand.Execute(null);
            await viewModel.GalleryActionTask;
            Assert.False(viewModel.IsChangePreview);
            Assert.True(viewModel.ApplyChangesCommand.CanExecute(null));
            viewModel.ApplyChangesCommand.Execute(null);
            await viewModel.GalleryActionTask;
            var refreshed = Entry((await fixture.Store.LoadAsync()).Videos, "ABP-398.mp4");
            Assert.Equal(initialCommits + 3, fixture.Commits);
            Assert.Equal("New ABP-398", refreshed.Meta.Title);
            Assert.Equal("Studio", refreshed.Meta.Studio);
            Assert.Equal(new DateOnly(2024, 1, 2), refreshed.Meta.Date);
            Assert.Null(refreshed.Meta.RawDate);
            Assert.Equal("Existing notes", refreshed.Meta.Description);
            Assert.Empty(refreshed.Meta.ManualFieldLocks);
            Assert.Equal(updated.Revision + 1, refreshed.Revision);
        }
        finally
        {
            viewModel.CancelEditCommand.Execute(null);
            window.Close();
        }
    });

    [MetadataFixtureFact]
    public Task F1Refresh_FillsTheDraftAndCoverAndSavesWithTheVisibleButton() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var path = fixture.Copy("ABP-088.mp4", "ARAN-117.mp4");
        var original = LocalEntry(path, "ARAN-117");
        await fixture.ConfigureScanAsync();
        await fixture.Store.SaveAsync(new LibraryData { Videos = [original] });
        var cover = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"));
        var source = new Source(VideoMetadataSourceKind.LibreDmm, new(),
            _ => new(Candidate("ARAN-117") with { Actors = ["Actor, Jr."] }, cover, ".jpg"));
        using var viewModel = fixture.Create(source);
        var window = new MainWindow(viewModel);
        try
        {
            await viewModel.InitializeAsync();
            var initialJson = await File.ReadAllTextAsync(fixture.Store.FilePath);
            var initialCommits = fixture.Commits;

            async Task<(GalleryEditorDraft Draft, string Cover)> RefreshAsync()
            {
                viewModel.SelectedVideo = Assert.Single(viewModel.Videos);
                await viewModel.BeginGalleryEditAsync();
                var draft = Assert.IsType<GalleryEditorDraft>(viewModel.EditorDraft);
                draft.Fields.Single(field => field.Field == MetadataField.Title).Value = "Unsaved edit";
                viewModel.FetchMetadataCommand.Execute(null);
                await viewModel.GalleryActionTask;
                Assert.Equal("Unsaved edit", draft.Fields.Single(field => field.Field == MetadataField.Title).Value);
                Assert.Equal("Description", draft.Fields.Single(field => field.Field == MetadataField.Description).Value);
                var thumbnail = draft.Fields.Single(field => field.Field == MetadataField.Thumbnail).Value;
                Assert.NotEmpty(thumbnail);
                Assert.Equal(thumbnail, draft.ThumbnailEditor.ThumbnailPath);
                var coverPath = new Uri(draft.ThumbnailEditor.ThumbnailPreviewUri).LocalPath;
                Assert.Equal(LibraryPathHelper.ResolveToAbsolute(thumbnail), coverPath);
                Assert.True(File.Exists(coverPath));
                WpfTestHost.DrainDispatcher(window.Dispatcher);
                Assert.True(window.GalleryEditorWindowForTests.EditorThumbnailPreview.Source?.IsFrozen);
                Assert.False(viewModel.IsChangePreview);
                Assert.Equal(initialJson, await File.ReadAllTextAsync(fixture.Store.FilePath));
                return (draft, coverPath);
            }

            var cancelled = await RefreshAsync();
            viewModel.CancelEditCommand.Execute(null);
            Assert.False(File.Exists(cancelled.Cover));
            Assert.Equal(initialCommits, fixture.Commits);

            var filled = await RefreshAsync();
            var editor = window.GalleryEditorWindowForTests;
            Assert.Equal(Visibility.Collapsed, editor.EditorPreview.Visibility);
            Assert.Equal(Visibility.Visible, editor.EditorApply.Visibility);
            Assert.Equal("저장", editor.EditorApply.Content);
            var save = editor.EditorApply.Command;
            Assert.True(save.CanExecute(null));
            save.Execute(null);
            await viewModel.GalleryActionTask;

            var stored = Assert.Single((await fixture.Store.LoadAsync()).Videos);
            Assert.Equal(initialCommits + 1, fixture.Commits);
            Assert.Equal(1, stored.Revision);
            Assert.Equal("Unsaved edit", stored.Meta.Title);
            Assert.Equal([MetadataField.Title], stored.Meta.ManualFieldLocks);
            Assert.Equal("Description", stored.Meta.Description);
            Assert.Equal(["Actor, Jr."], stored.Meta.Actors);
            Assert.Equal(filled.Cover, LibraryPathHelper.ResolveToAbsolute(stored.Meta.Thumbnail));
            Assert.True(File.Exists(filled.Cover));
        }
        finally { viewModel.CancelEditCommand.Execute(null); window.Close(); }
    });

    [MetadataFixtureFact]
    public Task Commit_StaleOrCancelledCandidatesDiscardOnlyTheirOwnCover() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        var first = LocalEntry(fixture.Copy("ABP-088.mp4"), "ABP-088");
        var second = LocalEntry(fixture.Copy("JUR-459.mp4"), "JUR-459");
        await fixture.ConfigureScanAsync();
        await fixture.Store.SaveAsync(new LibraryData { Videos = [first, second] });
        var cover = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"));
        var source = new Source(VideoMetadataSourceKind.LibreDmm, new ConcurrentQueue<string>(),
            _ => new(Candidate("Provider title"), cover, ".jpg"));
        var service = fixture.CreateService(NullTranslationService.Instance, source);
        using var viewModel = fixture.Create(service);
        await viewModel.InitializeAsync();
        var lease = Assert.IsType<LibraryMutationLease>(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata));

        async Task<VideoEntry> GetCandidateAsync(VideoEntry entry)
        {
            var result = await service.FetchEnrichmentAsync(entry, CancellationToken.None);
            var candidate = Assert.IsType<VideoEntry>(result.Entry);
            Assert.True(File.Exists(LibraryPathHelper.ResolveToAbsolute(candidate.Meta.Thumbnail)));
            return candidate;
        }

        var accepted = await GetCandidateAsync(first);
        await viewModel.CommitEntryCandidateAsync(first, accepted, lease);
        var committed = await File.ReadAllTextAsync(fixture.Store.FilePath);
        var commits = fixture.Commits;
        var stale = await GetCandidateAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.CommitEntryCandidateAsync(first, stale, lease));
        Assert.False(File.Exists(LibraryPathHelper.ResolveToAbsolute(stale.Meta.Thumbnail)));

        var cancelled = await GetCandidateAsync(second);
        viewModel.Dispose();
        Assert.True(viewModel.LifetimeToken.IsCancellationRequested);
        var stopped = await Record.ExceptionAsync(() => viewModel.CommitEntryCandidateAsync(second, cancelled, lease));
        Assert.True(stopped is OperationCanceledException or InvalidOperationException);
        Assert.False(File.Exists(LibraryPathHelper.ResolveToAbsolute(cancelled.Meta.Thumbnail)));
        Assert.True(File.Exists(LibraryPathHelper.ResolveToAbsolute(accepted.Meta.Thumbnail)));
        Assert.Equal(commits, fixture.Commits);
        Assert.Equal(committed, await File.ReadAllTextAsync(fixture.Store.FilePath));
    });

    [MetadataFixtureFact]
    public Task StopMetadataForShutdownAsync_WaitsForCleanupAndSkipsQueuedLookups() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Copy("ABP-088.mp4");
        fixture.Copy("ABP-383.mp4");
        await fixture.ConfigureScanAsync();
        var source = new DelayedMetadataSource();
        using var viewModel = fixture.Create(source);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await viewModel.InitializeAsync(timeout.Token);

        try
        {
            await source.Started.Task.WaitAsync(timeout.Token);
            viewModel.InspectedVideo = viewModel.Videos.First();
            viewModel.FetchMetadataCommand.Execute(viewModel.InspectedVideo);
            Assert.False(viewModel.GalleryActionTask.IsCompleted);
            var stopping = viewModel.StopMetadataForShutdownAsync();
            await source.CancellationObserved.Task.WaitAsync(timeout.Token);
            Assert.False(stopping.IsCompleted);

            source.Cleanup.TrySetResult();
            await stopping.WaitAsync(timeout.Token);
            await viewModel.GalleryActionTask.WaitAsync(timeout.Token);
            Assert.Equal(1, source.Calls);
            Assert.False(viewModel.IsFetchingMetadata);
            Assert.False(viewModel.IsEditing);
            var stored = (await fixture.Store.LoadAsync()).Videos;
            Assert.Equal(2, stored.Count);
            Assert.All(stored, entry => Assert.Equal(0, entry.Revision));
        }
        finally
        {
            source.Cleanup.TrySetResult();
        }
    });

    [MetadataFixtureFact]
    public Task StopMetadataForShutdownAsync_BeforeScanPreventsLateLookup() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        fixture.Copy("ABP-088.mp4");
        await fixture.ConfigureScanAsync();
        var calls = new ConcurrentQueue<string>();
        using var viewModel = fixture.Create(new Source(VideoMetadataSourceKind.LibreDmm, calls,
            _ => new(Candidate("Provider title"))));

        await viewModel.StopMetadataForShutdownAsync();
        await viewModel.InitializeAsync();

        Assert.Empty(calls);
        Assert.False(viewModel.IsFetchingMetadata);
        Assert.Equal(0, Assert.Single((await fixture.Store.LoadAsync()).Videos).Revision);
    });

    private sealed class DelayedMetadataSource : IWebVideoMetaSource
    {
        public VideoMetadataSourceKind Kind => VideoMetadataSourceKind.LibreDmm;
        public string Name => "Delayed test source";
        public string Host => "fixture.invalid";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleanup { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }

        public async Task<WebVideoMetaResult> FetchAsync(MetadataProductCode code, bool includeThumbnail,
            ISet<string> blockedHosts, CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await Cleanup.Task;
            }

            return new(Candidate("Late candidate"));
        }
    }

    private static VideoMeta Candidate(string title) => new(title, new DateOnly(2024, 1, 2), ["Actor"], "", ["Tag"], "Description")
    {
        Studio = "Studio", Genres = ["Genre"]
    };

    private static VideoEntry LocalEntry(string path, string title)
    {
        var file = new FileInfo(path);
        return new VideoEntry(LibraryPathHelper.NormalizeLibraryPath(path), new(title, null, [], "", [], ""),
            file.Length, file.LastWriteTimeUtc, file.CreationTimeUtc) { Presence = LibraryFilePresence.Available };
    }

    private static VideoEntry Entry(IEnumerable<VideoEntry> entries, string fileName) =>
        entries.Single(entry => Path.GetFileName(entry.Path) == fileName);

    private static async Task WaitForAutoMetadataAsync(MainViewModel viewModel)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (viewModel.IsFetchingMetadata)
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
    }

    private sealed class Source(VideoMetadataSourceKind kind, ConcurrentQueue<string> calls,
        Func<MetadataProductCode, WebVideoMetaResult> respond) : IWebVideoMetaSource
    {
        public VideoMetadataSourceKind Kind => kind;
        public string Name => kind.ToString();
        public string Host => kind == VideoMetadataSourceKind.LibreDmm ? "libre.fixture.invalid" : "bus.fixture.invalid";

        public Task<WebVideoMetaResult> FetchAsync(MetadataProductCode code, bool includeThumbnail,
            ISet<string> blockedHosts, CancellationToken cancellationToken)
        {
            calls.Enqueue($"{Kind}:{code.Value}");
            return Task.FromResult(respond(code));
        }
    }

    private sealed class RecordingTranslation : ITextTranslationService
    {
        public bool IsEnabled => true;
        public List<(string Text, string TargetLanguage)> Calls { get; } = [];

        public Task<string?> TranslateAsync(string text, string? sourceLanguageCode, string targetLanguageCode,
            CancellationToken cancellationToken)
        {
            Calls.Add((text, targetLanguageCode));
            return Task.FromResult<string?>("Translated description");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AiriMetadataTests", Guid.NewGuid().ToString("N"));
        private readonly string _videos;
        private int _commits;

        public Fixture()
        {
            _videos = Path.Combine(_root, "videos");
            Directory.CreateDirectory(_videos);
            Store = new LibraryStore(Path.Combine(_root, "videos.json"), (stage, _, _) =>
            {
                if (stage == LibraryStoreSaveStage.AfterCommit)
                    Interlocked.Increment(ref _commits);
            }, null);
        }

        public LibraryStore Store { get; }
        public int Commits => Volatile.Read(ref _commits);

        public string Copy(string fileName, string? alias = null)
        {
            var copy = Path.Combine(_videos, alias ?? fileName);
            File.Copy(Path.Combine(MetadataFixtureFactAttribute.Videos!, fileName), copy);
            return copy;
        }

        public Task ConfigureScanAsync() => new SettingsStore(Store.FilePath).SaveAsync(new AppSettings
        {
            Targets = [new TargetFolder(_videos, ["*.mp4"], [])]
        });

        public MainViewModel Create(params IWebVideoMetaSource[] sources) => Create(NullTranslationService.Instance, sources);

        public WebMetadataService CreateService(ITextTranslationService translation, params IWebVideoMetaSource[] sources) =>
            new(sources, new ThumbnailCache(_root), translation, "KO");

        public MainViewModel Create(ITextTranslationService translation, params IWebVideoMetaSource[] sources) =>
            Create(CreateService(translation, sources));

        public MainViewModel Create(WebMetadataService service) =>
            new(Store, new LibraryScanner(new FileSystemScanner()), service, new TestThumbnailImageLoader());

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}

public sealed class MetadataFixtureFactAttribute : FactAttribute
{
    private static readonly string[] FileNames = ["ABP-088.mp4", "ABP-383.mp4", "ABP-398.mp4", "ABP-445.mp4",
        "JUR-459.mp4", "GsVFgYVFMQgsBLAe.mp4"];

    internal static string? Videos { get; } = FindVideos();

    public MetadataFixtureFactAttribute()
    {
        if (Videos is null)
            Skip = "The approved local AIRI-3 dummy videos are unavailable.";
    }

    private static string? FindVideos()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var videos = Path.Combine(directory.FullName, "bin", "Debug", "net9.0-windows10.0.26100.0", "videos");
            if (FileNames.All(name => File.Exists(Path.Combine(videos, name))))
                return videos;
        }
        return null;
    }
}
