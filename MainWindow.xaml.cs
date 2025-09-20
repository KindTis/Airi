using System.Windows;
using Airi.Services;
using Airi.ViewModels;

namespace Airi
{
    public partial class MainWindow : Window
    {
        public MainViewModel ViewModel { get; }

        public MainWindow()
        {
            InitializeComponent();
            var libraryStore = new LibraryStore();
            ViewModel = new MainViewModel(libraryStore);
            DataContext = ViewModel;
        }
    }
}
