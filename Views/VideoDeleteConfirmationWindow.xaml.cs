using System.Windows;

namespace Airi.Views;

public partial class VideoDeleteConfirmationWindow : Window
{
    public VideoDeleteConfirmationWindow(string title, string fileName)
    {
        InitializeComponent();
        VideoTitle.Text = title;
        VideoFileName.Text = fileName;
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e) => DialogResult = true;
    private void OnCancelClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
