using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class SortSettingsPersistenceTests
{
    [Fact]
    public Task InitializeAsync_RestoresSelectionAndActualCollectionOrder() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.Settings.SaveAsync(
                fixture.CreateSettings("Title", "Ascending"));
            fixture.Store.Library = fixture.CreateLibrary("Zulu", "Alpha");
            using var viewModel = fixture.CreateViewModel();

            await viewModel.InitializeAsync();

            Assert.Equal(MainViewModel.SortField.Title, viewModel.SelectedSortOption.Field);
            Assert.Equal(ListSortDirection.Ascending, viewModel.SelectedSortOption.Direction);
            Assert.Equal(
                new[] { "Alpha", "Zulu" },
                viewModel.FilteredVideos.Cast<VideoItem>().Select(item => item.Title));
        });

    [Fact]
    public Task SelectedSortOption_AppliesThenPersistsStableValues() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Store.Library = fixture.CreateLibrary("Zulu", "Alpha");
            using var viewModel = fixture.CreateViewModel();
            await viewModel.InitializeAsync();

            viewModel.SelectedSortOption = viewModel.SortOptions.Single(option =>
                option.Field == MainViewModel.SortField.Title &&
                option.Direction == ListSortDirection.Ascending);
            await viewModel.SettingsSaveTask;

            var persisted = await fixture.Settings.LoadAsync();
            Assert.Equal("Title", persisted.Sort.Field);
            Assert.Equal("Ascending", persisted.Sort.Direction);
            Assert.Equal(
                new[] { "Alpha", "Zulu" },
                viewModel.FilteredVideos.Cast<VideoItem>().Select(item => item.Title));
        });

    [Fact]
    public Task SelectedSortOption_SaveFailureKeepsScreenSortAndVideosFile() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.Settings.LoadAsync();
            await File.WriteAllTextAsync(fixture.Store.FilePath, """{ "sentinel": true }""");
            var before = await File.ReadAllBytesAsync(fixture.Store.FilePath);
            var failingStore = new SettingsStore(fixture.Store.FilePath, (stage, _, _) =>
            {
                if (stage == SettingsStoreSaveStage.BeforeCommit)
                {
                    throw new IOException("settings denied");
                }
            });
            using var viewModel = fixture.CreateViewModel(failingStore);
            await viewModel.InitializeAsync();

            viewModel.SelectedSortOption = viewModel.SortOptions.Single(option =>
                option.Field == MainViewModel.SortField.Title &&
                option.Direction == ListSortDirection.Ascending);
            await viewModel.SettingsSaveTask;

            Assert.Equal(MainViewModel.SortField.Title, viewModel.SelectedSortOption.Field);
            Assert.Contains(
                "settings denied",
                viewModel.StatusMessage,
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, await File.ReadAllBytesAsync(fixture.Store.FilePath));
        });

    [Fact]
    public Task InitializeAsync_SettingsFailureStopsBeforeLibraryLoadAndScan() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await File.WriteAllTextAsync(fixture.Settings.FilePath, """{ "Sort": [] }""");
            using var viewModel = fixture.CreateViewModel();

            await Assert.ThrowsAsync<JsonException>(() => viewModel.InitializeAsync());

            Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
            Assert.Equal(0, fixture.Store.LoadCount);
            Assert.Equal(0, fixture.Scanner.ScanCount);
            Assert.Contains(
                "library load and scan stopped",
                viewModel.StatusMessage,
                StringComparison.OrdinalIgnoreCase);
        });

    [Fact]
    public Task InitializeAsync_SettingsCreationFailureStopsBeforeLibraryLoadAndScan() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            using var viewModel = fixture.CreateViewModel(
                fixture.CreateSettingsStoreWithBlockedDirectory());

            await Assert.ThrowsAnyAsync<IOException>(() => viewModel.InitializeAsync());

            Assert.Equal(StartupLibraryState.Faulted, viewModel.StartupState);
            Assert.Equal(0, fixture.Store.LoadCount);
            Assert.Equal(0, fixture.Scanner.ScanCount);
            Assert.Contains(
                "library load and scan stopped",
                viewModel.StatusMessage,
                StringComparison.OrdinalIgnoreCase);
        });

    [Fact]
    public Task RapidChanges_AllSuccessRestoreLastSelectionAfterRestart() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            fixture.Store.Library = fixture.CreateLibrary("Zulu", "Alpha");
            using (var firstSession = fixture.CreateViewModel())
            {
                await firstSession.InitializeAsync();
                firstSession.SelectedSortOption = firstSession.SortOptions.Single(option =>
                    option.Field == MainViewModel.SortField.ReleaseDate &&
                    option.Direction == ListSortDirection.Ascending);
                firstSession.SelectedSortOption = firstSession.SortOptions.Single(option =>
                    option.Field == MainViewModel.SortField.Title &&
                    option.Direction == ListSortDirection.Descending);
                await firstSession.SettingsSaveTask;
            }

            using var restarted = fixture.CreateViewModel();
            await restarted.InitializeAsync();

            Assert.Equal(MainViewModel.SortField.Title, restarted.SelectedSortOption.Field);
            Assert.Equal(ListSortDirection.Descending, restarted.SelectedSortOption.Direction);
            Assert.Equal(
                new[] { "Zulu", "Alpha" },
                restarted.FilteredVideos.Cast<VideoItem>().Select(item => item.Title));
        });

    [Fact]
    public Task RapidChanges_LatestFailureKeepsScreenLatestButRestoresLastSuccess() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new Fixture();
            await fixture.Settings.LoadAsync();
            var attempts = 0;
            var controlled = new SettingsStore(fixture.Store.FilePath, (stage, _, _) =>
            {
                if (stage == SettingsStoreSaveStage.BeforeTemporaryWrite &&
                    Interlocked.Increment(ref attempts) == 2)
                {
                    throw new IOException("latest failed");
                }
            });
            using (var firstSession = fixture.CreateViewModel(controlled))
            {
                await firstSession.InitializeAsync();
                firstSession.SelectedSortOption = firstSession.SortOptions.Single(option =>
                    option.Field == MainViewModel.SortField.ReleaseDate &&
                    option.Direction == ListSortDirection.Ascending);
                firstSession.SelectedSortOption = firstSession.SortOptions.Single(option =>
                    option.Field == MainViewModel.SortField.Title &&
                    option.Direction == ListSortDirection.Descending);
                await firstSession.SettingsSaveTask;
                Assert.Equal(
                    MainViewModel.SortField.Title,
                    firstSession.SelectedSortOption.Field);
                Assert.Contains(
                    "latest failed",
                    firstSession.StatusMessage,
                    StringComparison.OrdinalIgnoreCase);
            }

            using var restarted = fixture.CreateViewModel();
            await restarted.InitializeAsync();
            Assert.Equal(
                MainViewModel.SortField.ReleaseDate,
                restarted.SelectedSortOption.Field);
            Assert.Equal(
                ListSortDirection.Ascending,
                restarted.SelectedSortOption.Direction);
        });

    private sealed class Fixture : IDisposable
    {
        private readonly string _root =
            Path.Combine(
                Path.GetTempPath(),
                "AiriSortSettingsTests",
                Guid.NewGuid().ToString("N"));

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            Store = new FakeLibraryStore(Path.Combine(_root, "videos.json"));
            Settings = new SettingsStore(Store.FilePath);
        }

        public FakeLibraryStore Store { get; }
        public CapturingScanner Scanner { get; } = new();
        public SettingsStore Settings { get; }

        public MainViewModel CreateViewModel(SettingsStore? settingsStore = null)
        {
            var provider = new CrawlerSessionProvider();
            return new MainViewModel(
                Store,
                Scanner,
                new WebMetadataService(
                    Array.Empty<IWebVideoMetaSource>(),
                    new ThumbnailCache(_root),
                    NullTranslationService.Instance,
                    "KO"),
                provider,
                new OneFourOneJavMetaSource(provider),
                new NeverCrawlerSessionFactory(),
                ThumbnailPerformanceProbe.Disabled,
                new TestThumbnailImageLoader(),
                settingsStore: settingsStore ?? Settings);
        }

        public AppSettings CreateSettings(string field, string direction) => new()
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

        public SettingsStore CreateSettingsStoreWithBlockedDirectory()
        {
            var blocker = Path.Combine(_root, "blocked");
            File.WriteAllText(blocker, "not a directory");
            return new SettingsStore(Path.Combine(blocker, "videos.json"));
        }

        public LibraryData CreateLibrary(params string[] titles) => new()
        {
            Videos = titles.Select((title, index) => new VideoEntry(
                $"./Videos/{index}.mp4",
                new VideoMeta(
                    title,
                    new DateOnly(2020, 1, titles.Length - index),
                    Array.Empty<string>(),
                    string.Empty,
                    Array.Empty<string>(),
                    string.Empty))).ToList()
        };

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FakeLibraryStore(string filePath) : ILibraryStore
    {
        public string FilePath { get; } = filePath;
        public LibraryData Library { get; set; } = new();
        public int LoadCount { get; private set; }
        public int SaveCount { get; private set; }

        public Task<LibraryData> LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.FromResult(Library);
        }

        public Task SaveAsync(
            LibraryData library,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            Library = library;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingScanner : ILibraryScanner
    {
        public int ScanCount { get; private set; }
        public IReadOnlyList<TargetFolder> Targets { get; private set; } =
            Array.Empty<TargetFolder>();

        public Task<LibraryScanResult> ScanAsync(
            LibraryData library,
            CancellationToken cancellationToken)
        {
            ScanCount++;
            return Task.FromResult(new LibraryScanResult(
                Array.Empty<FileSnapshot>(),
                Array.Empty<FileSnapshot>(),
                Array.Empty<VideoEntry>(),
                Array.Empty<UpdatedFile>()));
        }
    }

    private sealed class NeverCrawlerSessionFactory :
        IOneFourOneJavCrawlerSessionFactory
    {
        public Task<OneFourOneJavCrawlerStartResult> StartAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromException<OneFourOneJavCrawlerStartResult>(
                new InvalidOperationException(
                    "Crawler is not used by sort settings tests."));
    }
}
