using System.IO;
using Airi.Domain;
using Airi.Infrastructure;
using Airi.Services;

namespace Airi.ViewModels;

internal sealed record VideoDeletionTarget(VideoItem Item, string LibraryPath, string AbsolutePath, string Title, string FileName);

public partial class MainViewModel
{
    private readonly IVideoRecycleService _videoRecycleService;
    private readonly Func<string, bool> _deletionFileProbe;
    private VideoDeletionTarget? _videoDeletionTarget;
    internal Task VideoDeletionTask { get; private set; } = Task.CompletedTask;

    internal bool IsVideoDeletionTarget(VideoItem item) => ReferenceEquals(_videoDeletionTarget?.Item, item);

    internal Task DeleteSelectedVideoAsync(
        Func<VideoDeletionTarget, Task<bool>> confirm,
        Func<VideoItem, Task> stopPreview)
    {
        _dispatcher.VerifyAccess();
        var item = SelectedVideo;
        if (item is null || item.Presence != VideoPresenceState.Available) return Task.CompletedTask;
        if (!CanMutateLibrary)
        {
            StatusMessage = "라이브러리 작업 중입니다. 작업이 끝난 뒤 다시 시도해 주세요.";
            return Task.CompletedTask;
        }

        VideoDeletionTarget target;
        try
        {
            var libraryPath = LibraryPathHelper.NormalizeLibraryPath(item.LibraryPath);
            var absolutePath = LibraryPathHelper.ResolveToAbsolute(libraryPath);
            target = new(item, libraryPath, absolutePath, item.Title, Path.GetFileName(absolutePath));
            if (!IsValidDeletionIdentity(target)) throw new IOException("삭제 대상 항목을 확인할 수 없습니다.");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            StatusMessage = $"비디오 삭제 실패: {Path.GetFileName(item.SourcePath)} — {ex.Message}";
            return Task.CompletedTask;
        }

        var lease = TryBeginLibraryMutation(LibraryMutationOwner.VideoDeletion);
        if (lease is null) return Task.CompletedTask;
        _videoDeletionTarget = target;
        RandomPlayCommand.RaiseCanExecuteChanged();
        // Publish the completion before invoking callbacks: a modal dialog pumps the dispatcher,
        // and a close request must see the in-flight operation even during confirmation.
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        VideoDeletionTask = completion.Task;
        _ = RunVideoDeletionAsync(target, lease, confirm, stopPreview, completion);
        return completion.Task;
    }

    private async Task RunVideoDeletionAsync(
        VideoDeletionTarget target,
        LibraryMutationLease lease,
        Func<VideoDeletionTarget, Task<bool>> confirm,
        Func<VideoItem, Task> stopPreview,
        TaskCompletionSource completion)
    {
        var moved = false;
        try
        {
            await ValidateDeletionTargetAsync(target, lease);
            if (!await confirm(target)) return;
            await ValidateDeletionTargetAsync(target, lease);
            await stopPreview(target.Item);
            await ValidateDeletionTargetAsync(target, lease);

            var result = await _videoRecycleService.RecycleAsync(target.AbsolutePath);
            EnsureCurrentLease(lease);
            if (result.Outcome != VideoRecycleOutcome.Succeeded)
            {
                StatusMessage = result.Outcome == VideoRecycleOutcome.Cancelled
                    ? $"휴지통 이동이 취소되었습니다: {target.FileName}"
                    : $"비디오 삭제 실패: {target.FileName} — {result.Reason ?? "휴지통 이동 완료를 확인할 수 없습니다."}";
                return;
            }

            moved = true;
            var visible = FilteredVideos.Cast<VideoItem>().ToList();
            var position = visible.IndexOf(target.Item);
            var next = position >= 0 ? visible.Skip(position + 1).Concat(visible.Take(position).Reverse()).FirstOrDefault() : SelectedVideo;
            var candidate = new LibraryData
            {
                Version = _library.Version,
                Videos = _library.Videos.Where(entry => !LibraryPathHelper.NormalizeLibraryPath(entry.Path)
                    .Equals(target.LibraryPath, StringComparison.OrdinalIgnoreCase)).ToList()
            };
            await _libraryStore.SaveAsync(candidate, CancellationToken.None);
            EnsureCurrentLease(lease);
            _library = candidate;
            _videoIndex.Remove(target.LibraryPath);
            RemoveFromMetadataQueue(target.LibraryPath);
            Videos.Remove(target.Item);
            RefreshActorList();
            FilteredVideos.Refresh();
            var remaining = FilteredVideos.Cast<VideoItem>().ToList();
            SelectedVideo = next is not null && remaining.Contains(next) ? next : remaining.FirstOrDefault();
            UpdateStatus(updateMessage: false);
            StatusMessage = $"휴지통으로 이동했습니다: {target.FileName}. {BuildLibrarySummary()}";
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Video deletion failed for '{target.FileName}'.", ex);
            if (moved)
            {
                var fileAvailable = await Task.Run(() => _deletionFileProbe(target.AbsolutePath));
                EnsureCurrentLease(lease);
                if (IsValidDeletionIdentity(target) && !fileAvailable) target.Item.Presence = VideoPresenceState.Missing;
                UpdateStatus(updateMessage: false);
                StatusMessage = $"휴지통 이동은 완료되었지만 라이브러리 저장에 실패했습니다: {target.FileName} — {ex.Message}";
            }
            else StatusMessage = $"비디오 삭제 실패: {target.FileName} — {ex.Message}";
        }
        finally
        {
            _videoDeletionTarget = null;
            ReleaseLibraryMutation(lease);
            RandomPlayCommand.RaiseCanExecuteChanged();
            completion.TrySetResult();
        }
    }

    private async Task ValidateDeletionTargetAsync(VideoDeletionTarget target, LibraryMutationLease lease)
    {
        EnsureCurrentLease(lease);
        if (!IsValidDeletionIdentity(target)) throw new IOException("대상 항목이나 파일 경로가 변경되었습니다.");
        var fileAvailable = await Task.Run(() => _deletionFileProbe(target.AbsolutePath));
        EnsureCurrentLease(lease);
        if (!IsValidDeletionIdentity(target) || !fileAvailable)
            throw new IOException("대상 항목이나 파일 경로가 변경되었거나 파일이 없습니다.");
    }

    private bool IsValidDeletionIdentity(VideoDeletionTarget target) =>
        !_disposed && Videos.Contains(target.Item) &&
        target.Item.Presence == VideoPresenceState.Available &&
        _videoIndex.TryGetValue(target.LibraryPath, out var indexed) && ReferenceEquals(indexed, target.Item) &&
        FindEntry(target.LibraryPath) is not null &&
        LibraryPathHelper.NormalizeLibraryPath(target.Item.LibraryPath).Equals(target.LibraryPath, StringComparison.OrdinalIgnoreCase) &&
        Path.GetFullPath(target.Item.SourcePath).Equals(target.AbsolutePath, StringComparison.OrdinalIgnoreCase);
}
