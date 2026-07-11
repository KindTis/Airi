using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainViewModelStartupLoadingTests
{
    [Fact]
    public Task Constructor_StartsInLoadingAndCannotMutate() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();

        Assert.Equal(StartupLibraryState.Loading, viewModel.StartupState);
        Assert.False(viewModel.CanMutateLibrary);
        Assert.False(viewModel.IsScanning);
        Assert.False(viewModel.IsFetchingMetadata);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupState_ReadyWithoutOwner_CanMutate() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();

        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        Assert.True(viewModel.CanMutateLibrary);
        Assert.True(viewModel.FetchMetadataCommand.CanExecute(null));
        return Task.CompletedTask;
    });

    [Fact]
    public Task TryBeginLibraryMutation_AllowsExactlyOneDispatcherOwner() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        var first = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);
        var second = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.EditorSave);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Equal(first, viewModel.CurrentMutationLease);
        Assert.True(viewModel.IsFetchingMetadata);
        Assert.False(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReleaseLibraryMutation_WrongIdentityCannotReleaseOwner() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        var lease = Assert.IsType<LibraryMutationLease>(
            viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));

        viewModel.ReleaseLibraryMutation(lease with { Identity = Guid.NewGuid() });
        Assert.Equal(lease, viewModel.CurrentMutationLease);

        viewModel.ReleaseLibraryMutation(lease);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.True(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupStateTransitions_DeriveIsScanningAcrossScanApplySave() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Scanning);
        Assert.True(viewModel.IsScanning);
        viewModel.SetStartupState(StartupLibraryState.ApplyingScan);
        Assert.True(viewModel.IsScanning);
        viewModel.SetStartupState(StartupLibraryState.SavingScan);
        Assert.True(viewModel.IsScanning);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        Assert.False(viewModel.IsScanning);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Dispose_CancelsLifetimeAndAllLeases() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        Assert.NotNull(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.EditorSave));

        viewModel.Dispose();

        Assert.True(viewModel.LifetimeToken.IsCancellationRequested);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.False(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task InitializeAsync_OwnsOneStartupLeaseFromLoadThroughTerminalState() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var states = new List<(StartupLibraryState State, LibraryMutationLease? Lease)>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.StartupState))
            {
                states.Add((viewModel.StartupState, viewModel.CurrentMutationLease));
            }
        };

        await viewModel.InitializeAsync();

        Assert.Equal(new[]
        {
            StartupLibraryState.Publishing,
            StartupLibraryState.Scanning,
            StartupLibraryState.ApplyingScan,
            StartupLibraryState.SavingScan,
            StartupLibraryState.Ready
        }, states.ConvertAll(entry => entry.State));
        var identities = states.Select(entry => entry.Lease?.Identity).Distinct().ToArray();
        Assert.Single(identities);
        Assert.NotNull(identities[0]);
        Assert.All(states, entry => Assert.Equal(LibraryMutationOwner.StartupScan, entry.Lease?.Owner));
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.True(viewModel.CanMutateLibrary);
        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Equal(1, fixture.Scanner.ScanCount);
        Assert.Equal(1, fixture.Store.SaveCount);
    });

    [Fact]
    public Task InitializeAsync_OperationCancelDuringLoad_PreservesOriginalLibraryBytesAndAllowsRetry() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var originalBytes = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(fixture.Store.FilePath, originalBytes);
        var loadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = async token =>
        {
            loadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new LibraryData();
        };
        using var cancellation = new CancellationTokenSource();

        var initialization = viewModel.InitializeAsync(cancellation.Token);
        await loadStarted.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => initialization);

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(fixture.Store.FilePath));
        Assert.Equal(StartupLibraryState.Loading, viewModel.StartupState);
        Assert.Empty(viewModel.Videos);
        Assert.Null(viewModel.CurrentMutationLease);

        fixture.Store.LoadOverride = _ => Task.FromResult(new LibraryData());
        await viewModel.InitializeAsync();
        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
        Assert.Equal(2, fixture.Store.LoadCount);
    });

    [Fact]
    public Task InitializeAsync_ConcurrentCalls_DoNotCreateSecondStartupLease() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var releaseLoad = new TaskCompletionSource<LibraryData>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.LoadOverride = _ => releaseLoad.Task;

        var first = viewModel.InitializeAsync();
        var second = viewModel.InitializeAsync();
        releaseLoad.TrySetResult(new LibraryData());
        await Task.WhenAll(first, second);

        Assert.Equal(1, fixture.Store.LoadCount);
        Assert.Equal(1, fixture.Scanner.ScanCount);
        Assert.Equal(1, fixture.Store.SaveCount);
        Assert.Equal(StartupLibraryState.Ready, viewModel.StartupState);
    });

    [Fact]
    public Task StartupStateChange_RaisesIsScanningAndSkeletonNotifications() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        var changes = new List<string>();
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                changes.Add(args.PropertyName);
            }
        };

        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Scanning);
        viewModel.SetStartupState(StartupLibraryState.ApplyingScan);
        viewModel.SetStartupState(StartupLibraryState.SavingScan);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        Assert.Equal(2, changes.Count(name => name == nameof(MainViewModel.IsScanning)));
        Assert.Equal(2, changes.Count(name => name == nameof(MainViewModel.ShowVideoSkeleton)));
        Assert.Equal(2, changes.Count(name => name == nameof(MainViewModel.ShowActorSkeleton)));
        return Task.CompletedTask;
    });

    [Fact]
    public Task EditorLease_BlocksAutoAndManualUntilSaveCancelOrException() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        var editor = Assert.IsType<LibraryMutationLease>(viewModel.TryBeginMetadataEditorMutation());
        Assert.Equal(LibraryMutationOwner.EditorSave, editor.Owner);
        Assert.Null(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.AutoMetadata));
        Assert.Null(viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch));
        viewModel.EndMetadataEditorMutation(editor);
        Assert.True(viewModel.CanMutateLibrary);
        return Task.CompletedTask;
    });

    [Fact]
    public Task ReadOnlyCommands_RemainEnabledWhileMutationIsBlocked() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SearchQuery = "sample";

        Assert.True(viewModel.ClearSearchCommand.CanExecute(null));
        Assert.True(viewModel.CanUseCommandBar);
        viewModel.ClearSearchCommand.Execute(null);
        Assert.Equal(string.Empty, viewModel.SearchQuery);
        return Task.CompletedTask;
    });

    [Fact]
    public Task Faulted_ReleasesLeaseButKeepsMutationDisabled() => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        fixture.Store.LoadOverride = _ => Task.FromException<LibraryData>(new IOException("load failed"));

        await Assert.ThrowsAsync<IOException>(() => viewModel.InitializeAsync());

        Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
        Assert.Null(viewModel.CurrentMutationLease);
        Assert.False(viewModel.CanMutateLibrary);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
    });

    [Fact]
    public Task FetchCommand_RapidDoubleExecute_StartsOneManualLease() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        viewModel.SetStartupState(StartupLibraryState.Ready);

        var first = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);
        var second = viewModel.TryBeginLibraryMutation(LibraryMutationOwner.ManualFetch);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.False(viewModel.FetchMetadataCommand.CanExecute(null));
        Assert.Equal(LibraryMutationOwner.ManualFetch, viewModel.CurrentMutationLease?.Owner);
        return Task.CompletedTask;
    });

    [Fact]
    public Task StartupLifecycle_TransitionsOnlyAtLoadPublishScanApplySaveBoundaries() => WpfTestHost.RunAsync(() =>
    {
        using var fixture = new Fixture();
        using var viewModel = fixture.CreateViewModel();

        Assert.Throws<InvalidOperationException>(() =>
            viewModel.SetStartupState(StartupLibraryState.Scanning));
        viewModel.SetStartupState(StartupLibraryState.Publishing);
        Assert.Throws<InvalidOperationException>(() =>
            viewModel.SetStartupState(StartupLibraryState.SavingScan));
        viewModel.SetStartupState(StartupLibraryState.Scanning);
        viewModel.SetStartupState(StartupLibraryState.ApplyingScan);
        viewModel.SetStartupState(StartupLibraryState.SavingScan);
        viewModel.SetStartupState(StartupLibraryState.Ready);
        return Task.CompletedTask;
    });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AiriStartupTests", Guid.NewGuid().ToString("N"));

        public FakeLibraryStore Store { get; }
        public FakeLibraryScanner Scanner { get; } = new();

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Store = new FakeLibraryStore(Path.Combine(_root, "videos.json"));
        }

        public MainViewModel CreateViewModel()
        {
            var provider = new CrawlerSessionProvider();
            var source = new OneFourOneJavMetaSource(provider);
            return new MainViewModel(
                Store,
                Scanner,
                new WebMetadataService(
                    Array.Empty<IWebVideoMetaSource>(),
                    new ThumbnailCache(_root),
                    NullTranslationService.Instance,
                    "KO"),
                provider,
                source,
                new NeverCrawlerSessionFactory(),
                new TestThumbnailImageLoader());
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    internal sealed class FakeLibraryStore(string filePath) : ILibraryStore
    {
        public string FilePath { get; } = filePath;
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }
        public Func<CancellationToken, Task<LibraryData>>? LoadOverride { get; set; }
        public Func<LibraryData, CancellationToken, Task>? SaveOverride { get; set; }

        public Task<LibraryData> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return LoadOverride?.Invoke(cancellationToken) ?? Task.FromResult(new LibraryData());
        }

        public Task SaveAsync(LibraryData library, CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return SaveOverride?.Invoke(library, cancellationToken) ?? Task.CompletedTask;
        }
    }

    internal sealed class FakeLibraryScanner : ILibraryScanner
    {
        public int ScanCount { get; private set; }
        public Func<LibraryData, CancellationToken, Task<LibraryScanResult>>? ScanOverride { get; set; }

        public Task<LibraryScanResult> ScanAsync(LibraryData library, CancellationToken cancellationToken)
        {
            ScanCount++;
            return ScanOverride?.Invoke(library, cancellationToken) ?? Task.FromResult(new LibraryScanResult(
                Array.Empty<FileSnapshot>(),
                Array.Empty<FileSnapshot>(),
                Array.Empty<VideoEntry>(),
                Array.Empty<UpdatedFile>()));
        }
    }

    private sealed class NeverCrawlerSessionFactory : IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Crawler is not used by startup tests.");
    }
}
