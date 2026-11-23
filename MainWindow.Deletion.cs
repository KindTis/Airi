using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Airi;

public partial class MainWindow
{
    private bool _videoDeleteKeyHeld;
    internal async Task<bool> HandleVideoDeleteKeyAsync(Key key, ModifierKeys modifiers, bool repeat, DependencyObject? focused)
    {
        if (key != Key.Delete || modifiers != ModifierKeys.None || !IsVideoListFocus(focused)) return false;
        if (repeat || _videoDeleteKeyHeld || _previewShutdownStarted ||
            (IsGallerySelectionMode && ViewModel.CheckedCount == 0)) return true;
        _videoDeleteKeyHeld = true;
        try
        {
            ViewModel.DeleteCommand.Execute(null);
            await ViewModel.GalleryActionTask;
        }
        finally
        {
            if (!_previewShutdownStarted && IsVisible)
            {
                VideoList.Focus();
                if (ViewModel.SelectedVideo is { } selected)
                {
                    VideoList.ScrollIntoView(selected);
                }
            }
        }
        return true;
    }

    internal void ReleaseVideoDeleteKey() => _videoDeleteKeyHeld = false;

    private void OnVideoDeleteKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) ReleaseVideoDeleteKey();
    }

    private bool IsVideoListFocus(DependencyObject? focused)
    {
        for (var current = focused; current is not null;)
        {
            if (current is TextBoxBase or PasswordBox) return false;
            if (ReferenceEquals(current, VideoList)) return true;
            current = current is Visual ? VisualTreeHelper.GetParent(current) :
                current is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

}
