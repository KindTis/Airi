using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Airi.Services;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class ThumbnailSelectionWindowTests
{
    [Fact]
    public Task Constructor_LoadsAllFiveImagesWithoutKeepingFileHandles() =>
        WpfTestHost.RunAsync(() =>
        {
            using var fixture = new CandidateFixture();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);

            foreach (var path in fixture.Paths)
            {
                File.Delete(path);
                Assert.False(File.Exists(path));
            }
            Assert.Equal(5, window.CandidateList.Items.Count);
            window.Close();

            return Task.CompletedTask;
        });

    [Theory]
    [InlineData(4)]
    [InlineData(6)]
    public Task Constructor_RejectsCandidateCountOtherThanFive(int count) =>
        WpfTestHost.RunAsync(() =>
        {
            using var fixture = new CandidateFixture();
            var candidates = count == 4
                ? fixture.Candidates.Take(4).ToArray()
                : fixture.Candidates
                    .Append(fixture.Candidates[^1] with { Timestamp = TimeSpan.FromSeconds(6) })
                    .ToArray();
            Assert.Throws<ArgumentException>(() =>
                new ThumbnailSelectionWindow(candidates));
            return Task.CompletedTask;
        });

    [Fact]
    public Task KeyboardSelection_EnablesConfirmAndReturnsSelectWithExactlyOnePath() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new CandidateFixture();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);
            var selection = window.SelectAsync(CancellationToken.None);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var candidate = GetCandidate(window, 2);

            Keyboard.Focus(candidate);
            RaiseKey(candidate, Key.Enter);

            Assert.Equal(2, window.CandidateList.SelectedIndex);
            Assert.True(window.ConfirmButton.IsEnabled);
            Click(window.ConfirmButton);

            Assert.Equal(
                new ThumbnailSelectionResult(ThumbnailSelectionAction.Select, fixture.Paths[2]),
                await selection);
        });

    [Fact]
    public Task RegenerateButton_ReturnsRegenerateWithoutSelectedPath() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new CandidateFixture();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);
            var selection = window.SelectAsync(CancellationToken.None);
            WpfTestHost.DrainDispatcher(window.Dispatcher);

            Click(window.RegenerateButton);

            Assert.Equal(
                new ThumbnailSelectionResult(ThumbnailSelectionAction.Regenerate, null),
                await selection);
        });

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("close")]
    [InlineData("cancellation")]
    public Task CancelEscapeCloseOrCancellation_ReturnCancel(string action) =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new CandidateFixture();
            using var cancellation = new CancellationTokenSource();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);
            var selection = window.SelectAsync(cancellation.Token);
            WpfTestHost.DrainDispatcher(window.Dispatcher);

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
                case "cancellation":
                    cancellation.Cancel();
                    WpfTestHost.DrainDispatcher(window.Dispatcher);
                    break;
            }

            var result = await selection;
            Assert.Equal(ThumbnailSelectionAction.Cancel, result.Action);
            Assert.Null(result.FilePath);
        });

    [Fact]
    public Task TabAfterLastCandidate_MovesToRegenerateButton() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new CandidateFixture();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);
            var selection = window.SelectAsync(CancellationToken.None);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var last = GetCandidate(window, 4);

            Keyboard.Focus(last);
            Assert.True(last.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next)));

            Assert.Same(window.RegenerateButton, Keyboard.FocusedElement);
            window.Close();
            await selection;
        });

    [Fact]
    public Task SelectedAndFocusedCandidate_UsesOnlySelectionBorderAndAccessibleState() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new CandidateFixture();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);
            var selection = window.SelectAsync(CancellationToken.None);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            var candidate = GetCandidate(window, 1);

            window.CandidateList.SelectedIndex = 1;
            Keyboard.Focus(candidate);
            WpfTestHost.DrainDispatcher(window.Dispatcher);
            candidate.ApplyTemplate();
            var focusBorder = Assert.IsType<Border>(candidate.Template.FindName("FocusBorder", candidate));
            var selectionBorder = Assert.IsType<Border>(candidate.Template.FindName("SelectionBorder", candidate));

            Assert.True(candidate.IsKeyboardFocusWithin);
            Assert.Equal(
                Colors.Transparent,
                Assert.IsType<SolidColorBrush>(focusBorder.BorderBrush).Color);
            Assert.Equal(
                Color.FromRgb(0x3B, 0x82, 0xF6),
                Assert.IsType<SolidColorBrush>(selectionBorder.BorderBrush).Color);
            Assert.Equal("썸네일 후보 2/5, 선택됨", AutomationProperties.GetName(candidate));
            window.Close();
            await selection;
        });

    [Fact]
    public Task SelectAsync_CanOnlyStartOnce() =>
        WpfTestHost.RunAsync(async () =>
        {
            using var fixture = new CandidateFixture();
            var window = new ThumbnailSelectionWindow(fixture.Candidates);
            var selection = window.SelectAsync(CancellationToken.None);

            Assert.Throws<InvalidOperationException>(() =>
            {
                _ = window.SelectAsync(CancellationToken.None);
            });

            window.Close();
            await selection;
        });

    private static ListBoxItem GetCandidate(ThumbnailSelectionWindow window, int index) =>
        Assert.IsType<ListBoxItem>(
            window.CandidateList.ItemContainerGenerator.ContainerFromIndex(index));

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void RaiseKey(UIElement element, Key key)
    {
        var source = PresentationSource.FromVisual(element)
            ?? throw new InvalidOperationException("The test window has no presentation source.");
        element.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
    }

    private sealed class CandidateFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "Airi.Tests",
            Guid.NewGuid().ToString("N"));

        public CandidateFixture()
        {
            Directory.CreateDirectory(_root);
            Paths = Enumerable.Range(1, 5)
                .Select(index => Path.Combine(_root, $"candidate-{index:D2}.jpg"))
                .ToArray();
            foreach (var path in Paths)
            {
                File.Copy(SourceImagePath, path);
            }
            Candidates = Paths
                .Select((path, index) => new VideoThumbnailCandidate(
                    TimeSpan.FromSeconds(index + 1),
                    path))
                .ToArray();
        }

        public string[] Paths { get; }
        public VideoThumbnailCandidate[] Candidates { get; }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static string SourceImagePath => Path.Combine(
            AppContext.BaseDirectory,
            "resources",
            "noimage.jpg");
    }
}
