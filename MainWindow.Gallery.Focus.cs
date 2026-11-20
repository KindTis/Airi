using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Airi.Infrastructure;

namespace Airi;

public partial class MainWindow
{
    private readonly record struct GalleryFocusBookmark(Guid Id, int Index);
    private GalleryFocusBookmark? _galleryFocusBookmark;
    private long _galleryFocusGeneration;
    private bool _explicitGalleryClose;
    internal Task GalleryFocusRestoreTask { get; private set; } = Task.CompletedTask;
    internal void CloseGalleryAndRestoreForTests() => CloseGalleryWithFocus();

    private void RememberGalleryFocus(VideoItem? item)
    {
        if (item is null) return;
        var index = VideoList.Items.IndexOf(item);
        if (index >= 0) _galleryFocusBookmark = new(item.Id, index);
        ++_galleryFocusGeneration;
    }
    private void RequestGalleryFocusRestore(GalleryFocusBookmark? bookmark, bool waitForPublication = false)
    {
        var generation = ++_galleryFocusGeneration;
        GalleryFocusRestoreTask = RestoreGalleryFocusAsync(bookmark, generation, waitForPublication);
    }
    private async Task RestoreGalleryFocusAsync(GalleryFocusBookmark? bookmark, long generation, bool waitForPublication)
    {
        // A committed edit publishes its final query after ending the draft. Restore into that result.
        if (waitForPublication)
        {
            await ViewModel.GalleryActionTask;
            try { await ViewModel.GalleryQueryTask; } catch (OperationCanceledException) { }
        }
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        if (generation != _galleryFocusGeneration || _previewShutdownStarted || !IsLoaded) return;
        var index = bookmark is { } saved ? VideoList.Items.Cast<VideoItem>().ToList().FindIndex(item => item.Id == saved.Id) : -1;
        if (index < 0 && bookmark is { } removed && VideoList.Items.Count > 0) index = Math.Clamp(removed.Index, 0, VideoList.Items.Count - 1);
        if (index < 0) { GalleryHost.MakeVisible(VideoList, new Rect(VideoList.RenderSize)); VideoList.Focus(); return; }
        var target = (VideoItem)VideoList.Items[index];
        GalleryHost.ScrollToIndex(index);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        UpdateLayout();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
        if (generation != _galleryFocusGeneration || _previewShutdownStarted || !VideoList.Items.Contains(target)) return;
        if (VideoList.ItemContainerGenerator.ContainerFromItem(target) is ListBoxItem container && container.DataContext is VideoItem current && current.Id == target.Id)
        {
            _galleryPopup?.SuppressNextFocus(target.Id);
            try { if (!container.Focus()) VideoList.Focus(); }
            finally { _galleryPopup?.ClearFocusSuppression(); }
        }
        else VideoList.Focus();
    }
}
