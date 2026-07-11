using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainViewModelThumbnailTests
{
    [Fact]
    public Task RequestThumbnailAsync_SameItemAndKey_DoesNotDuplicateLoaderCall() => Run(async fixture =>
    {
        var item = fixture.CreateItem("same.jpg");
        var first = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        var duplicate = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        Assert.Single(fixture.Loader.Calls);
        Assert.True(duplicate.IsCompletedSuccessfully);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await first;
    });

    [Fact]
    public Task ReleaseThumbnail_InFlight_CancelsRequest() => Run(async fixture =>
    {
        var item = fixture.CreateItem("cancel.jpg");
        var request = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.ViewModel.ReleaseThumbnail(item);
        Assert.True(fixture.Loader.Calls[0].Token.IsCancellationRequested);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await request;
        Assert.Equal(ThumbnailLoadState.NotRequested, item.ThumbnailLoadState);
    });

    [Fact]
    public Task DecodedOwnerGauge_LoadedItemEntersAndReleaseLeavesOwnerSlot() => Run(async fixture =>
    {
        var item = fixture.CreateItem("owner.jpg");
        var request = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await request;
        Assert.Equal(1, fixture.Probe.GetDecodedStrongReferenceOwnerCount());

        fixture.ViewModel.ReleaseThumbnail(item);
        Assert.Equal(0, fixture.Probe.GetDecodedStrongReferenceOwnerCount());
    });

    [Fact]
    public Task RequestThumbnailAsync_PathChanged_DropsLateOldResult() => Run(async fixture =>
    {
        var item = fixture.CreateItem("old.jpg");
        var old = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        item.ThumbnailPath = "new.jpg";
        var current = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await old;
        Assert.NotSame(fixture.DecodedA, item.ThumbnailSource);
        fixture.Loader.Complete(1, fixture.DecodedB, false);
        await current;
        Assert.Same(fixture.DecodedB, item.ThumbnailSource);
    });

    [Fact]
    public Task RequestThumbnailAsync_RecycledAThenB_NeverAppliesAToB() => Run(async fixture =>
    {
        var a = fixture.CreateItem("a.jpg");
        var b = fixture.CreateItem("b.jpg");
        var aTask = fixture.ViewModel.RequestThumbnailAsync(a, 200);
        fixture.ViewModel.ReleaseThumbnail(a);
        var bTask = fixture.ViewModel.RequestThumbnailAsync(b, 200);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        fixture.Loader.Complete(1, fixture.DecodedB, false);
        await Task.WhenAll(aTask, bTask);
        Assert.Same(fixture.DecodedB, b.ThumbnailSource);
        Assert.NotSame(fixture.DecodedA, b.ThumbnailSource);
    });

    [Fact]
    public Task RequestThumbnailAsync_FallbackResult_SetsFailed() => Run(async fixture =>
    {
        var item = fixture.CreateItem("fallback.jpg");
        var task = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.Loader.FallbackSource, true);
        await task;
        Assert.Equal(ThumbnailLoadState.Failed, item.ThumbnailLoadState);
        Assert.Same(fixture.Loader.FallbackSource, item.ThumbnailSource);
    });

    [Fact]
    public Task RequestThumbnailAsync_CurrentCancellation_SetsNotRequested() => Run(async fixture =>
    {
        fixture.Loader.CancelCompletesCalls = true;
        using var cancellation = new CancellationTokenSource();
        var item = fixture.CreateItem("external-cancel.jpg");
        var task = fixture.ViewModel.RequestThumbnailAsync(item, 200, cancellation.Token);
        cancellation.Cancel();
        await task;
        var diagnostics = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(item);
        Assert.Equal(ThumbnailLoadState.NotRequested, item.ThumbnailLoadState);
        Assert.Equal("Cancelled", diagnostics.Outcome);
        Assert.False(diagnostics.HasInFlight);
    });

    [Fact]
    public Task ReleaseThumbnail_LoadedItem_ReleasesDecodedSource() => Run(async fixture =>
    {
        var item = fixture.CreateItem("loaded.jpg");
        var task = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await task;
        fixture.ViewModel.ReleaseThumbnail(item);
        Assert.Equal(ThumbnailLoadState.NotRequested, item.ThumbnailLoadState);
        Assert.Same(fixture.Loader.FallbackSource, item.ThumbnailSource);
    });

    [Fact]
    public Task ReleaseThumbnail_BetweenDecodeAndDispatcher_DropsCompletion() => Run(async fixture =>
    {
        var item = fixture.CreateItem("race.jpg");
        var task = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        await Task.Run(() => fixture.Loader.Complete(0, fixture.DecodedA, false));
        fixture.ViewModel.ReleaseThumbnail(item);
        await task;
        Assert.Equal(ThumbnailLoadState.NotRequested, item.ThumbnailLoadState);
        Assert.NotSame(fixture.DecodedA, item.ThumbnailSource);
    });

    [Fact]
    public Task RequestThumbnailAsync_ReenterAfterRelease_DropsAllOldCallbacks() => Run(async fixture =>
    {
        var item = fixture.CreateItem("reenter.jpg");
        var old = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.ViewModel.ReleaseThumbnail(item);
        var current = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await old;
        fixture.Loader.Complete(1, fixture.DecodedB, false);
        await current;
        Assert.Same(fixture.DecodedB, item.ThumbnailSource);
    });

    [Fact]
    public Task RequestThumbnailAsync_MissingOriginalIgnoresThumbnailUriThenRetriesAfterFileAppears() => Run(async fixture =>
    {
        var item = fixture.CreateItem("missing-original.jpg");
        item.ThumbnailUri = new Uri(fixture.FallbackFile).AbsoluteUri;
        var first = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        Assert.EndsWith("missing-original.jpg", fixture.Loader.Calls[0].Path, StringComparison.OrdinalIgnoreCase);
        fixture.Loader.Complete(0, fixture.Loader.FallbackSource, true);
        await first;
        fixture.ViewModel.ReleaseThumbnail(item);
        var second = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(1, fixture.DecodedA, false);
        await second;
        Assert.Equal(ThumbnailLoadState.Loaded, item.ThumbnailLoadState);
    });

    [Fact]
    public Task RequestThumbnailAsync_WidthChanged_StartsNewGeneration() => Run(async fixture =>
    {
        var item = fixture.CreateItem("width.jpg");
        var first = fixture.ViewModel.RequestThumbnailAsync(item, 160);
        var generation = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(item).Generation;
        var second = fixture.ViewModel.RequestThumbnailAsync(item, 260);
        Assert.Equal(2, fixture.Loader.Calls.Count);
        Assert.True(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(item).Generation > generation);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        fixture.Loader.Complete(1, fixture.DecodedB, false);
        await Task.WhenAll(first, second);
        Assert.Same(fixture.DecodedB, item.ThumbnailSource);
    });

    [Fact]
    public Task ApplyMetadataToCollections_Replace_CleansOldRuntimeAndSelection() => Run(async fixture =>
    {
        var old = fixture.CreateItem("replace-old.jpg", "./Videos/replace.mp4");
        fixture.Seed(old);
        fixture.ViewModel.SelectedVideo = old;
        var request = fixture.ViewModel.RequestThumbnailAsync(old, 200);
        var updated = new VideoEntry(
            old.LibraryPath,
            new VideoMeta("Updated", null, Array.Empty<string>(), "replace-new.jpg", Array.Empty<string>(), string.Empty));
        fixture.InvokeApplyMetadata(updated);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await request;
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(old).Exists);
        Assert.NotSame(old, fixture.ViewModel.SelectedVideo);
        Assert.Equal("Updated", fixture.ViewModel.SelectedVideo!.Title);
    });

    [Fact]
    public Task RequestThumbnailAsync_FailedDuplicateEvent_DoesNotRetryWithinRealization() => Run(async fixture =>
    {
        var item = fixture.CreateItem("failed.jpg");
        var first = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.Loader.FallbackSource, true);
        await first;
        await fixture.ViewModel.RequestThumbnailAsync(item, 200);
        Assert.Single(fixture.Loader.Calls);
    });

    [Fact]
    public Task RequestThumbnailAsync_FailedAfterRelease_RetriesExactlyOnce() => Run(async fixture =>
    {
        var item = fixture.CreateItem("retry.jpg");
        var first = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        fixture.Loader.Complete(0, fixture.Loader.FallbackSource, true);
        await first;
        fixture.ViewModel.ReleaseThumbnail(item);
        var retry = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        Assert.Equal(2, fixture.Loader.Calls.Count);
        fixture.Loader.Complete(1, fixture.DecodedA, false);
        await retry;
    });

    [Fact]
    public Task RequestThumbnailAsync_AllTerminalOutcomes_DisposeInFlightButKeepTracking() => Run(async fixture =>
    {
        var loaded = fixture.CreateItem("terminal-loaded.jpg");
        var loadedTask = fixture.ViewModel.RequestThumbnailAsync(loaded, 200);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        await loadedTask;
        var loadedState = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(loaded);
        Assert.Equal("Loaded", loadedState.Outcome);
        Assert.False(loadedState.HasInFlight);

        var failed = fixture.CreateItem("terminal-failed.jpg");
        var failedTask = fixture.ViewModel.RequestThumbnailAsync(failed, 200);
        fixture.Loader.Complete(1, fixture.Loader.FallbackSource, true);
        await failedTask;
        var failedState = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(failed);
        Assert.Equal("Failed", failedState.Outcome);
        Assert.False(failedState.HasInFlight);
    });

    [Fact]
    public Task RequestThumbnailAsync_ReentryGenerationIsMonotonic() => Run(async fixture =>
    {
        var item = fixture.CreateItem("generation.jpg");
        var first = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        var firstGeneration = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(item).Generation;
        fixture.ViewModel.ReleaseThumbnail(item);
        var afterRelease = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(item).Generation;
        var second = fixture.ViewModel.RequestThumbnailAsync(item, 200);
        var secondGeneration = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(item).Generation;
        Assert.True(firstGeneration < afterRelease && afterRelease < secondGeneration);
        fixture.Loader.Complete(0, fixture.DecodedA, false);
        fixture.Loader.Complete(1, fixture.DecodedB, false);
        await Task.WhenAll(first, second);
    });

    [Fact]
    public Task CollectionRemoveReplaceAndDispose_RemoveAllRuntimeState() => Run(async fixture =>
    {
        var removed = fixture.CreateItem("removed.jpg");
        fixture.ViewModel.Videos.Add(removed);
        _ = fixture.ViewModel.GetOrCreateThumbnailRuntimeIdentity(removed);
        fixture.ViewModel.Videos.Remove(removed);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(removed).Exists);

        var retained = fixture.CreateItem("disposed.jpg");
        fixture.ViewModel.Videos.Add(retained);
        _ = fixture.ViewModel.GetOrCreateThumbnailRuntimeIdentity(retained);
        fixture.ViewModel.Dispose();
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(retained).Exists);
        await Task.CompletedTask;
    });

    private static Task Run(Func<Fixture, Task> test) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await test(fixture);
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture()
        {
            _root = Path.Combine(Path.GetTempPath(), "AiriMainViewModelThumbnailTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Loader = new FakeLoader();
            Probe = ThumbnailPerformanceProbe.CreateEnabled();
            DecodedA = CreateBitmap(0x44);
            DecodedB = CreateBitmap(0x88);
            FallbackFile = Path.Combine(_root, "fallback.jpg");
            File.WriteAllBytes(FallbackFile, new byte[] { 1 });

            var provider = new CrawlerSessionProvider();
            var source = new OneFourOneJavMetaSource(provider);
            var metadata = new WebMetadataService(
                new IWebVideoMetaSource[] { new NoopMetaSource() },
                new ThumbnailCache(_root),
                NullTranslationService.Instance,
                "KO");
            ViewModel = new MainViewModel(
                new LibraryStore(Path.Combine(_root, "videos.json")),
                new LibraryScanner(new FileSystemScanner()),
                metadata,
                provider,
                source,
                new NoopCrawlerFactory(),
                Probe,
                Loader);
        }

        public MainViewModel ViewModel { get; }
        public FakeLoader Loader { get; }
        public ThumbnailPerformanceProbe Probe { get; }
        public ImageSource DecodedA { get; }
        public ImageSource DecodedB { get; }
        public string FallbackFile { get; }

        public VideoItem CreateItem(string thumbnailPath, string? libraryPath = null)
        {
            var item = new VideoItem
            {
                LibraryPath = libraryPath ?? $"./Videos/{Guid.NewGuid():N}.mp4",
                Title = "Video",
                ThumbnailPath = thumbnailPath,
                ThumbnailUri = new Uri(FallbackFile).AbsoluteUri
            };
            item.ReleaseThumbnail(Loader.FallbackSource);
            return item;
        }

        public void Seed(VideoItem item)
        {
            ViewModel.Videos.Add(item);
            var library = new LibraryData
            {
                Videos = new List<VideoEntry>
                {
                    new(item.LibraryPath,
                        new VideoMeta(item.Title, null, Array.Empty<string>(), item.ThumbnailPath, Array.Empty<string>(), string.Empty))
                }
            };
            typeof(MainViewModel).GetField("_library", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ViewModel, library);
            typeof(MainViewModel).GetMethod("RegisterVideo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ViewModel, new object[] { item });
        }

        public void InvokeApplyMetadata(VideoEntry entry) =>
            typeof(MainViewModel).GetMethod("ApplyMetadataToCollections", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(ViewModel, new object[] { entry });

        public void Dispose()
        {
            ViewModel.Dispose();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static ImageSource CreateBitmap(byte value)
        {
            var pixels = Enumerable.Repeat(value, 16 * 10 * 4).ToArray();
            var bitmap = BitmapSource.Create(16, 10, 96, 96, PixelFormats.Bgra32, null, pixels, 16 * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private sealed class FakeLoader : IThumbnailImageLoader
    {
        public FakeLoader()
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            bitmap.Freeze();
            FallbackSource = bitmap;
        }

        public ImageSource FallbackSource { get; }
        public List<Call> Calls { get; } = new();
        public bool CancelCompletesCalls { get; set; }

        public Task<ThumbnailImageResult> LoadAsync(string thumbnailPath, int decodePixelWidth, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ThumbnailImageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = new Call(thumbnailPath, decodePixelWidth, cancellationToken, completion);
            Calls.Add(call);
            if (CancelCompletesCalls)
            {
                cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            }
            return completion.Task;
        }

        public void Complete(int index, ImageSource source, bool fallback) =>
            Calls[index].Completion.TrySetResult(new ThumbnailImageResult(source, fallback));

        public sealed record Call(
            string Path,
            int Width,
            CancellationToken Token,
            TaskCompletionSource<ThumbnailImageResult> Completion);
    }

    private sealed class NoopMetaSource : IWebVideoMetaSource
    {
        public string Name => "Noop";
        public bool CanHandle(string query) => false;
        public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult<WebVideoMetaResult?>(null);
    }

    private sealed class NoopCrawlerFactory : IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<OneFourOneJavCrawlerStartResult>(new InvalidOperationException("Not used."));
    }
}
