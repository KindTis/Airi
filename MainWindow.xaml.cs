using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;
using Airi.Views;

namespace Airi
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient = new();
        private readonly ITextTranslationService _translationService;
        private readonly ThumbnailPerformanceProbe _performanceProbe;
        private bool _performanceRenderingAttached;
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            AppLogger.Info("Initializing MainWindow.");
            _performanceProbe = ThumbnailPerformanceProbe.Disabled;

            var deeplAuthKey = Environment.GetEnvironmentVariable("DEEPL_AUTH_KEY");
            if (string.IsNullOrWhiteSpace(deeplAuthKey))
            {
                _translationService = NullTranslationService.Instance;
                AppLogger.Info("DeepL translation disabled: DEEPL_AUTH_KEY not set.");
            }
            else
            {
                _translationService = new DeepLTranslationService(deeplAuthKey);
                AppLogger.Info("DeepL translation enabled.");
            }

            var translationTarget = Environment.GetEnvironmentVariable("DEEPL_TARGET_LANG");
            var translationTargetLanguageCode = string.IsNullOrWhiteSpace(translationTarget) ? "KO" : translationTarget;
            var libraryStore = new LibraryStore();
            var libraryScanner = new LibraryScanner(new FileSystemScanner());
            var thumbnailCache = new ThumbnailCache();
            var crawlerSessionProvider = new CrawlerSessionProvider();
            var oneFourOneJavCrawler = new OneFourOneJavCrawler();
            var oneFourOneJavSource = new OneFourOneJavMetaSource(crawlerSessionProvider, _httpClient);
            var crawlerSessionFactory = new OneFourOneJavCrawlerSessionFactory(oneFourOneJavCrawler);
            var metadataSources = new IWebVideoMetaSource[] { oneFourOneJavSource, new NanoJavMetaSource(_httpClient) };
            var webMetadataService = new WebMetadataService(
                metadataSources,
                thumbnailCache,
                _translationService,
                translationTargetLanguageCode);

            ViewModel = new MainViewModel(
                libraryStore,
                libraryScanner,
                webMetadataService,
                crawlerSessionProvider,
                oneFourOneJavSource,
                crawlerSessionFactory);
            WireViewModelEvents();
        }

        internal MainWindow(
            MainViewModel viewModel,
            ThumbnailPerformanceProbe? performanceProbe = null)
        {
            InitializeComponent();
            _translationService = NullTranslationService.Instance;
            _performanceProbe = performanceProbe ?? ThumbnailPerformanceProbe.Disabled;
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            WireViewModelEvents();
        }

        private void WireViewModelEvents()
        {
            DataContext = ViewModel;
            ViewModel.PlayVideoRequested += OnPlayVideoRequested;
            ViewModel.Videos.CollectionChanged += OnPerformanceVideosChanged;
            Loaded += OnLoaded;
        }

        private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F1 && !e.IsRepeat)
            {
                e.Handled = true;
                await OpenMetadataEditorAsync();
            }
        }

        private async Task OpenMetadataEditorAsync()
        {
            if (ViewModel.SelectedVideo is null)
            {
                return;
            }

            var dialog = new MetadataEditorWindow(ViewModel.SelectedVideo)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.Result is MetadataEditResult result)
            {
                await ViewModel.ApplyMetadataEditAsync(ViewModel.SelectedVideo, result);
            }
        }



        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("MainWindow loaded. Starting view model initialization.");
            _performanceProbe.TryMark(StartupTimingMarker.MainWindowLoaded);
            await ViewModel.InitializeAsync();
            AppLogger.Info("View model initialization complete.");
        }

        private void OnPerformanceVideosChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (!_performanceProbe.IsActive || ViewModel.Videos.Count == 0 || _performanceRenderingAttached)
            {
                return;
            }

            CompositionTarget.Rendering += OnPerformanceRendering;
            _performanceRenderingAttached = true;
        }

        private void OnPerformanceRendering(object? sender, EventArgs e)
        {
            if (!_performanceProbe.IsActive)
            {
                DetachPerformanceRendering();
                return;
            }

            var realizedContainers = EnumerableRealizedVideoContainers();
            var meaningfulCard = realizedContainers.FirstOrDefault(container =>
                container.IsVisible && container.ActualWidth > 0 && container.ActualHeight > 0);
            if (meaningfulCard is not null && !ViewModel.ShowVideoSkeleton)
            {
                _performanceProbe.TryMark(StartupTimingMarker.VisualFirstMeaningfulCard);
            }

            foreach (var container in realizedContainers)
            {
                foreach (var image in EnumerateVisualDescendants<Image>(container))
                {
                    if (IsEligibleLegacyThumbnail(image))
                    {
                        _performanceProbe.TryMark(StartupTimingMarker.VisualFirstThumbnail);
                        break;
                    }
                }

                if (_performanceProbe.HasMarker(StartupTimingMarker.VisualFirstThumbnail))
                {
                    break;
                }
            }

            if (_performanceProbe.HasMarker(StartupTimingMarker.VisualFirstMeaningfulCard) &&
                _performanceProbe.HasMarker(StartupTimingMarker.VisualFirstThumbnail))
            {
                DetachPerformanceRendering();
            }
        }

        private List<ListBoxItem> EnumerableRealizedVideoContainers()
        {
            var result = new List<ListBoxItem>();
            for (var index = 0; index < VideoList.Items.Count; index++)
            {
                if (VideoList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem container)
                {
                    result.Add(container);
                }
            }

            return result;
        }

        private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject parent)
            where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(parent);
            for (var index = 0; index < count; index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is T match)
                {
                    yield return match;
                }

                foreach (var descendant in EnumerateVisualDescendants<T>(child))
                {
                    yield return descendant;
                }
            }
        }

        private static bool IsEligibleLegacyThumbnail(Image image)
        {
            if (!string.Equals(image.Tag as string, "MainCardThumbnail", StringComparison.Ordinal) ||
                image.DataContext is not VideoItem item ||
                image.ActualWidth <= 0 ||
                image.ActualHeight <= 0 ||
                !image.IsVisible ||
                BindingOperations.GetBindingExpression(image, Image.SourceProperty)?.Status != BindingStatus.Active ||
                image.Source is not BitmapSource bitmapSource ||
                bitmapSource.PixelWidth <= 0 ||
                bitmapSource.PixelHeight <= 0)
            {
                return false;
            }

            if (bitmapSource is BitmapImage downloading && downloading.IsDownloading)
            {
                return false;
            }

            var expected = LibraryPathHelper.ResolveToAbsolute(item.ThumbnailPath);
            var fallback = LibraryPathHelper.ResolveToAbsolute("resources/noimage.jpg");
            if (string.IsNullOrWhiteSpace(expected) ||
                string.Equals(expected, fallback, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var sourceUri = bitmapSource is BitmapImage bitmapImage
                ? bitmapImage.UriSource
                : Uri.TryCreate(bitmapSource.ToString(), UriKind.Absolute, out var convertedUri)
                    ? convertedUri
                    : null;
            if (sourceUri is null || !sourceUri.IsFile)
            {
                return false;
            }

            return string.Equals(
                Path.GetFullPath(sourceUri.LocalPath),
                Path.GetFullPath(expected),
                StringComparison.OrdinalIgnoreCase);
        }

        internal string GetLegacyThumbnailDiagnostic()
        {
            var images = EnumerableRealizedVideoContainers()
                .SelectMany(container => EnumerateVisualDescendants<Image>(container))
                .Where(image => string.Equals(image.Tag as string, "MainCardThumbnail", StringComparison.Ordinal))
                .Select(image =>
                {
                    var source = image.Source;
                    var bitmap = source as BitmapSource;
                    var uri = (source as BitmapImage)?.UriSource?.ToString() ?? "<none>";
                    var baseUri = (source as IUriContext)?.BaseUri?.ToString() ?? "<none>";
                    var binding = BindingOperations.GetBindingExpression(image, Image.SourceProperty)?.Status.ToString() ?? "<none>";
                    return $"source={source?.GetType().FullName ?? "<null>"}; value={source}; uri={uri}; baseUri={baseUri}; pixels={bitmap?.PixelWidth ?? 0}x{bitmap?.PixelHeight ?? 0}; actual={image.ActualWidth}x{image.ActualHeight}; visible={image.IsVisible}; binding={binding}; data={image.DataContext?.GetType().Name ?? "<null>"}";
                });
            return string.Join(" | ", images);
        }

        internal int GetRealizedVideoContainerCount() => EnumerableRealizedVideoContainers().Count;

        internal ScrollViewer? GetVideoScrollViewer() =>
            EnumerateVisualDescendants<ScrollViewer>(VideoList).FirstOrDefault();

        internal Size GetFirstVideoContainerExtent()
        {
            var container = EnumerableRealizedVideoContainers().FirstOrDefault();
            return container is null
                ? new Size(0, 0)
                : new Size(container.ActualWidth, container.ActualHeight);
        }

        internal void ScrollVideoToIndex(int index)
        {
            if (index < 0 || index >= VideoList.Items.Count)
            {
                return;
            }

            VideoList.ScrollIntoView(VideoList.Items[index]);
            UpdateLayout();
        }

        private void DetachPerformanceRendering()
        {
            if (!_performanceRenderingAttached)
            {
                return;
            }

            CompositionTarget.Rendering -= OnPerformanceRendering;
            _performanceRenderingAttached = false;
        }

        private void VideoList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListBox listBox)
            {
                return;
            }

            if (listBox.SelectedItem is not VideoItem video)
            {
                return;
            }

            e.Handled = true;
            TryPlayVideo(video);
        }

        private void OnVideoItemMouseEnter(object sender, MouseEventArgs e)
        {
            if (sender is ListBoxItem item && ToolTipService.GetToolTip(item) is ToolTip tooltip)
            {
                tooltip.PlacementTarget = item;
                tooltip.Placement = PlacementMode.Relative;
                UpdateTooltipPosition(item, tooltip, e);
                tooltip.IsOpen = true;
            }
        }

        private void OnVideoItemMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is ListBoxItem item && ToolTipService.GetToolTip(item) is ToolTip tooltip && tooltip.IsOpen)
            {
                UpdateTooltipPosition(item, tooltip, e);
            }
        }

        private void OnVideoItemMouseLeave(object sender, MouseEventArgs e)
        {
            if (sender is ListBoxItem item && ToolTipService.GetToolTip(item) is ToolTip tooltip)
            {
                tooltip.IsOpen = false;
            }
        }

        private static void UpdateTooltipPosition(UIElement item, ToolTip tooltip, MouseEventArgs e)
        {
            var position = e.GetPosition(item);
            tooltip.HorizontalOffset = position.X + 5;
            tooltip.VerticalOffset = position.Y + 5;
        }

        private void OnPlayVideoRequested(VideoItem video)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => TryPlayVideo(video));
                return;
            }

            TryPlayVideo(video);
        }

        private void TryPlayVideo(VideoItem video)
        {
            if (video.Presence != VideoPresenceState.Available)
            {
                AppLogger.Info($"Skipping playback for unavailable video: {video.Title}");
                return;
            }

            if (string.IsNullOrWhiteSpace(video.SourcePath))
            {
                AppLogger.Info($"Skipping playback for {video.Title}; no source path set.");
                return;
            }

            if (!File.Exists(video.SourcePath))
            {
                AppLogger.Info($"File not found for playback: {video.SourcePath}");
                return;
            }

            try
            {
                AppLogger.Info($"Launching video with default player: {video.SourcePath}");
                Process.Start(new ProcessStartInfo
                {
                    FileName = video.SourcePath,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Failed to launch video: {video.SourcePath}", ex);
            }
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            DetachPerformanceRendering();
            ViewModel.Videos.CollectionChanged -= OnPerformanceVideosChanged;
            ViewModel.PlayVideoRequested -= OnPlayVideoRequested;
            _httpClient.Dispose();
            if (_translationService is IDisposable disposableTranslation)
            {
                disposableTranslation.Dispose();
            }
        }
    }
}








