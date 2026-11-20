using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Airi.Infrastructure;
using Airi.ViewModels;
using Airi.Views;

namespace Airi;

public partial class MainWindow
{
    private bool _videoDeleteKeyHeld;
    private VideoDeleteConfirmationWindow? _videoDeleteConfirmation;
    private readonly Dictionary<VideoItem, (ListBoxItem Container, ToolTip Tooltip, Guid SessionId)> _videoTooltips = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<VideoItem, Task> _videoPreviewRuns = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<VideoItem, Task> _videoTooltipStops = new(ReferenceEqualityComparer.Instance);
    internal Func<VideoDeletionTarget, Task<bool>>? ConfirmVideoDeletionOverride { get; set; }
    internal Func<VideoDeletionTarget, VideoDeleteConfirmationWindow>? VideoDeleteConfirmationFactory { get; set; }

    internal async Task<bool> HandleVideoDeleteKeyAsync(Key key, ModifierKeys modifiers, bool repeat, DependencyObject? focused)
    {
        if (key != Key.Delete || modifiers != ModifierKeys.None || !IsVideoListFocus(focused)) return false;
        if (repeat || _videoDeleteKeyHeld || _previewShutdownStarted) return true;
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

    private Task<bool> ConfirmVideoDeletionAsync(VideoDeletionTarget target)
    {
        if (_previewShutdownStarted) return Task.FromResult(false);
        if (ConfirmVideoDeletionOverride is { } confirm) return confirm(target);
        var dialog = VideoDeleteConfirmationFactory?.Invoke(target) ?? new VideoDeleteConfirmationWindow(target.Title, target.FileName);
        dialog.Owner = this;
        _videoDeleteConfirmation = dialog;
        dialog.PreviewKeyUp += OnVideoDeleteKeyUp;
        try { return Task.FromResult(dialog.ShowDialog() == true); }
        finally
        {
            dialog.PreviewKeyUp -= OnVideoDeleteKeyUp;
            _videoDeleteConfirmation = null;
        }
    }

    private async Task StopDeletedVideoPreviewAsync(VideoItem item)
    {
        // Only this video's tooltip is stopped; external default-player processes are untouched.
        if (_videoTooltips.TryGetValue(item, out var active))
        {
            if (ReferenceEquals(active.Tooltip.Content, item) &&
                active.Tooltip.Tag is TooltipPreviewState state && state.SessionId == active.SessionId)
            {
                await CloseVideoTooltipAsync(active.Container, active.Tooltip);
            }
            else
            {
                // The same container/tooltip may now belong to another video. Never close it;
                // stop only the captured session, then await its original media completion below.
                _videoTooltips.Remove(item);
                if (_tooltipPreviewController is not null) await _tooltipPreviewController.StopAsync(active.SessionId);
            }
        }
        else if (VideoList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem container &&
            ToolTipService.GetToolTip(container) is ToolTip tooltip && ReferenceEquals(tooltip.Content, item))
        {
            await CloseVideoTooltipAsync(container, tooltip);
        }
        if (_videoTooltipStops.TryGetValue(item, out var stopping)) await stopping;
        if (_videoPreviewRuns.TryGetValue(item, out var running)) await running;
    }
}
