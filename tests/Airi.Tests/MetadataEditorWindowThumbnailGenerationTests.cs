using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Services.VideoPreview;
using Airi.ViewModels;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MetadataEditorWindowThumbnailGenerationTests
{
    [Fact]
    public Task Generation_MissingSourceDoesNotProbeOrReplaceThumbnail() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture(hasSource: false);
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var messages = new List<ThumbnailMessage>();
            var window = fixture.CreateWindow(
                runner,
                (_, _) => Task.FromResult(new ThumbnailSelectionResult(ThumbnailSelectionAction.Cancel, null)),
                messages: messages);
            window.Show();
            var viewModel = Assert.IsType<MetadataEditorViewModel>(window.DataContext);
            var originalThumbnail = viewModel.ThumbnailPath;

            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;

            Assert.Equal(0, runner.ProbeCount);
            Assert.Equal(originalThumbnail, viewModel.ThumbnailPath);
            Assert.Contains(messages, message =>
                message.Message.Contains("영상 파일을 찾을 수 없습니다", StringComparison.Ordinal));
            Click(window.CancelButton);
            await window.CloseTask;
        });

    [Fact]
    public Task Generation_DisablesFiveConflictingActionsButLeavesCancelEnabled() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.BlockUntilCanceled);
            var window = fixture.CreateWindow(
                runner,
                (_, _) => throw new InvalidOperationException("selection should not open"));
            window.Show();

            Click(window.GenerateThumbnailButton);
            var firstTask = window.ThumbnailGenerationTask;
            Click(window.GenerateThumbnailButton);
            await runner.TwoFfmpegStarted.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Same(firstTask, window.ThumbnailGenerationTask);
            Assert.False(window.SelectThumbnailButton.IsEnabled);
            Assert.False(window.GenerateThumbnailButton.IsEnabled);
            Assert.False(window.ResetThumbnailButton.IsEnabled);
            Assert.False(window.TryParseOn141JavButton.IsEnabled);
            Assert.False(window.SaveButton.IsEnabled);
            Assert.True(window.CancelButton.IsEnabled);
            var buttonPanel = Assert.IsType<StackPanel>(window.SelectThumbnailButton.Parent);
            Assert.Equal(
                new[] { window.SelectThumbnailButton, window.GenerateThumbnailButton, window.ResetThumbnailButton },
                buttonPanel.Children.OfType<Button>().Take(3));

            Click(window.CancelButton);
            await window.CloseTask;
            Assert.True(runner.CanceledActiveCount >= 1);
        });

    [Fact]
    public Task RegenerateSuccess_ReplacesCandidatesOnlyAfterAllFiveSucceed() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            string[]? originalCandidates = null;
            string? sessionRoot = null;
            var calls = 0;
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) =>
                {
                    calls++;
                    var paths = candidates.Select(candidate => candidate.FilePath).ToArray();
                    if (calls == 1)
                    {
                        originalCandidates = paths;
                        sessionRoot = SessionRoot(paths[0]);
                        return Task.FromResult(new ThumbnailSelectionResult(
                            ThumbnailSelectionAction.Regenerate,
                            null));
                    }

                    Assert.NotNull(originalCandidates);
                    Assert.All(originalCandidates, path => Assert.True(File.Exists(path)));
                    Assert.NotEqual(
                        Path.GetDirectoryName(originalCandidates[0]),
                        Path.GetDirectoryName(paths[0]));
                    Assert.EndsWith("batch-0002", Path.GetDirectoryName(paths[0]), StringComparison.Ordinal);
                    return Task.FromResult(new ThumbnailSelectionResult(
                        ThumbnailSelectionAction.Select,
                        paths[0]));
                });
            window.Show();

            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;

            Assert.Equal(2, calls);
            Assert.NotNull(sessionRoot);
            Assert.False(Directory.Exists(sessionRoot));
            Click(window.CancelButton);
            await window.CloseTask;
        });

    [Fact]
    public Task RegenerateFailure_ReopensOriginalCandidatesAndShowsError() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var innerRunner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var runner = new FailNthFfmpegRunner(innerRunner, 6);
            var messages = new List<ThumbnailMessage>();
            string[]? originalCandidates = null;
            var calls = 0;
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) =>
                {
                    calls++;
                    var paths = candidates.Select(candidate => candidate.FilePath).ToArray();
                    if (calls == 1)
                    {
                        originalCandidates = paths;
                        return Task.FromResult(new ThumbnailSelectionResult(
                            ThumbnailSelectionAction.Regenerate,
                            null));
                    }

                    Assert.Equal(originalCandidates, paths);
                    Assert.All(paths, path => Assert.Contains("batch-0001", path, StringComparison.Ordinal));
                    return Task.FromResult(new ThumbnailSelectionResult(
                        ThumbnailSelectionAction.Select,
                        paths[0]));
                },
                messages: messages);
            window.Show();

            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;

            Assert.Equal(2, calls);
            Assert.Contains(messages, message =>
                message.Message.Contains("다시 생성하지 못했습니다", StringComparison.Ordinal));
            Click(window.CancelButton);
            await window.CloseTask;
        });

    [Fact]
    public Task SelectionCancel_DeletesWholeSessionRoot() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            string? sessionRoot = null;
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) =>
                {
                    sessionRoot = SessionRoot(candidates[0].FilePath);
                    return Task.FromResult(new ThumbnailSelectionResult(
                        ThumbnailSelectionAction.Cancel,
                        null));
                });
            window.Show();

            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;

            Assert.NotNull(sessionRoot);
            Assert.False(Directory.Exists(sessionRoot));
            Click(window.CancelButton);
            await window.CloseTask;
        });

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("close")]
    public Task F1CloseDuringExtraction_CancelsProcessesAndAwaitsTemporaryCleanup(string action) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.BlockUntilCanceled);
            var cleanupStarted = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCleanup = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var window = fixture.CreateWindow(
                runner,
                (_, _) => throw new InvalidOperationException("selection should not open"),
                deleteTemporaryDirectoryAsync: async path =>
                {
                    cleanupStarted.TrySetResult(path);
                    await releaseCleanup.Task;
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    return null;
                });
            window.Show();
            Click(window.GenerateThumbnailButton);
            await runner.TwoFfmpegStarted.WaitAsync(TimeSpan.FromSeconds(2));

            TriggerClose(window, action);
            var sessionRoot = await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.False(window.CloseTask.IsCompleted);
            Assert.True(Directory.Exists(sessionRoot));
            releaseCleanup.TrySetResult();
            await window.CloseTask;
            Assert.False(Directory.Exists(sessionRoot));
            Assert.True(runner.CanceledActiveCount >= 1);
        });

    [Fact]
    public Task F1CloseDuringSelection_CancelsSelectionAndAwaitsTemporaryCleanup() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var selectionStarted = new TaskCompletionSource<CancellationToken>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            string? cleanedRoot = null;
            var window = fixture.CreateWindow(
                runner,
                async (_, cancellationToken) =>
                {
                    selectionStarted.TrySetResult(cancellationToken);
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return new ThumbnailSelectionResult(ThumbnailSelectionAction.Cancel, null);
                },
                deleteTemporaryDirectoryAsync: path =>
                {
                    cleanedRoot = path;
                    if (Directory.Exists(path))
                    {
                        Directory.Delete(path, recursive: true);
                    }
                    return Task.FromResult<Exception?>(null);
                });
            window.Show();
            Click(window.GenerateThumbnailButton);
            var selectionToken = await selectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Click(window.CancelButton);
            await window.CloseTask;

            Assert.True(selectionToken.IsCancellationRequested);
            Assert.NotNull(cleanedRoot);
            Assert.False(Directory.Exists(cleanedRoot));
        });

    [Fact]
    public Task TemporaryCleanupFailure_WarnsWithResidualAbsolutePath() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var messages = new List<ThumbnailMessage>();
            string? residualRoot = null;
            var window = fixture.CreateWindow(
                runner,
                (_, _) => Task.FromResult(new ThumbnailSelectionResult(
                    ThumbnailSelectionAction.Cancel,
                    null)),
                deleteTemporaryDirectoryAsync: path =>
                {
                    residualRoot = path;
                    return Task.FromResult<Exception?>(new IOException("temporary cleanup failed"));
                },
                messages: messages);
            window.Show();

            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;

            Assert.NotNull(residualRoot);
            Assert.True(Path.IsPathFullyQualified(residualRoot));
            Assert.Contains(messages, message =>
                message.Image == MessageBoxImage.Warning &&
                message.Message.Contains(residualRoot, StringComparison.Ordinal));
            Click(window.CancelButton);
            await window.CloseTask;
            Directory.Delete(residualRoot, recursive: true);
        });

    [Fact]
    public Task ExtractionAndTemporaryCleanupFailures_PreserveBothMessages() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.ExitFailure);
            var messages = new List<ThumbnailMessage>();
            string? residualRoot = null;
            var window = fixture.CreateWindow(
                runner,
                (_, _) => throw new InvalidOperationException("selection should not open"),
                deleteTemporaryDirectoryAsync: path =>
                {
                    residualRoot = path;
                    return Task.FromResult<Exception?>(new IOException("temporary cleanup failed"));
                },
                messages: messages);
            window.Show();

            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;

            var message = Assert.Single(messages);
            Assert.Contains("23", message.Message, StringComparison.Ordinal);
            Assert.Contains("temporary cleanup failed", message.Message, StringComparison.Ordinal);
            Assert.Contains(residualRoot!, message.Message, StringComparison.Ordinal);
            Click(window.CancelButton);
            await window.CloseTask;
            Directory.Delete(residualRoot!, recursive: true);
        });

    [Fact]
    public Task SaveAfterTwoGeneratedSelections_KeepsOnlyFinalTrackedCache() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) => Task.FromResult(new ThumbnailSelectionResult(
                    ThumbnailSelectionAction.Select,
                    candidates[0].FilePath)));
            string? firstCache = null;
            string? finalCache = null;

            var dialogResult = await ShowDialogAndRunAsync(window, async () =>
            {
                Click(window.GenerateThumbnailButton);
                await window.ThumbnailGenerationTask;
                firstCache = CurrentCachePath(window);
                await Task.Delay(5);
                Click(window.GenerateThumbnailButton);
                await window.ThumbnailGenerationTask;
                finalCache = CurrentCachePath(window);
                Assert.NotEqual(firstCache, finalCache);
                Click(window.SaveButton);
                await window.CloseTask;
            });

            Assert.True(dialogResult);
            Assert.False(File.Exists(firstCache));
            Assert.True(File.Exists(finalCache));
            Assert.Equal(
                Assert.IsType<MetadataEditorViewModel>(window.DataContext).ThumbnailPath,
                window.Result?.ThumbnailPath);
            File.Delete(finalCache!);
        });

    [Fact]
    public Task SaveAfterSwitchingToFileSelection_DeletesAllGeneratedCachesOnly() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) => Task.FromResult(new ThumbnailSelectionResult(
                    ThumbnailSelectionAction.Select,
                    candidates[0].FilePath)));
            string? generatedCache = null;
            string? otherCache = null;

            var dialogResult = await ShowDialogAndRunAsync(window, async () =>
            {
                Click(window.GenerateThumbnailButton);
                await window.ThumbnailGenerationTask;
                generatedCache = CurrentCachePath(window);
                var viewModel = Assert.IsType<MetadataEditorViewModel>(window.DataContext);
                Assert.True(await viewModel.UpdateThumbnailFromFileAsync(fixture.OtherImagePath));
                otherCache = CurrentCachePath(window);
                Click(window.SaveButton);
                await window.CloseTask;
            });

            Assert.True(dialogResult);
            Assert.False(File.Exists(generatedCache));
            Assert.True(File.Exists(otherCache));
            File.Delete(otherCache!);
        });

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("close")]
    public Task CancelEscapeOrClose_DeletesEveryGeneratedCacheOnly(string action) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) => Task.FromResult(new ThumbnailSelectionResult(
                    ThumbnailSelectionAction.Select,
                    candidates[0].FilePath)));
            window.Show();
            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;
            var generatedCache = CurrentCachePath(window);

            TriggerClose(window, action);
            await window.CloseTask;

            Assert.False(File.Exists(generatedCache));
            Assert.Null(window.Result);
            Assert.True(File.Exists(fixture.OtherImagePath));
        });

    [Fact]
    public Task CancelAfterPreviewRealization_DeletesGeneratedCacheWithoutResidualWarning() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var messages = new List<ThumbnailMessage>();
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) =>
                {
                    File.Copy(fixture.OtherImagePath, candidates[0].FilePath, overwrite: true);
                    return Task.FromResult(new ThumbnailSelectionResult(
                        ThumbnailSelectionAction.Select,
                        candidates[0].FilePath));
                },
                messages: messages);
            window.Show();
            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;
            var generatedCache = CurrentCachePath(window);

            var preview = FindVisualChild<Image>(window);
            Assert.NotNull(preview);
            window.UpdateLayout();
            _ = preview.Source?.Width;
            await window.Dispatcher.InvokeAsync(
                () => { },
                DispatcherPriority.ApplicationIdle);

            Click(window.CancelButton);
            await window.CloseTask;

            Assert.False(File.Exists(generatedCache));
            Assert.DoesNotContain(messages, message =>
                message.Title.Equals("정리 경고", StringComparison.Ordinal));
        });

    [Fact]
    public Task GeneratedCacheCleanupFailure_WarnsWithResidualPathButStillSaves() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var messages = new List<ThumbnailMessage>();
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) => Task.FromResult(new ThumbnailSelectionResult(
                    ThumbnailSelectionAction.Select,
                    candidates[0].FilePath)),
                deleteGeneratedCacheAsync: _ =>
                    Task.FromResult<Exception?>(new IOException("cache cleanup failed")),
                messages: messages);
            string? residualCache = null;

            var dialogResult = await ShowDialogAndRunAsync(window, async () =>
            {
                Click(window.GenerateThumbnailButton);
                await window.ThumbnailGenerationTask;
                residualCache = CurrentCachePath(window);
                Assert.IsType<MetadataEditorViewModel>(window.DataContext).ResetThumbnail();
                Click(window.SaveButton);
                await window.CloseTask;
            });

            Assert.True(dialogResult);
            Assert.NotNull(window.Result);
            Assert.True(File.Exists(residualCache));
            Assert.Contains(messages, message =>
                message.Message.Contains(residualCache!, StringComparison.Ordinal));
            File.Delete(residualCache!);
        });

    [Fact]
    public Task GeneratedCacheCleanupFailure_WarnsWithResidualPathButStillCancels() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new MetadataEditorFixture();
            var runner = new ThumbnailMediaProcessRunner(ThumbnailRunnerBehavior.Success);
            var messages = new List<ThumbnailMessage>();
            var window = fixture.CreateWindow(
                runner,
                (candidates, _) => Task.FromResult(new ThumbnailSelectionResult(
                    ThumbnailSelectionAction.Select,
                    candidates[0].FilePath)),
                deleteGeneratedCacheAsync: _ =>
                    Task.FromResult<Exception?>(new IOException("cache cleanup failed")),
                messages: messages);
            window.Show();
            Click(window.GenerateThumbnailButton);
            await window.ThumbnailGenerationTask;
            var residualCache = CurrentCachePath(window);

            Click(window.CancelButton);
            await window.CloseTask;

            Assert.Null(window.Result);
            Assert.True(File.Exists(residualCache));
            Assert.Contains(messages, message =>
                message.Message.Contains(residualCache, StringComparison.Ordinal));
            File.Delete(residualCache);
        });

    private static async Task<bool?> ShowDialogAndRunAsync(
        MetadataEditorWindow window,
        Func<Task> action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _ = window.Dispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                await action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
                window.Close();
            }
        }));

        var result = window.ShowDialog();
        await completion.Task;
        return result;
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void TriggerClose(MetadataEditorWindow window, string action)
    {
        switch (action)
        {
            case "cancel":
                Click(window.CancelButton);
                break;
            case "escape":
                RaiseKey(window, Key.Escape);
                break;
            case "close":
                window.Close();
                break;
        }
    }

    private static void RaiseKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element)
            ?? throw new InvalidOperationException("The test window has no presentation source.");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        });
    }

    private static string CurrentCachePath(MetadataEditorWindow window) =>
        Path.GetFullPath(LibraryPathHelper.ResolveToAbsolute(
            Assert.IsType<MetadataEditorViewModel>(window.DataContext).ThumbnailPath));

    private static string SessionRoot(string candidatePath) =>
        Directory.GetParent(Path.GetDirectoryName(candidatePath)!)!.FullName;

    private static T? FindVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed record ThumbnailMessage(
        string Message,
        string Title,
        MessageBoxImage Image);

    private sealed class MetadataEditorFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Airi.Tests",
            Guid.NewGuid().ToString("N"));

        public MetadataEditorFixture(bool hasSource = true)
        {
            Directory.CreateDirectory(_root);
            SourcePath = Path.Combine(_root, "source.mp4");
            OtherImagePath = Path.Combine(_root, "other.jpg");
            File.Copy(
                Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg"),
                OtherImagePath);
            Item = new VideoItem
            {
                Title = "Sample",
                Description = string.Empty,
                Actors = Array.Empty<string>(),
                Tags = Array.Empty<string>()
            };
            if (hasSource)
            {
                File.WriteAllBytes(SourcePath, [1]);
                Item.UpdateFileState(
                    SourcePath,
                    1,
                    DateTime.UtcNow,
                    VideoPresenceState.Available,
                    DateTime.UtcNow);
            }
        }

        public VideoItem Item { get; }
        public string SourcePath { get; }
        public string OtherImagePath { get; }

        public MetadataEditorWindow CreateWindow(
            IMediaProcessRunner runner,
            Func<IReadOnlyList<VideoThumbnailCandidate>, CancellationToken,
                Task<ThumbnailSelectionResult>> selector,
            Func<string, Task<Exception?>>? deleteTemporaryDirectoryAsync = null,
            Func<string, Task<Exception?>>? deleteGeneratedCacheAsync = null,
            List<ThumbnailMessage>? messages = null) =>
            new(
                Item,
                new VideoThumbnailExtractor("C:\\bundle", runner),
                selector,
                deleteTemporaryDirectoryAsync,
                deleteGeneratedCacheAsync,
                (message, title, image) => messages?.Add(new ThumbnailMessage(message, title, image)));

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private sealed class FailNthFfmpegRunner : IMediaProcessRunner
    {
        private readonly IMediaProcessRunner _inner;
        private readonly int _failureOrdinal;
        private int _ffmpegCount;

        public FailNthFfmpegRunner(IMediaProcessRunner inner, int failureOrdinal)
        {
            _inner = inner;
            _failureOrdinal = failureOrdinal;
        }

        public Task<MediaProcessResult> RunAsync(
            MediaProcessRequest request,
            Func<Stream, CancellationToken, Task> readOutputAsync,
            CancellationToken cancellationToken)
        {
            if (Path.GetFileName(request.FileName).Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref _ffmpegCount) == _failureOrdinal)
            {
                return Task.FromResult(new MediaProcessResult(23, 0, false));
            }

            return _inner.RunAsync(request, readOutputAsync, cancellationToken);
        }
    }
}
