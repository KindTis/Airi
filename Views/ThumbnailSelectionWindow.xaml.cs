using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Airi.Services;

namespace Airi.Views;

internal enum ThumbnailSelectionAction
{
    Cancel,
    Select,
    Regenerate
}

internal readonly record struct ThumbnailSelectionResult(
    ThumbnailSelectionAction Action,
    string? FilePath);

public partial class ThumbnailSelectionWindow : Window
{
    private TaskCompletionSource<ThumbnailSelectionResult>? _completion;
    private CancellationTokenRegistration _cancellationRegistration;
    private ThumbnailSelectionResult _result =
        new(ThumbnailSelectionAction.Cancel, null);

    internal ThumbnailSelectionWindow(
        IReadOnlyList<VideoThumbnailCandidate> candidates)
        : this(ValidateCandidates(candidates))
    {
    }

    private ThumbnailSelectionWindow(VideoThumbnailCandidate[] candidates)
    {
        InitializeComponent();
        CandidateList.ItemsSource = candidates
            .OrderBy(candidate => candidate.Timestamp)
            .Select(candidate => new ThumbnailCandidateDisplay(
                candidate.FilePath,
                LoadImage(candidate.FilePath)))
            .ToArray();
        CandidateList.ItemContainerGenerator.StatusChanged += OnContainerStatusChanged;
    }

    private static VideoThumbnailCandidate[] ValidateCandidates(
        IReadOnlyList<VideoThumbnailCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count != 5)
        {
            throw new ArgumentException("Exactly five thumbnail candidates are required.", nameof(candidates));
        }

        return candidates.ToArray();
    }

    internal Task<ThumbnailSelectionResult> SelectAsync(CancellationToken cancellationToken)
    {
        if (_completion is not null)
        {
            throw new InvalidOperationException("Thumbnail selection has already started.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        _completion = new TaskCompletionSource<ThumbnailSelectionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _cancellationRegistration = cancellationToken.Register(() =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (IsVisible)
                {
                    Close();
                }
            })));
        Show();
        return _completion.Task;
    }

    private static BitmapSource LoadImage(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        if (image.CanFreeze)
        {
            image.Freeze();
        }
        return image;
    }

    private void OnContainerStatusChanged(object? sender, EventArgs e)
    {
        if (CandidateList.ItemContainerGenerator.Status == GeneratorStatus.ContainersGenerated)
        {
            UpdateAccessibilityNames();
        }
    }

    private void OnCandidateSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfirmButton.IsEnabled = CandidateList.SelectedItem is not null;
        UpdateAccessibilityNames();
    }

    private void UpdateAccessibilityNames()
    {
        for (var index = 0; index < CandidateList.Items.Count; index++)
        {
            if (CandidateList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item)
            {
                var selected = item.IsSelected ? ", 선택됨" : string.Empty;
                AutomationProperties.SetName(
                    item,
                    $"썸네일 후보 {index + 1}/{CandidateList.Items.Count}{selected}");
            }
        }
    }

    private void OnCandidatePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Space or Key.Enter))
        {
            return;
        }

        if (ItemsControl.ContainerFromElement(
                CandidateList,
                Keyboard.FocusedElement as DependencyObject) is ListBoxItem item)
        {
            CandidateList.SelectedItem = item.DataContext;
            e.Handled = true;
        }
    }

    private void OnRegenerateClick(object sender, RoutedEventArgs e)
    {
        _result = new ThumbnailSelectionResult(ThumbnailSelectionAction.Regenerate, null);
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not ThumbnailCandidateDisplay selected)
        {
            return;
        }

        _result = new ThumbnailSelectionResult(ThumbnailSelectionAction.Select, selected.FilePath);
        Close();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cancellationRegistration.Dispose();
        _completion?.TrySetResult(_result);
        base.OnClosed(e);
    }

    private sealed record ThumbnailCandidateDisplay(string FilePath, BitmapSource Image);
}
