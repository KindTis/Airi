using Airi;
using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Airi.ViewModels;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Web;
using Microsoft.Win32;

namespace Airi.Views
{
    public partial class MetadataEditorWindow : Window
    {
        private readonly VideoThumbnailExtractor _thumbnailExtractor;
        private readonly string? _sourcePath;
        private readonly Func<IReadOnlyList<VideoThumbnailCandidate>, CancellationToken,
            Task<ThumbnailSelectionResult>> _selectGeneratedThumbnailAsync;
        private readonly Func<string, Task<Exception?>> _deleteTemporaryDirectoryAsync;
        private readonly Func<string, Task<Exception?>> _deleteGeneratedCacheAsync;
        private readonly Action<string, string, MessageBoxImage> _showThumbnailMessage;
        private readonly HashSet<string> _generatedThumbnailCachePaths =
            new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _thumbnailGenerationCancellation;
        private Task _thumbnailGenerationTask = Task.CompletedTask;
        private Task _closeTask = Task.CompletedTask;
        private bool _isInteractionInProgress;
        private bool _isThumbnailGenerationInProgress;
        private bool _closeStarted;
        private bool _allowClose;
        private bool _isClosed;

        public MetadataEditorWindow(VideoItem item)
            : this(
                item,
                new VideoThumbnailExtractor(Path.Combine(
                    AppContext.BaseDirectory,
                    "resources",
                    "ffmpeg",
                    "win-x64")),
                null,
                null,
                null,
                null)
        {
        }

        internal MetadataEditorWindow(
            VideoItem item,
            VideoThumbnailExtractor thumbnailExtractor,
            Func<IReadOnlyList<VideoThumbnailCandidate>, CancellationToken,
                Task<ThumbnailSelectionResult>>? selectGeneratedThumbnailAsync,
            Func<string, Task<Exception?>>? deleteTemporaryDirectoryAsync,
            Func<string, Task<Exception?>>? deleteGeneratedCacheAsync,
            Action<string, string, MessageBoxImage>? showThumbnailMessage)
        {
            ArgumentNullException.ThrowIfNull(item);
            InitializeComponent();
            _thumbnailExtractor = thumbnailExtractor ??
                throw new ArgumentNullException(nameof(thumbnailExtractor));
            _sourcePath = item.SourcePath;
            _selectGeneratedThumbnailAsync = selectGeneratedThumbnailAsync ??
                SelectGeneratedThumbnailAsync;
            _deleteTemporaryDirectoryAsync = deleteTemporaryDirectoryAsync ??
                PathCleanup.DeleteDirectoryAsync;
            _deleteGeneratedCacheAsync = deleteGeneratedCacheAsync ??
                PathCleanup.DeleteFileAsync;
            _showThumbnailMessage = showThumbnailMessage ??
                ((message, title, image) =>
                    MessageBox.Show(this, message, title, MessageBoxButton.OK, image));
            DataContext = new MetadataEditorViewModel(item);
            KeyDown += OnWindowKeyDown;
        }

        public MetadataEditResult? Result { get; private set; }
        internal Task ThumbnailGenerationTask => _thumbnailGenerationTask;
        internal Task CloseTask => _closeTask;

        private async void OnSelectThumbnailClick(object sender, RoutedEventArgs e)
        {
            if (_isThumbnailGenerationInProgress || _closeStarted)
            {
                return;
            }

            if (DataContext is not MetadataEditorViewModel vm)
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Title = "썸네일 이미지 선택",
                Filter = "이미지 파일 (*.jpg;*.jpeg;*.png;*.webp;*.bmp)|*.jpg;*.jpeg;*.png;*.webp;*.bmp|모든 파일 (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
            {
                try
                {
                    var updated = await vm.UpdateThumbnailFromFileAsync(dialog.FileName);
                    if (!updated)
                    {
                        MessageBox.Show(this, "이미지 파일을 불러오지 못했습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"썸네일을 업데이트하는 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OnResetThumbnailClick(object sender, RoutedEventArgs e)
        {
            if (_isThumbnailGenerationInProgress || _closeStarted)
            {
                return;
            }

            if (DataContext is MetadataEditorViewModel vm)
            {
                vm.ResetThumbnail();
            }
        }

        private void OnGenerateThumbnailClick(object sender, RoutedEventArgs e)
        {
            if (_isInteractionInProgress || _isThumbnailGenerationInProgress || _closeStarted)
            {
                return;
            }

            _thumbnailGenerationTask = GenerateThumbnailAsync();
        }

        private void OnSaveClick(object sender, RoutedEventArgs e)
        {
            if (_isThumbnailGenerationInProgress || _closeStarted)
            {
                return;
            }

            if (DataContext is not MetadataEditorViewModel vm)
            {
                BeginClose(save: false, result: null);
                return;
            }

            if (!vm.TryBuildResult(out var result, out var error))
            {
                MessageBox.Show(this, error ?? "입력을 확인해주세요.", "입력 오류", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            BeginClose(save: true, result);
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            BeginClose(save: false, result: null);
        }

        private void OnWindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                BeginClose(save: false, result: null);
            }
        }

        private async void OnTryParseOn141JavClick(object sender, RoutedEventArgs e)
        {
            if (_isThumbnailGenerationInProgress || _closeStarted)
            {
                return;
            }

            if (DataContext is not MetadataEditorViewModel vm)
            {
                return;
            }

            var normalized = LibraryPathHelper.NormalizeCode(vm.Title);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                MessageBox.Show(this, "검색에 사용할 제목이 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (Owner is not MainWindow mainWindow)
            {
                AppLogger.Info("[MetadataEditor] Unable to access crawler from metadata editor window owner.");
                MessageBox.Show(this, "크롤러에 접근할 수 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            AppLogger.Info($"[MetadataEditor] Initiating crawler parse for '{vm.Title}' (normalized: '{normalized}').");

            SetInteractionInProgress(true);
            try
            {
                var result = await mainWindow.ViewModel.TryFetchOneFourOneJavMetadataAsync(normalized);
                if (result is null)
                {
                    AppLogger.Info("[MetadataEditor] No metadata payload was returned from 141jav.");
                    MessageBox.Show(this, "141jav에서 메타데이터를 찾지 못했습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var applied = await ApplyCrawlerResultAsync(vm, result);
                if (!applied)
                {
                    MessageBox.Show(this, "적용할 새 메타데이터가 없습니다.", "안내", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                AppLogger.Info("[MetadataEditor] Crawler parse completed.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("[MetadataEditor] Unexpected exception while parsing crawler page.", ex);
                MessageBox.Show(this, $"141jav 메타데이터를 적용하는 중 오류가 발생했습니다.\n{ex.Message}", "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetInteractionInProgress(false);
            }
        }

        private async Task<bool> ApplyCrawlerResultAsync(MetadataEditorViewModel vm, WebVideoMetaResult result)
        {
            AppLogger.Info("[MetadataEditor] Parsing crawler metadata result.");
            var applied = false;
            var meta = result.Meta;

            if (meta.Date is DateOnly releaseDate)
            {
                AppLogger.Info($"[MetadataEditor] Applying release date from crawler: {releaseDate:yyyy-MM-dd}.");
                vm.ReleaseDate = releaseDate.ToDateTime(TimeOnly.MinValue);
                applied = true;
            }

            if (meta.Tags.Count > 0)
            {
                AppLogger.Info($"[MetadataEditor] Applying {meta.Tags.Count} tags from crawler.");
                vm.TagsText = string.Join(Environment.NewLine, meta.Tags);
                applied = true;
            }

            if (meta.Actors.Count > 0)
            {
                AppLogger.Info($"[MetadataEditor] Applying {meta.Actors.Count} actors from crawler.");
                vm.ActorsText = string.Join(Environment.NewLine, meta.Actors);
                applied = true;
            }

            if (!string.IsNullOrWhiteSpace(meta.Description))
            {
                vm.Description = meta.Description;
                AppLogger.Info("[MetadataEditor] Applied crawler description to metadata editor.");
                applied = true;
            }

            if (result.ThumbnailBytes is { Length: > 0 })
            {
                var updated = await vm.UpdateThumbnailFromBytesAsync(result.ThumbnailBytes, result.ThumbnailExtension);
                if (!updated)
                {
                    AppLogger.Info("[MetadataEditor] Thumbnail update from crawler image was not applied.");
                    MessageBox.Show(this, "썸네일을 갱신하지 못했습니다.", "경고", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else
                {
                    AppLogger.Info("[MetadataEditor] Thumbnail updated from crawler image.");
                    applied = true;
                }
            }

            return applied;
        }

        private void SetInteractionInProgress(bool isInProgress)
        {
            _isInteractionInProgress = isInProgress;
            UpdateActionAvailability();
        }

        private async Task<ThumbnailSelectionResult> SelectGeneratedThumbnailAsync(
            IReadOnlyList<VideoThumbnailCandidate> candidates,
            CancellationToken cancellationToken)
        {
            var window = new ThumbnailSelectionWindow(candidates)
            {
                Owner = this
            };
            return await window.SelectAsync(cancellationToken);
        }

        private async Task GenerateThumbnailAsync()
        {
            if (DataContext is not MetadataEditorViewModel vm)
            {
                return;
            }

            var sourcePath = _sourcePath;
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            {
                _showThumbnailMessage(
                    "썸네일을 생성할 영상 파일을 찾을 수 없습니다.",
                    "안내",
                    MessageBoxImage.Information);
                return;
            }

            using var cancellation = new CancellationTokenSource();
            _thumbnailGenerationCancellation = cancellation;
            SetThumbnailGenerationInProgress(true);
            // ponytail: 배치별 즉시 삭제 대신 세션 루트에서 한 번에 정리한다.
            // 다시 생성 디스크 사용이 실제 문제가 될 때만 배치별 수명주기를 복잡하게 만든다.
            var sessionRoot = Path.Combine(
                Path.GetTempPath(),
                "Airi",
                "VideoThumbnails",
                Guid.NewGuid().ToString("N"));
            var batchNumber = 0;
            Exception? operationFailure = null;
            Exception? temporaryCleanupFailure = null;
            var failureStage = "생성";

            try
            {
                var currentCandidates = await ExtractBatchAsync();

                while (true)
                {
                    var selection = await _selectGeneratedThumbnailAsync(
                        currentCandidates,
                        cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();

                    if (selection.Action == ThumbnailSelectionAction.Cancel)
                    {
                        break;
                    }
                    if (selection.Action == ThumbnailSelectionAction.Regenerate)
                    {
                        try
                        {
                            var replacement = await ExtractBatchAsync();
                            currentCandidates = replacement;
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Error("[MetadataEditor] Thumbnail regeneration failed.", ex);
                            _showThumbnailMessage(
                                $"썸네일을 다시 생성하지 못했습니다.\n{ex.Message}",
                                "오류",
                                MessageBoxImage.Error);
                        }
                        continue;
                    }
                    if (selection.Action == ThumbnailSelectionAction.Select &&
                        !string.IsNullOrWhiteSpace(selection.FilePath))
                    {
                        failureStage = "적용";
                        var updated = await vm.UpdateThumbnailFromFileAsync(
                            selection.FilePath,
                            CancellationToken.None);
                        if (!updated)
                        {
                            throw new InvalidDataException(
                                "선택한 썸네일 이미지를 캐시에 저장하지 못했습니다.");
                        }
                        _generatedThumbnailCachePaths.Add(Path.GetFullPath(
                            LibraryPathHelper.ResolveToAbsolute(vm.ThumbnailPath)));
                        break;
                    }

                    throw new InvalidDataException("잘못된 썸네일 선택 결과입니다.");
                }

                async Task<IReadOnlyList<VideoThumbnailCandidate>> ExtractBatchAsync()
                {
                    var batchDirectory = Path.Combine(
                        sessionRoot,
                        $"batch-{++batchNumber:D4}");
                    return await _thumbnailExtractor.ExtractAsync(
                        sourcePath,
                        batchDirectory,
                        Random.Shared,
                        cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                operationFailure = ex;
                AppLogger.Error($"[MetadataEditor] Thumbnail {failureStage} failed.", ex);
            }
            finally
            {
                temporaryCleanupFailure = await _deleteTemporaryDirectoryAsync(sessionRoot);
                if (ReferenceEquals(_thumbnailGenerationCancellation, cancellation))
                {
                    _thumbnailGenerationCancellation = null;
                }
                if (!_isClosed)
                {
                    SetThumbnailGenerationInProgress(false);
                }
            }

            if (operationFailure is not null)
            {
                var message = $"썸네일을 {failureStage}하지 못했습니다.\n{operationFailure.Message}";
                if (temporaryCleanupFailure is not null)
                {
                    message +=
                        $"\n임시 썸네일 파일을 정리하지 못했습니다.\n" +
                        $"{temporaryCleanupFailure.Message}\n잔존 위치:\n{sessionRoot}";
                }
                _showThumbnailMessage(message, "오류", MessageBoxImage.Error);
            }
            else if (temporaryCleanupFailure is not null)
            {
                _showThumbnailMessage(
                    $"임시 썸네일 파일을 정리하지 못했습니다.\n" +
                    $"{temporaryCleanupFailure.Message}\n잔존 위치:\n{sessionRoot}",
                    "정리 경고",
                    MessageBoxImage.Warning);
            }
        }

        private void SetThumbnailGenerationInProgress(bool isInProgress)
        {
            _isThumbnailGenerationInProgress = isInProgress;
            UpdateActionAvailability();
        }

        private void UpdateActionAvailability()
        {
            var isGenerating = _isThumbnailGenerationInProgress;
            var isIdle = !_isInteractionInProgress && !isGenerating;
            SelectThumbnailButton.IsEnabled = !isGenerating;
            GenerateThumbnailButton.IsEnabled = isIdle;
            ResetThumbnailButton.IsEnabled = !isGenerating;
            TryParseOn141JavButton.IsEnabled = isIdle;
            CancelButton.IsEnabled = !_isInteractionInProgress;
            SaveButton.IsEnabled = isIdle;
        }

        private async Task<IReadOnlyList<string>> CleanupGeneratedCachesAsync(
            string? keepRelativePath)
        {
            var keepAbsolutePath = string.IsNullOrWhiteSpace(keepRelativePath)
                ? null
                : Path.GetFullPath(LibraryPathHelper.ResolveToAbsolute(keepRelativePath));
            var targets = _generatedThumbnailCachePaths
                .Where(path => !string.Equals(
                    path,
                    keepAbsolutePath,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var results = await Task.WhenAll(targets.Select(async path =>
                (Path: path, Failure: await _deleteGeneratedCacheAsync(path))));

            foreach (var result in results.Where(result => result.Failure is null))
            {
                _generatedThumbnailCachePaths.Remove(result.Path);
            }

            return results
                .Where(result => result.Failure is not null)
                .Select(result => result.Path)
                .ToArray();
        }

        private void BeginClose(bool save, MetadataEditResult? result)
        {
            if (_closeStarted)
            {
                return;
            }

            _closeStarted = true;
            _closeTask = CompleteCloseAsync(save, result);
        }

        private async Task CompleteCloseAsync(bool save, MetadataEditResult? result)
        {
            await Task.Yield();
            _thumbnailGenerationCancellation?.Cancel();
            try
            {
                await _thumbnailGenerationTask;
            }
            catch (OperationCanceledException)
            {
            }

            var residualPaths = await CleanupGeneratedCachesAsync(
                save ? result?.ThumbnailPath : null);
            if (residualPaths.Count > 0)
            {
                _showThumbnailMessage(
                    $"생성한 썸네일 캐시를 정리하지 못했습니다.\n잔존 위치:\n" +
                    string.Join(Environment.NewLine, residualPaths),
                    "정리 경고",
                    MessageBoxImage.Warning);
            }

            if (save && result is MetadataEditResult savedResult)
            {
                Result = savedResult;
            }
            _allowClose = true;
            if (save)
            {
                DialogResult = true;
            }
            else
            {
                Close();
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_allowClose)
            {
                base.OnClosing(e);
                return;
            }

            e.Cancel = true;
            BeginClose(save: false, result: null);
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _isClosed = true;
            base.OnClosed(e);
        }
    }
}
