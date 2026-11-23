using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Airi.Domain;
using System.Windows.Input;
using Airi.ViewModels;

namespace Airi.Views;

public partial class GalleryMetadataEditorWindow : Window
{
    public event RoutedEventHandler? ChooseThumbnailRequested;
    public event RoutedEventHandler? GenerateThumbnailRequested;
    public event RoutedEventHandler? ResetThumbnailRequested;
    public GalleryMetadataEditorWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
    internal void SetThumbnailToolsVisible(bool visible)
    {
        ThumbnailTools.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarLayout(EditorBody.ActualWidth > 0 ? EditorBody.ActualWidth : Width);
    }

    private void OnEditorBodySizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) UpdateSidebarLayout(e.NewSize.Width);
    }

    private void UpdateSidebarLayout(double width)
    {
        var sideBySide = width >= 720;
        EditorSidebarColumn.Width = new GridLength(sideBySide && ThumbnailTools.Visibility == Visibility.Visible ? 266 : 0);
        Grid.SetColumn(EditorSidebar, sideBySide ? 0 : 1);
        Grid.SetRow(EditorSidebar, sideBySide ? 0 : 1);
        EditorSidebar.BorderThickness = sideBySide ? new Thickness(0, 0, 1, 0) : new Thickness(0, 1, 0, 0);
    }

    private void OnChooseThumbnailClick(object sender, RoutedEventArgs e) => ChooseThumbnailRequested?.Invoke(sender, e);
    private void OnGenerateGalleryThumbnailClick(object sender, RoutedEventArgs e) => GenerateThumbnailRequested?.Invoke(sender, e);
    private void OnResetGalleryThumbnailClick(object sender, RoutedEventArgs e) => ResetThumbnailRequested?.Invoke(sender, e);
    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not MainViewModel viewModel || !viewModel.CancelEditCommand.CanExecute(null)) return;
        e.Handled = true;
        viewModel.CancelEditCommand.Execute(null);
    }
}

public sealed class GalleryEditorFieldConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is GalleryEditorDraft draft && parameter is MetadataField field
            ? draft.Fields.FirstOrDefault(item => item.Field == field)
            : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
