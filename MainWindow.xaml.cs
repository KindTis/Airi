using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
using System.Windows.Threading;
using System.Threading.Tasks;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Services.VideoPreview;
using Airi.ViewModels;
using Airi.Web;
using Airi.Views;

namespace Airi
{
    public partial class MainWindow : Window
    {
        internal readonly record struct RealizedThumbnailBindingDiagnostic(
            int ImageIdentity,
            long ItemIdentity,
            bool RegistrationMatchesDataContext,
            bool SourceMatchesCurrentItem,
            ThumbnailLoadState LoadState);

        private readonly ITextTranslationService _translationService;
        private readonly ThumbnailPerformanceProbe _performanceProbe;
        private readonly IThumbnailImageLoader? _thumbnailImageLoader;
        private bool _performanceRenderingAttached;
        private readonly Dictionary<Image, VideoItem> _thumbnailRegistrations = new();
        private readonly Dictionary<Image, int> _thumbnailRegistrationWidths = new();
        private readonly Dictionary<VideoItem, int> _thumbnailRegistrationCounts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<VideoItem, int> _thumbnailRequestedWidths = new(ReferenceEqualityComparer.Instance);
        private readonly TooltipPreviewController? _tooltipPreviewController;
        private readonly bool? _clientAreaAnimationsEnabledOverride;
        private Task _initializationTask = Task.CompletedTask;
        private bool _initializationStarted;
        private bool _previewShutdownStarted;
        private bool _previewShutdownComplete;
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
            var binaryFolder = Path.Combine(AppContext.BaseDirectory, "resources", "ffmpeg", "win-x64");
            _tooltipPreviewController = new TooltipPreviewController(
                new FfmpegVideoPreviewService(new FfmpegCorePreviewBackend(binaryFolder)),
                TimeProvider.System);

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
            var metadataSources = MetadataSourceConfiguration.Load(
                Path.Combine(AppContext.BaseDirectory, MetadataSourceConfiguration.FileName));
            var webMetadataService = new WebMetadataService(
                metadataSources,
                thumbnailCache,
                _translationService,
                translationTargetLanguageCode);

            ViewModel = new MainViewModel(
                libraryStore,
                libraryScanner,
                webMetadataService,
                thumbnailImageLoader);
            WireViewModelEvents();
        }

        internal MainWindow(
            MainViewModel viewModel,
            ThumbnailPerformanceProbe? performanceProbe = null,
            IThumbnailImageLoader? thumbnailImageLoader = null,
            TooltipPreviewController? tooltipPreviewController = null,
            bool? clientAreaAnimationsEnabledOverride = null,
            IVideoLauncher? videoLauncher = null)
        {
            InitializeComponent();
            _translationService = NullTranslationService.Instance;
            _performanceProbe = performanceProbe ?? ThumbnailPerformanceProbe.Disabled;
            _thumbnailImageLoader = thumbnailImageLoader;
            _tooltipPreviewController = tooltipPreviewController;
            _clientAreaAnimationsEnabledOverride = clientAreaAnimationsEnabledOverride;
            _videoLauncher = videoLauncher ?? new WindowsVideoLauncher();
            ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            WireViewModelEvents();
        }

        private void WireViewModelEvents()
        {
            DataContext = ViewModel;
            InitializeGallerySurface();
            ViewModel.PlayVideoRequested += OnPlayVideoRequested;
            ViewModel.Videos.CollectionChanged += OnPerformanceVideosChanged;
            Loaded += OnLoaded;
        }

        private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            try
            {
                if (HandleGalleryNavigationKey(e)) return;
                if (e.Key == Key.Delete)
                {
                    var focused = Keyboard.FocusedElement as DependencyObject;
                    if (Keyboard.Modifiers == ModifierKeys.None && IsVideoListFocus(focused))
                    {
                        e.Handled = true;
                        await HandleVideoDeleteKeyAsync(e.Key, Keyboard.Modifiers, e.IsRepeat, focused);
                    }
                }
                else if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None && !e.IsRepeat)
                {
                    e.Handled = true;
                    await OpenMetadataEditorAsync();
                }
            }
            catch (Exception ex) { AppLogger.Error("Unexpected keyboard command failure.", ex); }
        }

        private async Task OpenMetadataEditorAsync()
        {
            if (ViewModel.IsEditing || (IsGallerySelectionMode && ViewModel.CheckedCount == 0)) return;
            var focused = Keyboard.FocusedElement as DependencyObject;
            var focusedCard = focused is null ? null : ItemsControl.ContainerFromElement(VideoList, focused) as ListBoxItem;
            ViewModel.EditCommand.Execute(focusedCard?.DataContext as VideoItem);
            await ViewModel.GalleryActionTask;
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

        internal static int CalculateTooltipDecodeWidth(double dpiScaleX) =>
            Math.Clamp((int)Math.Ceiling(480d * dpiScaleX), 64, 960);

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

        internal IReadOnlyList<RealizedThumbnailBindingDiagnostic> GetRealizedThumbnailBindingDiagnostics() =>
            EnumerableRealizedVideoContainers()
                .SelectMany(container => EnumerateVisualDescendants<Image>(container))
                .Where(image => string.Equals(image.Tag as string, "MainCardThumbnail", StringComparison.Ordinal))
                .Select(image =>
                {
                    var item = image.DataContext as VideoItem;
                    var registrationMatches = item is not null &&
                                              _thumbnailRegistrations.TryGetValue(image, out var registered) &&
                                              ReferenceEquals(registered, item);
                    var runtime = item is null
                        ? ThumbnailRuntimeDiagnostics.Missing
                        : ViewModel.GetThumbnailRuntimeDiagnostics(item);
                    return new RealizedThumbnailBindingDiagnostic(
                        RuntimeHelpers.GetHashCode(image),
                        runtime.RuntimeIdentity,
                        registrationMatches,
                        item is not null &&
                        ReferenceEquals(image.Source, item.ThumbnailSource),
                        item?.ThumbnailLoadState ?? ThumbnailLoadState.NotRequested);
                })
                .ToArray();

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

            GalleryHost.ScrollToIndex(index);
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
            if (IsGallerySelectionMode || sender is not ListBox listBox)
            {
                return;
            }

            if (e.OriginalSource is not DependencyObject source ||
                ItemsControl.ContainerFromElement(listBox, source) is not ListBoxItem { DataContext: VideoItem video })
            {
                return;
            }

            e.Handled = true;
            TryPlayVideo(video);
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
            if (_previewShutdownStarted || ViewModel.IsVideoDeletionTarget(video)) return;
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

            if(video.IsInRecycleBin)return;
            var result=_videoLauncher.Play(video.SourcePath);
            if(!result.Succeeded)ShowGalleryMessage("영상을 재생할 수 없습니다",result.Reason??"파일을 확인해 주세요.");
        }
        protected override async void OnClosing(CancelEventArgs e)
        {
            if (ViewModel is null) { base.OnClosing(e); return; }
            if (!_previewShutdownComplete && (_tooltipPreviewController is not null || !ViewModel.VideoDeletionTask.IsCompleted || !ViewModel.GalleryActionTask.IsCompleted || !_galleryThumbnailTask.IsCompleted || ViewModel.IsEditing || ViewModel.IsFetchingDraft || ViewModel.IsFetchingMetadata))
            {
                e.Cancel = true;
                if (_previewShutdownStarted) return;
                _previewShutdownStarted = true;
                try
                {
                    var automaticMetadataStop = ViewModel.StopMetadataForShutdownAsync();
                    CancelGalleryThumbnailWork();
                    if (ViewModel.CancelEditCommand.CanExecute(null)) ViewModel.CancelEditCommand.Execute(null);
                    _galleryDialog?.Close();
                    await automaticMetadataStop;
                    await ViewModel.VideoDeletionTask;
                    await ViewModel.GalleryActionTask;
                    await StopGallerySurfaceAsync();
                    if (_tooltipPreviewController is not null) await _tooltipPreviewController.DisposeAsync();
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    AppLogger.Error("Unexpected video operation shutdown failure.", ex);
                }
                finally
                {
                    _previewShutdownComplete = true;
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(Close));
                }
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(System.EventArgs e)
        {
            if (ViewModel is null) { base.OnClosed(e); return; }
            Loaded -= OnLoaded;
            StopGalleryPointerTracking();
            CloseGalleryEditorWindow();
            _galleryCardMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
            _galleryPopup?.Dispose();
            ViewModel.PropertyChanged -= OnGalleryPropertyChanged;
            ViewModel.GalleryScrollToTopRequested -= OnGalleryScrollToTop;
            ViewModel.GalleryQueryRequested -= CloseGalleryReadingForQuery;
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
            if (_translationService is IDisposable disposableTranslation)
            {
                disposableTranslation.Dispose();
            }
            base.OnClosed(e);
        }
    }
}








