using System.Net.Http;
using System.Windows;
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
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("MainWindow loaded. Starting view model initialization.");
            await ViewModel.InitializeAsync();
            AppLogger.Info("View model initialization complete.");
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            _httpClient.Dispose();
        }
    }
}
