using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Airi.Services;
using Airi.Domain;
using Airi.ViewModels;
using Airi.Web;
using System.IO;
using System.Windows.Media;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainWindowVideoDeletionTests
{
    [Fact]
    public Task GalleryHistory_OpenRowsFollowNewestAndUndoOnlyTheirCapturedOperation() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync();
        var window = new MainWindow(vm); var body = (StackPanel)window.CreateGalleryHistoryBody(out var dispose);
        try
        {
            async Task Edit(string value) { await vm.BeginGalleryEditAsync(); vm.EditorDraft!.Fields.Single(field => field.Field == MetadataField.Studio).Value = value; vm.PreviewChangesCommand.Execute(null); await vm.ApplyGalleryChangesAsync(); }
            await Edit("first"); await Edit("second");
            var newest = vm.ChangeHistory[0]; var older = vm.ChangeHistory[1];
            Assert.Equal(new[] { newest.Id, older.Id }, body.Children.OfType<StackPanel>().Select(row => (Guid)row.Tag));
            var row = (StackPanel)body.Children[0]; var undo = row.Children.OfType<Button>().Single();
            Assert.True(undo.Command.CanExecute(null));
            f.FailSave = true; undo.Command.Execute(null); await vm.GalleryActionTask; WpfTestHost.DrainDispatcher(window.Dispatcher);
            Assert.True(newest.CanUndo); Assert.NotEmpty(newest.Error!); Assert.Equal(newest.Error, row.Children.OfType<TextBlock>().Last().Text); Assert.Equal("second", vm.SelectedVideo!.Studio);
            f.FailSave = false; undo.Command.Execute(null); await vm.GalleryActionTask; WpfTestHost.DrainDispatcher(window.Dispatcher);
            Assert.False(newest.CanUndo); Assert.Null(newest.Error); Assert.True(older.CanUndo); Assert.False(undo.Command.CanExecute(null));
            undo.Command.Execute(null); await vm.GalleryActionTask; Assert.Equal("first", vm.SelectedVideo!.Studio); Assert.True(older.CanUndo);
        }
        finally { dispose(); window.Close(); }
    });

    [Fact]
    public Task GalleryClose_CancelsDraftFetchBeforeWaitingForItsCompletion() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); var source = new CancellationWaitingSource(); using var vm = await f.CreateAsync(metadataSources: new[] { source });
        var window = new MainWindow(vm); WpfTestHost.Show(window); await window.InitializationTask;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); window.Closed += (_, _) => closed.TrySetResult();
        try
        {
            vm.FetchMetadataCommand.Execute(null); await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(source.Token.IsCancellationRequested);
            Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath)); Assert.Empty(f.Recycler.Paths);
        }
        finally { if (!closed.Task.IsCompleted) { vm.CancelEditCommand.Execute(null); window.Close(); } }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task GalleryThumbnail_DraftCancellationRejectsLateFileOrExtraction(bool extract) => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(); var window = new MainWindow(vm);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pickerCalls = 0; string? extractionDirectory = null; CancellationToken token = default;
        var filesBefore = Directory.GetFiles(f.Root).OrderBy(path => path).ToArray();
        try
        {
            WpfTestHost.Show(window); await window.InitializationTask; vm.InspectedVideo = vm.SelectedVideo; await vm.BeginGalleryEditAsync();
            var draft = vm.EditorDraft!; var original = draft.ThumbnailEditor.ThumbnailPath;
            window.GalleryThumbnailFileReader = async (_, cancellation) => { token = cancellation; started.TrySetResult(); await release.Task; return new byte[] { 1, 2, 3 }; };
            window.GalleryThumbnailExtractorOverride = async (_, directory, cancellation) => { token = cancellation; extractionDirectory = directory; Directory.CreateDirectory(directory); await File.WriteAllTextAsync(Path.Combine(directory, "owned.jpg"), "fake extraction"); started.TrySetResult(); await release.Task; return Array.Empty<VideoThumbnailCandidate>(); };
            window.GalleryThumbnailSelectorOverride = (_, _) => { pickerCalls++; throw new InvalidOperationException("Late picker must not open."); };
            var work = extract ? window.GenerateGalleryThumbnailForTestsAsync() : window.SelectGalleryThumbnailFileForTestsAsync("late.png");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); vm.CancelEditCommand.Execute(null); Assert.True(token.IsCancellationRequested);
            release.TrySetResult(); await work.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, pickerCalls); Assert.Null(vm.EditorDraft); Assert.Equal(original, draft.ThumbnailEditor.ThumbnailPath);
            if (extractionDirectory is not null) Assert.False(Directory.Exists(Path.GetDirectoryName(extractionDirectory)));
            Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath)); Assert.Equal(filesBefore, Directory.GetFiles(f.Root).OrderBy(path => path));
        }
        finally { release.TrySetResult(); await window.GalleryThumbnailWorkForTests; window.Close(); }
    });

    [Fact]
    public Task GalleryQuery_ImmediatelyClosesReadingAndRejectsDelayedSameIdsWorker() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(); var window = new MainWindow(vm);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            WpfTestHost.Show(window); await window.InitializationTask; window.UpdateLayout(); WpfTestHost.DrainDispatcher(window.Dispatcher);
            var list = window.GetVideoListForTests(); var item = vm.SelectedVideo!; var anchor = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(item));
            window.OpenGalleryForTests(item, anchor); WpfTestHost.DrainDispatcher(window.Dispatcher); Assert.True(window.GalleryPopupIsOpenForTests);
            var ids = vm.FilteredVideos.Cast<VideoItem>().Select(video => video.Id).ToArray();
            vm.GalleryQueryEvaluator = async (snapshot, query, _, _) => { if (query.Search == "Title") { started.TrySetResult(); await release.Task; returned.TrySetResult(); } return GalleryQueryEngine.Evaluate(snapshot, query); };
            vm.SearchQuery = "Title"; Assert.False(window.GalleryPopupIsOpenForTests); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.SearchQuery = ""; await VideoDeletionFixture.WaitForQueryAsync(vm); var generation = vm.PublishedQueryGeneration; Assert.Equal(ids, vm.FilteredVideos.Cast<VideoItem>().Select(video => video.Id));
            release.TrySetResult(); await returned.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Dispatcher.Yield(DispatcherPriority.ContextIdle); Assert.Equal(generation, vm.PublishedQueryGeneration); Assert.False(window.GalleryPopupIsOpenForTests);
        }
        finally { release.TrySetResult(); window.Close(); }
    });

    [Fact]
    public Task GalleryPresentation_MissingValuesKeepSourceAndExposeDialogAndEditorLabels() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture(); using var vm = await f.CreateAsync(); var window = new MainWindow(vm);
        try
        {
            WpfTestHost.Show(window); await window.InitializationTask;
            var item = vm.SelectedVideo!; item.Title = ""; item.Description = ""; item.Actors = Array.Empty<string>(); item.Genres = Array.Empty<string>(); item.Tags = Array.Empty<string>();
            window.UpdateLayout(); WpfTestHost.DrainDispatcher(window.Dispatcher);
            var anchor = Assert.IsType<ListBoxItem>(window.GetVideoListForTests().ItemContainerGenerator.ContainerFromItem(item));
            var cardPeer = UIElementAutomationPeer.CreatePeerForElement(anchor)!;
            Assert.Equal(Path.GetFileName(item.SourcePath), cardPeer.GetName());
            window.OpenGalleryForTests(item, anchor); WpfTestHost.DrainDispatcher(window.Dispatcher);
            var root = window.GalleryPopupRootForTests; root.UpdateLayout();
            var peer = UIElementAutomationPeer.CreatePeerForElement(root)!;
            Assert.Contains(Path.GetFileName(item.SourcePath), peer.GetName()); Assert.Contains("Escape", peer.GetHelpText());
            var expanded = Assert.IsAssignableFrom<IExpandCollapseProvider>(peer.GetPattern(PatternInterface.ExpandCollapse));
            Assert.Equal(ExpandCollapseState.Expanded, expanded.ExpandCollapseState);
            var labels = Descendants(root).OfType<TextBlock>().Select(text => text.Text).ToArray();
            Assert.Contains("설명이 아직 등록되지 않았습니다.", labels);
            Assert.Equal("", item.Title); Assert.Equal("", item.Description); Assert.Empty(item.Actors); Assert.Empty(item.Genres); Assert.Empty(item.Tags);
            await vm.BeginGalleryEditAsync(); root.UpdateLayout(); WpfTestHost.DrainDispatcher(window.Dispatcher);
            var inputs = Descendants(root).OfType<TextBox>().Where(input => input.DataContext is GalleryDraftField).ToArray();
            Assert.NotEmpty(inputs);
            foreach (var input in inputs)
            {
                var field = (GalleryDraftField)input.DataContext;
                Assert.Equal(field.Label, UIElementAutomationPeer.CreatePeerForElement(input)!.GetName());
                Assert.NotNull(AutomationProperties.GetLabeledBy(input)); Assert.NotEmpty(AutomationProperties.GetHelpText(input));
            }
            window.CloseGalleryAndRestoreForTests(); await window.GalleryFocusRestoreTask;
            Assert.Equal(ExpandCollapseState.Collapsed, expanded.ExpandCollapseState);
            Assert.Equal(f.InitialJson, await File.ReadAllTextAsync(f.Store.FilePath));
        }
        finally { window.Close(); }
        static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i); yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
    });

    private sealed class CancellationWaitingSource : IWebVideoMetaSource
    {
        public string Name => "Cancellation waiting test source";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public VideoMetadataSourceKind Kind => VideoMetadataSourceKind.LibreDmm;
        public string Host => "fixture.invalid";

        public async Task<WebVideoMetaResult> FetchAsync(MetadataProductCode code, bool includeThumbnail,
            ISet<string> blockedHosts, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new(null);
        }
    }

    [Fact]
    public Task InputScope_ModifiersRepeatAndHeldKeyCannotDeleteAgain() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var item = vm.SelectedVideo!;
        File.Delete(item.SourcePath);
        item.Presence = VideoPresenceState.Missing;
        vm.CurrentGalleryView = GalleryView.Missing;
        await VideoDeletionFixture.WaitForQueryAsync(vm);
        var window = new MainWindow(vm);
        var list = (ListBox)window.FindName("VideoList");
        var confirmations = 0;
        window.ConfirmGalleryOperationOverride = _ => { confirmations++; return Task.FromResult(false); };
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
        window.ConfirmGalleryOperationOverride = _ => Task.FromResult(true);
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
        Assert.Equal(3, vm.Videos.Count);
        Assert.Contains(vm.Videos, item => item.IsInRecycleBin);
    });

    [Fact]
    public Task ActualRoutedKeyHandlers_F1IsHandledAndDeleteKeyUpReleasesLatch() => WpfTestHost.RunAsync(async () =>
    {
        using var f = new VideoDeletionFixture();
        using var vm = await f.CreateAsync();
        var window = new MainWindow(vm);
        var list = (ListBox)window.FindName("VideoList");
        var confirmations = 0;
        window.ConfirmGalleryOperationOverride = _ => { confirmations++; return Task.FromResult(false); };
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

}
