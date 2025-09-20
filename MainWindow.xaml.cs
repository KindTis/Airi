using System.Windows;
using Airi.Services;
using Airi.ViewModels;
using Airi.Infrastructure;

namespace Airi
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            AppLogger.Info("Initializing MainWindow.");
            var libraryStore = new LibraryStore();
            var libraryScanner = new LibraryScanner(new FileSystemScanner());
            ViewModel = new MainViewModel(libraryStore, libraryScanner);
            DataContext = ViewModel;
            Loaded += OnLoaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("MainWindow loaded. Starting view model initialization.");
            await ViewModel.InitializeAsync();
            AppLogger.Info("View model initialization complete.");
        }
    }
}

