using System.IO;
using System.Windows;
using Airi.Infrastructure;
using Airi.Services;
using Airi.ViewModels;
using Airi.Views;
using Microsoft.Win32;

namespace Airi;

public partial class MainWindow
{
    private GalleryEditorDraft? _galleryThumbnailOwner;
    private long _galleryThumbnailGeneration;
    internal Func<string, CancellationToken, Task<byte[]>> GalleryThumbnailFileReader { get; set; } = File.ReadAllBytesAsync;
    internal Func<string, string, CancellationToken, Task<IReadOnlyList<VideoThumbnailCandidate>>>? GalleryThumbnailExtractorOverride { get; set; }
    internal Func<IReadOnlyList<VideoThumbnailCandidate>, CancellationToken, Task<ThumbnailSelectionResult>>? GalleryThumbnailSelectorOverride { get; set; }
    internal Task SelectGalleryThumbnailFileForTestsAsync(string path) => ViewModel.EditorDraft is { } draft ? RunGalleryThumbnailWorkAsync(draft, (generation, token) => LoadGalleryThumbnailFileAsync(draft, path, generation, token)) : Task.CompletedTask;
    internal Task GenerateGalleryThumbnailForTestsAsync() => ViewModel.EditorDraft is { } draft && ViewModel.InspectedVideo is { } item ? RunGalleryThumbnailWorkAsync(draft, (generation, token) => GenerateGalleryThumbnailAsync(draft, item, generation, token)) : Task.CompletedTask;
    internal Task GalleryThumbnailWorkForTests => _galleryThumbnailTask;
    private bool IsGalleryThumbnailCurrent(GalleryEditorDraft draft, long generation) => !_previewShutdownStarted && generation == _galleryThumbnailGeneration && ReferenceEquals(ViewModel.EditorDraft, draft) && _galleryThumbnailCancellation?.IsCancellationRequested == false;
    private void CancelGalleryThumbnailWork()
    {
        ++_galleryThumbnailGeneration;
        _galleryThumbnailCancellation?.Cancel();
    }
    private void SyncGalleryThumbnailLifetime()
    {
        if (_galleryEditorWindow is null) return;
        if (_galleryThumbnailOwner is { } owner && !ReferenceEquals(owner, ViewModel.EditorDraft)) CancelGalleryThumbnailWork();
        var idle = _galleryThumbnailTask.IsCompleted;
        ThumbnailTools.IsEnabled = idle && !ViewModel.IsSaving && !ViewModel.IsFetchingDraft;
        EditorPreview.IsEnabled = idle;
        EditorApply.IsEnabled = idle;
    }
    private Task RunGalleryThumbnailWorkAsync(GalleryEditorDraft draft, Func<long, CancellationToken, Task> operation)
    {
        if (!_galleryThumbnailTask.IsCompleted || !ReferenceEquals(ViewModel.EditorDraft, draft) || ViewModel.IsSaving || ViewModel.IsFetchingDraft) return Task.CompletedTask;
        var cancellation = _galleryThumbnailCancellation = new CancellationTokenSource();
        _galleryThumbnailOwner = draft;
        var generation = ++_galleryThumbnailGeneration;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _galleryThumbnailTask = completion.Task;
        SyncGalleryThumbnailLifetime();
        _ = CompleteAsync();
        return completion.Task;
        async Task CompleteAsync()
        {
            try { await operation(generation, cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (IsGalleryThumbnailCurrent(draft, generation)) ShowGalleryMessage("썸네일 준비 실패", ex.Message); }
            finally
            {
                // Dispose again after a non-cooperative completion so it cannot leave a late owned image.
                if (!ReferenceEquals(ViewModel.EditorDraft, draft) || generation != _galleryThumbnailGeneration) draft.ThumbnailEditor.Dispose();
                if (ReferenceEquals(_galleryThumbnailCancellation, cancellation)) { _galleryThumbnailCancellation = null; _galleryThumbnailOwner = null; }
                cancellation.Dispose();
                completion.TrySetResult();
                SyncGalleryThumbnailLifetime();
            }
        }
    }
    private async Task LoadGalleryThumbnailFileAsync(GalleryEditorDraft draft, string path, long generation, CancellationToken token)
    {
        var bytes = await GalleryThumbnailFileReader(path, token);
        token.ThrowIfCancellationRequested();
        if (!IsGalleryThumbnailCurrent(draft, generation)) return;
        await draft.ThumbnailEditor.UpdateThumbnailFromBytesAsync(bytes, Path.GetExtension(path), Path.GetFileName(path), token);
        token.ThrowIfCancellationRequested();
        if (IsGalleryThumbnailCurrent(draft, generation)) draft.SyncThumbnail();
    }
    private async Task GenerateGalleryThumbnailAsync(GalleryEditorDraft draft, VideoItem item, long generation, CancellationToken token)
    {
        var session = Path.Combine(Path.GetTempPath(), "Airi", "VideoThumbnails", Guid.NewGuid().ToString("N"));
        var extractor = new VideoThumbnailExtractor(Path.Combine(AppContext.BaseDirectory, "resources", "ffmpeg", "win-x64"));
        var batch = 0;
        try
        {
            while (true)
            {
                var directory = Path.Combine(session, $"batch-{++batch:D4}");
                var candidates = GalleryThumbnailExtractorOverride is { } extract ? await extract(item.SourcePath, directory, token) : await extractor.ExtractAsync(item.SourcePath, directory, Random.Shared, token);
                token.ThrowIfCancellationRequested();
                if (!IsGalleryThumbnailCurrent(draft, generation)) return;
                ThumbnailSelectionResult choice;
                if (GalleryThumbnailSelectorOverride is { } select) choice = await select(candidates, token);
                else { var picker = new ThumbnailSelectionWindow(candidates) { Owner = this }; choice = await picker.SelectAsync(token); }
                token.ThrowIfCancellationRequested();
                if (!IsGalleryThumbnailCurrent(draft, generation)) return;
                if (choice.Action == ThumbnailSelectionAction.Regenerate) continue;
                if (choice.Action == ThumbnailSelectionAction.Select && choice.FilePath is { } path) await LoadGalleryThumbnailFileAsync(draft, path, generation, token);
                break;
            }
        }
        finally { var error = await PathCleanup.DeleteDirectoryAsync(session); if (error is not null) AppLogger.Error("Temporary thumbnail cleanup failed.", error); }
    }
}
