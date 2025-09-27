using Airi;
using System.Windows;
using Airi.ViewModels;

namespace Airi.Views
{
    public partial class MetadataEditorWindow : Window
    {
        public MetadataEditorWindow(VideoItem item)
        {
            InitializeComponent();
            DataContext = new MetadataEditorViewModel(item);
        }

        public MetadataEditResult? Result { get; private set; }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (DataContext is not MetadataEditorViewModel vm)
            {
                DialogResult = false;
                return;
            }

            if (!vm.TryBuildResult(out var result, out var error))
            {
                MessageBox.Show(this, error ?? "입력 값을 확인해주세요.", "유효성 검사", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Result = result;
            DialogResult = true;
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}

