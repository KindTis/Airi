using System;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
        private readonly IThumbnailImageLoader? _thumbnailImageLoader;
        private bool _performanceRenderingAttached;
        private readonly Dictionary<Image, VideoItem> _thumbnailRegistrations = new();
        private readonly Dictionary<Image, int> _thumbnailRegistrationWidths = new();
        private readonly Dictionary<VideoItem, int> _thumbnailRegistrationCounts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VideoItem, int> _thumbnailRequestedWidths = new(ReferenceEqualityComparer.Instance);
        private Task _initializationTask = Task.CompletedTask;
        private bool _initializationStarted;
        public MainViewModel ViewModel { get; }
        internal Task InitializationTask => _initializationTask;
        internal bool InitializationStarted => _initializationStarted;

        public MainWindow()
            : this(ThumbnailImageLoader.CreateWithGeneratedFallback())
        {
        }

        public MainWindow(IThumbnailImageLoader thumbnailImageLoader)
        {
            InitializeComponent();
            AppLogger.Info("Initializing MainWindow.");
            _performanceProbe = ThumbnailPerformanceProbe.Disabled;
            _thumbnailImageLoader = thumbnailImageLoader ?? throw new ArgumentNullException(nameof(thumbnailImageLoader));

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
                crawlerSessionFactory,
                thumbnailImageLoader);
            WireViewModelEvents();
        }

        internal MainWindow(
            MainViewModel viewModel,
            ThumbnailPerformanceProbe? performanceProbe = null)
        {
            InitializeComponent();
            _translationService = NullTranslationService.Instance;
            _performanceProbe = performanceProbe ?? ThumbnailPerformanceProbe.Disabled;
            _thumbnailImageLoader = null;
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
            var selected = ViewModel.SelectedVideo;
            if (selected is null)
            {
                return;
            }

            var lease = ViewModel.TryBeginMetadataEditorMutation();
            if (lease is null)
            {
                return;
            }

            try
            {
                var dialog = new MetadataEditorWindow(selected)
                {
                    Owner = this
                };

                if (dialog.ShowDialog() == true && dialog.Result is MetadataEditResult result)
                {
                    await ViewModel.ApplyMetadataEditAsync(selected, result, lease);
                }
            }
            finally
            {
                ViewModel.EndMetadataEditorMutation(lease);
            }
        }



        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_initializationStarted)
            {
                return;
            }

            _initializationStarted = true;
            Loaded -= OnLoaded;
            AppLogger.Info("MainWindow loaded. Starting view model initialization.");
            _performanceProbe.TryMark(StartupTimingMarker.MainWindowLoaded);
            _initializationTask = ViewModel.InitializeAsync();
            try
            {
                await _initializationTask;
                AppLogger.Info("View model initialization complete.");
            }
            catch (OperationCanceledException) when (ViewModel.LifetimeToken.IsCancellationRequested)
            {
                AppLogger.Info("View model initialization ended because the window closed.");
            }
            catch (Exception ex) when (ViewModel.StartupState == StartupLibraryState.Faulted)
            {
                _ = ex;
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unhandled view model initialization failure.", ex);
            }
        }

        private async void OnThumbnailImageLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Image image || image.DataContext is not VideoItem item)
            {
                return;
            }

            try
            {
                await RegisterThumbnailImageAsync(image, item);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected thumbnail request failure.", ex);
            }
        }

        private void OnThumbnailImageUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
            {
                UnregisterThumbnailImage(image);
            }
        }

        private async void OnThumbnailImageDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not Image image)
            {
                return;
            }

            try
            {
                await ChangeThumbnailImageDataContextAsync(image, e.NewValue as VideoItem, image.IsLoaded);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Unexpected thumbnail request failure.", ex);
            }
        }

        internal static int CalculateThumbnailDecodeWidth(Image image)
        {
            ArgumentNullException.ThrowIfNull(image);
            var dipWidth = image.ActualWidth > 0 ? image.ActualWidth : 260d;
            var dpi = VisualTreeHelper.GetDpi(image).DpiScaleX;
            return CalculateThumbnailDecodeWidth(dipWidth, dpi);
        }

        internal static int CalculateThumbnailDecodeWidth(double actualWidth, double dpiScaleX) =>
            Math.Clamp((int)Math.Ceiling(actualWidth * dpiScaleX), 64, 520);

        internal Task RegisterThumbnailImageForTestsAsync(Image image) =>
            image.DataContext is VideoItem item
                ? RegisterThumbnailImageAsync(image, item)
                : Task.CompletedTask;

        internal Task ChangeThumbnailImageDataContextForTestsAsync(
            Image image,
            VideoItem? newItem,
            bool isLoaded) =>
            ChangeThumbnailImageDataContextAsync(image, newItem, isLoaded);

        internal void UnregisterThumbnailImageForTests(Image image) => UnregisterThumbnailImage(image);

        internal int GetThumbnailRegistrationCountForTests() => _thumbnailRegistrations.Count;

        internal bool AreActiveThumbnailRegistrationsTerminalForTests() =>
            _thumbnailRegistrations.Values
                .DistinctBy(item => RuntimeHelpers.GetHashCode(item))
                .All(item =>
                {
                    var diagnostics = ViewModel.GetThumbnailRuntimeDiagnostics(item);
                    return diagnostics.Exists &&
                           !diagnostics.HasInFlight &&
                           diagnostics.Outcome is "Loaded" or "Failed" &&
                           item.ThumbnailLoadState is ThumbnailLoadState.Loaded or ThumbnailLoadState.Failed;
                });

        private async Task ChangeThumbnailImageDataContextAsync(
            Image image,
            VideoItem? newItem,
            bool isLoaded)
        {
            UnregisterThumbnailImage(image);
            image.DataContext = newItem;
            if (isLoaded && newItem is not null)
            {
                await RegisterThumbnailImageAsync(image, newItem);
            }
        }

        private async Task RegisterThumbnailImageAsync(Image image, VideoItem item)
        {
            Dispatcher.VerifyAccess();
            var width = CalculateThumbnailDecodeWidth(image);
            if (_thumbnailRegistrations.TryGetValue(image, out var registered))
            {
                if (!ReferenceEquals(registered, item))
                {
                    UnregisterThumbnailImage(image);
                }
                else
                {
                    _thumbnailRegistrationWidths[image] = width;
                    if (_thumbnailRequestedWidths.TryGetValue(item, out var requestedWidth) && width > requestedWidth)
                    {
                        _thumbnailRequestedWidths[item] = width;
                        await ViewModel.RequestThumbnailAsync(item, width);
                    }
                    return;
                }
            }

            _thumbnailRegistrations.Add(image, item);
            _thumbnailRegistrationWidths.Add(image, width);
            _thumbnailRegistrationCounts.TryGetValue(item, out var activeCount);
            _thumbnailRegistrationCounts[item] = activeCount + 1;
            var itemIdentity = ViewModel.GetOrCreateThumbnailRuntimeIdentity(item);
            _performanceProbe.EnterImageRegistration(RuntimeHelpers.GetHashCode(image), itemIdentity);

            if (activeCount == 0)
            {
                _thumbnailRequestedWidths[item] = width;
                await ViewModel.RequestThumbnailAsync(item, width);
            }
            else if (_thumbnailRequestedWidths.TryGetValue(item, out var requestedWidth) && width > requestedWidth)
            {
                _thumbnailRequestedWidths[item] = width;
                await ViewModel.RequestThumbnailAsync(item, width);
            }
        }

        private void UnregisterThumbnailImage(Image image)
        {
            Dispatcher.VerifyAccess();
            if (!_thumbnailRegistrations.Remove(image, out var item))
            {
                return;
            }

            _thumbnailRegistrationWidths.Remove(image);
            var itemIdentity = ViewModel.GetOrCreateThumbnailRuntimeIdentity(item);
            _performanceProbe.LeaveImageRegistration(RuntimeHelpers.GetHashCode(image), itemIdentity);
            var activeCount = _thumbnailRegistrationCounts[item] - 1;
            if (activeCount < 0)
            {
                throw new InvalidOperationException("Thumbnail image registration count became negative.");
            }

            if (activeCount == 0)
            {
                _thumbnailRegistrationCounts.Remove(item);
                _thumbnailRequestedWidths.Remove(item);
                ViewModel.ReleaseThumbnail(item);
            }
            else
            {
                _thumbnailRegistrationCounts[item] = activeCount;
            }

            if (_thumbnailRegistrations.Count != _thumbnailRegistrationWidths.Count ||
                _thumbnailRegistrationCounts.Values.Sum() != _thumbnailRegistrations.Count)
            {
                throw new InvalidOperationException("Thumbnail image registration invariants were violated.");
            }
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

            if (item.ThumbnailLoadState == ThumbnailLoadState.Loaded)
            {
                return ReferenceEquals(image.Source, item.ThumbnailSource);
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

        internal int GetRealizedNonFallbackThumbnailSourceCount(ImageSource fallback) =>
            _thumbnailRegistrations.Values
                .DistinctBy(item => RuntimeHelpers.GetHashCode(item))
                .Count(item => item.ThumbnailSource is { } source && !ReferenceEquals(source, fallback));

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

        internal ListBox GetVideoListForTests() => VideoList;

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
            Loaded -= OnLoaded;
            DetachPerformanceRendering();
            ViewModel.Videos.CollectionChanged -= OnPerformanceVideosChanged;
            ViewModel.PlayVideoRequested -= OnPlayVideoRequested;
            foreach (var image in _thumbnailRegistrations.Keys.ToArray())
            {
                UnregisterThumbnailImage(image);
            }
            if (_thumbnailRegistrations.Count != 0 ||
                _thumbnailRegistrationWidths.Count != 0 ||
                _thumbnailRegistrationCounts.Count != 0 ||
                _thumbnailRequestedWidths.Count != 0)
            {
                throw new InvalidOperationException("Thumbnail registrations remained after window close cleanup.");
            }
            ViewModel.Dispose();
            _httpClient.Dispose();
            if (_translationService is IDisposable disposableTranslation)
            {
                disposableTranslation.Dispose();
            }
            base.OnClosed(e);
        }
    }
}








