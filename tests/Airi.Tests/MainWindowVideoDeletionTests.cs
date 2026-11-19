using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Airi.Services;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainWindowVideoDeletionTests
{
    [Fact]
    public Task InputScope_ModifiersRepeatAndHeldKeyCannotDeleteAgain() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var window = new MainWindow(vm);
        var list = (ListBox)window.FindName("VideoList");
        var confirmations = 0;
        window.ConfirmVideoDeletionOverride = _ => { confirmations++; return Task.FromResult(false); };
        try
        {
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, new TextBox());
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.Shift, false, list);
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.Control, false, list);
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, true, list);
            Assert.Equal(0, confirmations);
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
            Assert.Equal(1, confirmations);
            window.ReleaseVideoDeleteKey();
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
            Assert.Equal(2, confirmations);
            Assert.Empty(f.Recycler.Paths);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CloseDuringRecycle_AwaitsResultAndSaveBeforeDisposing() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<VideoRecycleResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Recycler.Override = _ => { started.SetResult(); return finish.Task; };
        var window = new MainWindow(vm);
        window.ConfirmVideoDeletionOverride = _ => Task.FromResult(true);
        var list = (ListBox)window.FindName("VideoList");
        var task = window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
        await started.Task;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.Closed += (_, _) => closed.SetResult();
        window.Close();
        Assert.False(vm.LifetimeToken.IsCancellationRequested);
        Assert.False(closed.Task.IsCompleted);
        finish.SetResult(new VideoRecycleResult(VideoRecycleOutcome.Succeeded));
        await task;
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.LifetimeToken.IsCancellationRequested);
        Assert.Equal(2, vm.Videos.Count);
    });

    [Fact]
    public Task ActualRoutedKeyHandlers_F1IsHandledAndDeleteKeyUpReleasesLatch() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var window = new MainWindow(vm);
        var list = (ListBox)window.FindName("VideoList");
        var confirmations = 0;
        window.ConfirmVideoDeletionOverride = _ => { confirmations++; return Task.FromResult(false); };
        try
        {
            WpfTestHost.Show(window);
            await window.InitializationTask;
            var source = PresentationSource.FromVisual(window)!;
            window.Hide(); // Keep all command execution below outside keyboard-focus transfer paths.
            var selected = vm.SelectedVideo;
            vm.SelectedVideo = null;
            var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.F1)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            window.RaiseEvent(down);
            Assert.True(down.Handled); // Actual XAML PreviewKeyDown connection, without opening an editor.
            vm.SelectedVideo = selected;
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
            Assert.Equal(1, confirmations);
            window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Delete)
            { RoutedEvent = Keyboard.PreviewKeyUpEvent });
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list);
            Assert.Equal(2, confirmations); // Actual XAML PreviewKeyUp connection released the held-key latch.
            Assert.Empty(f.Recycler.Paths);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task CloseDuringActualModalConfirmation_CancelsWithoutMovingOrSaving() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        var window = new MainWindow(vm);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoDeleteConfirmationWindow? dialog = null;
        window.Closed += (_, _) => closed.TrySetResult();
        window.VideoDeleteConfirmationFactory = target =>
        {
            dialog = new VideoDeleteConfirmationWindow(target.Title, target.FileName);
            WpfTestHost.ConfigureOffscreen(dialog);
            dialog.Loaded += (_, _) =>
            {
                seen.TrySetResult();
                dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(window.Close));
            };
            return dialog;
        };
        try
        {
            WpfTestHost.Show(window);
            await window.InitializationTask;
            var list = (ListBox)window.FindName("VideoList");
            await window.HandleVideoDeleteKeyAsync(Key.Delete, ModifierKeys.None, false, list).WaitAsync(TimeSpan.FromSeconds(10));
            await seen.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(window, dialog!.Owner);
            Assert.NotEqual(true, dialog.DialogResult);
            Assert.False(dialog.IsVisible);
            Assert.True(vm.LifetimeToken.IsCancellationRequested);
            Assert.Same(item, vm.SelectedVideo);
            Assert.Contains(item, vm.Videos);
            Assert.Empty(f.Recycler.Paths);
            Assert.Equal(f.InitialJson, await System.IO.File.ReadAllTextAsync(f.Store.FilePath));
        }
        finally
        {
            if (!closed.Task.IsCompleted) window.Close();
        }
    });

    [Fact]
    public Task Dialog_ShowsFullValues_DefaultCancelAndCloseAreFalse() => WpfTestHost.RunAsync(() =>
    {
        var title = new string('가', 300);
        var fileName = new string('a', 220) + ".mp4";
        var dialog = new VideoDeleteConfirmationWindow(title, fileName);
        Assert.Equal(title, ((TextBox)dialog.FindName("VideoTitle")).Text);
        Assert.Equal(fileName, ((TextBox)dialog.FindName("VideoFileName")).Text);
        var cancel = (Button)dialog.FindName("CancelButton");
        Assert.True(cancel.IsDefault);
        Assert.True(cancel.IsCancel);
        dialog.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(dialog.Close));
        Assert.NotEqual(true, WpfTestHost.ShowDialog(dialog));
        return Task.CompletedTask;
    });
}
