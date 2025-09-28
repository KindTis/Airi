using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            AppLogger.Info("Initializing MainWindow.");

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
            var metadataSources = new IWebVideoMetaSource[] { new NanoJavMetaSource(_httpClient) };
            var thumbnailCache = new ThumbnailCache();
            var webMetadataService = new WebMetadataService(
                metadataSources,
                thumbnailCache,
                _translationService,
                translationTargetLanguageCode);

            var oneFourOneJavCrawler = new OneFourOneJavCrawler(_translationService, translationTargetLanguageCode);

            ViewModel = new MainViewModel(libraryStore, libraryScanner, webMetadataService, oneFourOneJavCrawler);
            DataContext = ViewModel;
            ViewModel.PlayVideoRequested += OnPlayVideoRequested;
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
            await ViewModel.InitializeAsync();
            AppLogger.Info("View model initialization complete.");
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
            ViewModel.PlayVideoRequested -= OnPlayVideoRequested;
            _httpClient.Dispose();
            if (_translationService is IDisposable disposableTranslation)
            {
                disposableTranslation.Dispose();
            }
        }
    }
}








