using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Web;

namespace Airi
{
    public partial class MainWindow : Window
    {
        private readonly HttpClient _httpClient = new();
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            AppLogger.Info("Initializing MainWindow.");

            var libraryStore = new LibraryStore();
            var libraryScanner = new LibraryScanner(new FileSystemScanner());
            var metadataSources = new IWebVideoMetaSource[] { new NanoJavMetaSource(_httpClient) };
            var thumbnailCache = new ThumbnailCache();
            var webMetadataService = new WebMetadataService(metadataSources, thumbnailCache);

            ViewModel = new MainViewModel(libraryStore, libraryScanner, webMetadataService);
            DataContext = ViewModel;
            ViewModel.PlayVideoRequested += OnPlayVideoRequested;
            Loaded += OnLoaded;
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
        }
    }
}




