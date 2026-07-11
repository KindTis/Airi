using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests
{
    [Collection(WpfTestCollection.Name)]
    public sealed class MainViewModelSkeletonTests
    {
        [Fact]
        public void Constructor_WhenCreated_SkeletonFlagsAreEnabled()
        {
            RunInSta(() =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();

                Assert.True(viewModel.IsInitialLoading);
                Assert.True(viewModel.ShowVideoSkeleton);
                Assert.True(viewModel.ShowActorSkeleton);
            });
        }

        [Fact]
        public void ShowActorSkeleton_WhenOnlyDefaultActorDuringScan_ReturnsTrueThenFalseAfterActorAdded()
        {
            RunInSta(() =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();

                SetPrivateField(viewModel, "_isInitialLoading", false);
                viewModel.SetStartupState(StartupLibraryState.Publishing);
                viewModel.SetStartupState(StartupLibraryState.Scanning);

                Assert.True(viewModel.ShowActorSkeleton);

                viewModel.Actors.Add("Sample Actor");

                Assert.False(viewModel.ShowActorSkeleton);
            });
        }

        [Fact]
        public void ShowVideoSkeleton_WhenNoVideoDuringScan_ReturnsTrueThenFalseAfterVideoAdded()
        {
            RunInSta(() =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();

                SetPrivateField(viewModel, "_isInitialLoading", false);
                viewModel.SetStartupState(StartupLibraryState.Publishing);
                viewModel.SetStartupState(StartupLibraryState.Scanning);

                Assert.True(viewModel.ShowVideoSkeleton);

                viewModel.Videos.Add(new VideoItem
                {
                    LibraryPath = "./Videos/sample.mp4",
                    Title = "Sample"
                });

                Assert.False(viewModel.ShowVideoSkeleton);
            });
        }

        [Fact]
        public void ActorsCollectionChanged_WhenItemAdded_RaisesShowActorSkeletonPropertyChanged()
        {
            RunInSta(() =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();
                var changed = new List<string>();
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    {
                        changed.Add(e.PropertyName!);
                    }
                };

                viewModel.Actors.Add("Sample Actor");

                Assert.Contains(nameof(MainViewModel.ShowActorSkeleton), changed);
            });
        }

        [Fact]
        public void VideosCollectionChanged_WhenItemAdded_RaisesShowVideoSkeletonPropertyChanged()
        {
            RunInSta(() =>
            {
                using var fixture = new ViewModelFixture();
                var viewModel = fixture.CreateViewModel();
                var changed = new List<string>();
                viewModel.PropertyChanged += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.PropertyName))
                    {
                        changed.Add(e.PropertyName!);
                    }
                };

                viewModel.Videos.Add(new VideoItem
                {
                    LibraryPath = "./Videos/sample.mp4",
                    Title = "Sample"
                });

                Assert.Contains(nameof(MainViewModel.ShowVideoSkeleton), changed);
            });
        }

        private static void SetPrivateField<T>(MainViewModel viewModel, string fieldName, T value)
        {
            var field = typeof(MainViewModel).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(viewModel, value);
        }

        private static void RunInSta(Action action)
        {
            WpfTestHost.Run(action);
        }

        private sealed class ViewModelFixture : IDisposable
        {
            private readonly string _root;

            public ViewModelFixture()
            {
                _root = Path.Combine(Path.GetTempPath(), "AiriMainViewModelTests", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_root);
            }

            public MainViewModel CreateViewModel()
            {
                var libraryPath = Path.Combine(_root, "videos.json");
                var libraryStore = new LibraryStore(libraryPath);
                var libraryScanner = new LibraryScanner(new FileSystemScanner());
                var thumbnailCache = new ThumbnailCache(_root);
                var metadataService = new WebMetadataService(
                    new IWebVideoMetaSource[] { new StubMetaSource() },
                    thumbnailCache,
                    NullTranslationService.Instance,
                    "KO");
                var crawlerSessionProvider = new CrawlerSessionProvider();
                var oneFourOneJavSource = new OneFourOneJavMetaSource(crawlerSessionProvider);
                var crawlerSessionFactory = new StubCrawlerSessionFactory();

                return new MainViewModel(
                    libraryStore,
                    libraryScanner,
                    metadataService,
                    crawlerSessionProvider,
                    oneFourOneJavSource,
                    crawlerSessionFactory,
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

        private sealed class StubMetaSource : IWebVideoMetaSource
        {
            public string Name => "Stub";

            public bool CanHandle(string query) => false;

            public Task<WebVideoMetaResult?> FetchAsync(string query, CancellationToken cancellationToken)
            {
                return Task.FromResult<WebVideoMetaResult?>(null);
            }
        }

        private sealed class StubCrawlerSessionFactory : IOneFourOneJavCrawlerSessionFactory
        {
            public Task<OneFourOneJavCrawlerStartResult> StartAsync(CancellationToken cancellationToken = default)
            {
                throw new InvalidOperationException("This test fixture does not start crawler sessions.");
            }
        }
    }
}
