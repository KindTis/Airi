using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Airi.Infrastructure;
using Airi.Services;
using Airi.Services.VideoPreview;
using Airi.ViewModels;
using Airi.Web;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class MainWindowThumbnailRealizationTests
{
    [Fact]
    public void CalculateThumbnailDecodeWidth_UsesActualWidthAndDpiThenClamps64To520()
    {
        WpfTestHost.Run(() =>
        {
            Assert.Equal(64, MeasureWidth(1));
            Assert.Equal(234, MeasureWidth(234));
            Assert.Equal(520, MeasureWidth(600));
        });
    }

    [Fact]
    public void CalculateTooltipDecodeWidth_Uses480DipAndDpiThenClampsTo960()
    {
        WpfTestHost.Run(() =>
        {
            Assert.Equal(480, MainWindow.CalculateTooltipDecodeWidth(1d));
            Assert.Equal(720, MainWindow.CalculateTooltipDecodeWidth(1.5d));
            Assert.Equal(960, MainWindow.CalculateTooltipDecodeWidth(3d));
        });
    }

    [Fact]
    public void TooltipPreviewState_DelayIndicatorStaysVisibleUntilFirstFrameAndNeverRestarts()
    {
        WpfTestHost.Run(() =>
        {
            var state = new TooltipPreviewState(Guid.NewGuid(), null, true);
            var changedProperties = new List<string>();
            state.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName is { } propertyName)
                {
                    changedProperties.Add(propertyName);
                }
            };

            Assert.True(state.IsDelayIndicatorVisible);

            state.SetPhase(TooltipPreviewPhase.Preparing);
            Assert.True(state.IsDelayIndicatorVisible);

            state.SetPhase(TooltipPreviewPhase.Playing);
            Assert.True(state.IsDelayIndicatorVisible);

            state.ShowFrame(
                1,
                1,
                new VideoPreviewFrame(new byte[4], TimeSpan.Zero));

            Assert.False(state.IsDelayIndicatorVisible);
            Assert.Contains(
                nameof(TooltipPreviewState.IsDelayIndicatorVisible),
                changedProperties);

            state.SetPhase(TooltipPreviewPhase.Cover);
            state.SetPhase(TooltipPreviewPhase.Preparing);

            Assert.False(state.IsDelayIndicatorVisible);
        });
    }

    [Theory]
    [InlineData(TooltipPreviewPhase.Cover)]
    [InlineData(TooltipPreviewPhase.Closed)]
    public void TooltipPreviewState_TerminalPhaseHidesDelayIndicator(
        TooltipPreviewPhase phase)
    {
        WpfTestHost.Run(() =>
        {
            var state = new TooltipPreviewState(Guid.NewGuid(), null, true);

            state.SetPhase(phase);

            Assert.False(state.IsDelayIndicatorVisible);
        });
    }

    [Fact]
    public void TooltipPreviewState_InitiallyHiddenIndicatorDoesNotBecomeVisible()
    {
        WpfTestHost.Run(() =>
        {
            var state = new TooltipPreviewState(Guid.NewGuid(), null, false);

            state.SetPhase(TooltipPreviewPhase.Preparing);
            state.SetPhase(TooltipPreviewPhase.Playing);

            Assert.False(state.IsDelayIndicatorVisible);
        });
    }

    [Fact]
    public Task DeletionStop_ReusedContainerPreservesNewGalleryAndStopsCapturedPreview() => RunWithPreview(async fixture =>
    {
        fixture.MakeAvailable(fixture.A); var container = await fixture.RealizeAsync(fixture.A);
        fixture.Window.OpenGalleryForTests(fixture.A, container);
        await fixture.PreviewService!.WaitUntilCalledAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var oldToken = fixture.PreviewService.PrepareToken; var oldState = fixture.Window.GalleryPreviewStateForTests!;
        container.DataContext = fixture.B; fixture.Window.OpenGalleryForTests(fixture.B, container);
        await WaitUntilAsync(() => fixture.Window.GalleryPreviewStateForTests?.SessionId != oldState.SessionId, TimeSpan.FromSeconds(2));
        var newState = fixture.Window.GalleryPreviewStateForTests;
        var method = typeof(MainWindow).GetMethod("StopGalleryOperationPreviewAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await ((Task)method.Invoke(fixture.Window, new object[] { fixture.A })!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Window.GalleryPopupIsOpenForTests); Assert.Same(fixture.B, fixture.ViewModel.InspectedVideo); Assert.Same(newState, fixture.Window.GalleryPreviewStateForTests); Assert.True(oldToken.IsCancellationRequested);
        await fixture.Window.CloseGalleryForTestsAsync();
    }, clientAreaAnimationsEnabledOverride: true);
    [Fact]
    public Task TooltipPreview_AnimationsDisabledDoesNotCallInjectedService() => RunWithPreview(async fixture =>
    {
        await fixture.PreviewController!.RunAsync(
            new TooltipPreviewSession(
                Guid.NewGuid(),
                new VideoPreviewRequest(fixture.A.SourcePath, 480, 350, 15, TimeSpan.FromSeconds(10)),
                false,
                true,
                new AlwaysCurrentPreviewSink()),
            CancellationToken.None);

        Assert.Equal(0, fixture.PreviewService!.PrepareCallCount);
    });

    [Fact]
    public Task VideoItemMouseEnter_UsesActualItemSourcePath() =>
        RunWithPreview(async fixture =>
        {
            fixture.MakeAvailable(fixture.A);
            var container = await fixture.RealizeAsync(fixture.A);

            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });

            await fixture.PreviewService!.WaitUntilCalledAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(fixture.A.SourcePath, fixture.PreviewService.Request!.SourcePath);
            await WaitUntilAsync(() => fixture.Window.GalleryPreviewStateForTests is not null, TimeSpan.FromSeconds(2));
            var state = Assert.IsType<TooltipPreviewState>(fixture.Window.GalleryPreviewStateForTests);
            Assert.True(state.IsDelayIndicatorVisible);
            await fixture.Window.CloseGalleryForTestsAsync();
        }, clientAreaAnimationsEnabledOverride: true);

    [Fact]
    public Task VideoItemMouseEnter_AnimationsDisabledStartsWithIndicatorHidden() =>
        RunWithPreview(async fixture =>
        {
            fixture.MakeAvailable(fixture.A);
            var container = await fixture.RealizeAsync(fixture.A);

            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });

            await WaitUntilAsync(() => fixture.Window.GalleryPreviewStateForTests is not null, TimeSpan.FromSeconds(2));
            var state = Assert.IsType<TooltipPreviewState>(fixture.Window.GalleryPreviewStateForTests);
            Assert.False(state.IsDelayIndicatorVisible);
            Assert.Equal(0, fixture.PreviewService!.PrepareCallCount);
            await fixture.Window.CloseGalleryForTestsAsync();
        }, clientAreaAnimationsEnabledOverride: false);

    [Fact]
    public Task VideoItemMouseEnter_IneligibleVideoStartsWithIndicatorHidden() =>
        RunWithPreview(async fixture =>
        {
            var container = await fixture.RealizeAsync(fixture.A);

            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });

            await WaitUntilAsync(() => fixture.Window.GalleryPreviewStateForTests is not null, TimeSpan.FromSeconds(2));
            var state = Assert.IsType<TooltipPreviewState>(fixture.Window.GalleryPreviewStateForTests);
            Assert.False(state.IsDelayIndicatorVisible);
            Assert.Equal(0, fixture.PreviewService!.PrepareCallCount);
            await fixture.Window.CloseGalleryForTestsAsync();
        }, clientAreaAnimationsEnabledOverride: true);

    [Fact]
    public Task VideoItemMouseEnter_WithoutControllerStartsWithIndicatorHidden() =>
        Run(async fixture =>
        {
            fixture.MakeAvailable(fixture.A);
            var container = await fixture.RealizeAsync(fixture.A);

            container.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = Mouse.MouseEnterEvent,
                Source = container
            });

            await WaitUntilAsync(() => fixture.Window.GalleryPreviewStateForTests is not null, TimeSpan.FromSeconds(2));
            var state = Assert.IsType<TooltipPreviewState>(fixture.Window.GalleryPreviewStateForTests);
            Assert.False(state.IsDelayIndicatorVisible);
            await fixture.Window.CloseGalleryForTestsAsync();
        });

    [Fact]
    public Task VideoListDoubleClick_OnlyPlaysClickedVideoItem() => Run(async fixture =>
    {
        fixture.A.Title = "selected";
        fixture.B.Title = "clicked";
        await fixture.RealizeAsync(fixture.A);
        fixture.ViewModel.Videos.Add(fixture.B);
        fixture.Window.UpdateLayout();
        WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        var list = fixture.Window.GetVideoListForTests();
        await WaitUntilAsync(
            () => list.ItemContainerGenerator.ContainerFromItem(fixture.B) is ListBoxItem,
            TimeSpan.FromSeconds(5));
        var clickedContainer = Assert.IsType<ListBoxItem>(
            list.ItemContainerGenerator.ContainerFromItem(fixture.B));
        list.SelectedItem = fixture.A;
        var messages = new List<string>();
        AppLogger.TestObserver = (_, message, _) => messages.Add(message);

        try
        {
            var itemArgs = RaiseDoubleClick(list, clickedContainer);

            Assert.True(itemArgs.Handled);
            Assert.Contains($"Skipping playback for {fixture.B.Title}; no source path set.", messages);
            Assert.DoesNotContain($"Skipping playback for {fixture.A.Title}; no source path set.", messages);

            messages.Clear();
            var scrollBar = Assert.Single(
                EnumerateVisualDescendants<ScrollBar>(list),
                value => value.Orientation == Orientation.Vertical);
            var scrollBarArgs = RaiseDoubleClick(list, scrollBar);

            Assert.False(scrollBarArgs.Handled);
            Assert.Empty(messages);
        }
        finally
        {
            AppLogger.TestObserver = null;
        }
    });

    [Fact]
    public Task WindowClose_AwaitsPreviewShutdownThenCloses() => RunWithPreview(async fixture =>
    {
        var preview = fixture.PreviewController!.RunAsync(
            new TooltipPreviewSession(
                Guid.NewGuid(),
                new VideoPreviewRequest(fixture.A.SourcePath, 480, 350, 15, TimeSpan.FromSeconds(10)),
                true,
                true,
                new AlwaysCurrentPreviewSink()),
            CancellationToken.None);
        await fixture.PreviewService!.WaitUntilCalledAsync();

        fixture.Window.Close();
        WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        await preview;
        WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);

        Assert.True(fixture.PreviewService.PrepareToken.IsCancellationRequested);
        Assert.DoesNotContain(fixture.Window, Application.Current.Windows.Cast<Window>());
    });

    [Fact]
    public Task GalleryPopup_SeparatesCoverAndPreviewSourcesWhileOpen() => Run(async fixture =>
    {
        var container = await fixture.RealizeAsync(fixture.A);
        fixture.Window.OpenGalleryForTests(fixture.A, container);
        await WaitUntilAsync(() => fixture.Window.GalleryPreviewStateForTests is not null, TimeSpan.FromSeconds(2));
        var state = fixture.Window.GalleryPreviewStateForTests!;
        var initialSource = fixture.A.ThumbnailSource;
        var upgradedSource = CreateBitmap(29);
        var root = fixture.Window.GalleryPopupRootForTests;
        var indicator = Assert.Single(EnumerateVisualDescendants<ProgressBar>(root));
        Assert.Equal(4d, indicator.Height);
        Assert.Equal(Color.FromRgb(0xD5, 0xB6, 0x7C), Assert.IsType<SolidColorBrush>(indicator.Foreground).Color);
        state.UpdateCover(upgradedSource);
        state.ShowFrame(8, 8, new VideoPreviewFrame(new byte[8 * 8 * 4], TimeSpan.Zero));
        WpfTestHost.DrainDispatcher(root.Dispatcher);
        var images = EnumerateVisualDescendants<Image>(root).ToArray();
        var previewImage = Assert.Single(images.Where(image => ReferenceEquals(image.Source, state.PreviewSource) && image.Visibility == Visibility.Visible));
        Assert.Equal(Stretch.Uniform, previewImage.Stretch);
        Assert.Contains(images, image => ReferenceEquals(image.Source, upgradedSource));
        Assert.Same(initialSource, fixture.A.ThumbnailSource);
        state.SetPhase(TooltipPreviewPhase.Cover); WpfTestHost.DrainDispatcher(root.Dispatcher);
        Assert.Contains(images, image => image.Source is null && image.Visibility == Visibility.Collapsed);
        await fixture.Window.CloseGalleryForTestsAsync();
    });
    [Fact]
    public Task Loaded_WhenOldRegistrationDiffers_ReleasesOldBeforeRequestingCurrent() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        image.DataContext = fixture.B;
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        Assert.Equal(new[] { fixture.A.ThumbnailPath, fixture.B.ThumbnailPath }, fixture.Loader.Paths);
        Assert.Null(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Outcome);
    });

    [Fact]
    public Task DataContextChanged_WhenLoaded_ReleasesRegistrationThenRequestsNewValue() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(image, fixture.B, isLoaded: true);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).HasInFlight);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Outcome);
    });

    [Fact]
    public Task Unloaded_UsesRegistrationInsteadOfCurrentDataContext() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        image.DataContext = fixture.B;
        fixture.Window.UnregisterThumbnailImageForTests(image);
        Assert.Null(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Exists);
    });

    [Fact]
    public Task DataContextChangedThenUnloaded_DoesNotReleaseNewItemTwice() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(image, fixture.B, isLoaded: true);
        fixture.Window.UnregisterThumbnailImageForTests(image);
        var generation = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Generation;
        fixture.Window.UnregisterThumbnailImageForTests(image);
        Assert.Equal(generation, fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Generation);
    });

    [Fact]
    public Task UnloadedThenDataContextChanged_DoesNotReleaseNewItem() => Run(async fixture =>
    {
        var image = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(image);
        fixture.Window.UnregisterThumbnailImageForTests(image);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(image, fixture.B, isLoaded: false);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Exists);
        Assert.Single(fixture.Loader.Paths);
    });

    [Fact]
    public Task Close_ReleasesEveryRegistrationAndLeavesDictionaryEmpty() => Run(async fixture =>
    {
        var first = fixture.CreateImage(fixture.A);
        var second = fixture.CreateImage(fixture.B);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(first);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(second);
        fixture.Window.Close();
        Assert.Equal(0, fixture.Window.GetThumbnailRegistrationCountForTests());
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Exists);
        Assert.False(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.B).Exists);
    });

    [Fact]
    public Task RequestWithoutImageRegistration_RecordsOutsideRealizationWindow() => Run(async fixture =>
    {
        await fixture.ViewModel.RequestThumbnailAsync(fixture.A, 234);
        Assert.False(fixture.Probe.GetRequestRecords().Single().InRealizationWindow);
    });

    [Fact]
    public Task LoadedRegistrationBeforeRequest_RecordsInsideRealizationWindow() => Run(async fixture =>
    {
        await fixture.Window.RegisterThumbnailImageForTestsAsync(fixture.CreateImage(fixture.A));
        Assert.True(fixture.Probe.GetRequestRecords().Single().InRealizationWindow);
    });

    [Fact]
    public Task RecyclingEventOrders_LeaveOldRegistrationAndKeepOnlyNewItemActive() => Run(async fixture =>
    {
        var firstOrder = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(firstOrder);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(firstOrder, fixture.B, isLoaded: true);
        fixture.Window.UnregisterThumbnailImageForTests(firstOrder);

        var secondOrder = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(secondOrder);
        fixture.Window.UnregisterThumbnailImageForTests(secondOrder);
        await fixture.Window.ChangeThumbnailImageDataContextForTestsAsync(secondOrder, fixture.B, isLoaded: false);

        Assert.Equal(0, fixture.Window.GetThumbnailRegistrationCountForTests());
        Assert.Equal(0, fixture.Probe.GetActiveRegistrationCount());
    });

    [Fact]
    public Task SameItemTwoImages_FirstUnloadKeepsThumbnailActive() => Run(async fixture =>
    {
        var first = fixture.CreateImage(fixture.A);
        var second = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(first);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(second);
        fixture.Window.UnregisterThumbnailImageForTests(first);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.Equal(1, fixture.Window.GetThumbnailRegistrationCountForTests());
    });

    [Fact]
    public Task SameItemTwoImages_LastUnloadReleasesExactlyOnce() => Run(async fixture =>
    {
        var first = fixture.CreateImage(fixture.A);
        var second = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(first);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(second);
        fixture.Window.UnregisterThumbnailImageForTests(first);
        fixture.Window.UnregisterThumbnailImageForTests(second);
        var generation = fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Generation;
        fixture.Window.UnregisterThumbnailImageForTests(second);
        Assert.Equal(generation, fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Generation);
        Assert.Null(fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
    });

    [Fact]
    public Task SameItem_NewRegistrationBeforeOldUnload_DoesNotDropTracking() => Run(async fixture =>
    {
        var oldImage = fixture.CreateImage(fixture.A);
        var newImage = fixture.CreateImage(fixture.A);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(oldImage);
        await fixture.Window.RegisterThumbnailImageForTestsAsync(newImage);
        fixture.Window.UnregisterThumbnailImageForTests(oldImage);
        Assert.Equal("Loaded", fixture.ViewModel.GetThumbnailRuntimeDiagnostics(fixture.A).Outcome);
        Assert.Single(fixture.Loader.Paths);
    });

    [Fact]
    public Task GalleryClose_RemovedTargetReturnsKeyboardFocusToNextId() => Run(async fixture =>
    {
        var anchor = await fixture.RealizeAsync(fixture.A);
        fixture.ViewModel.Videos.Add(fixture.B); await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel);
        fixture.Window.UpdateLayout(); WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        fixture.Window.OpenGalleryForTests(fixture.A, anchor);
        await WaitUntilAsync(() => fixture.Window.GalleryPopupIsOpenForTests, TimeSpan.FromSeconds(2));
        fixture.ViewModel.Videos.Remove(fixture.A); await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel);
        fixture.Window.CloseGalleryAndRestoreForTests(); await fixture.Window.GalleryFocusRestoreTask;
        var focused = Assert.IsType<ListBoxItem>(Keyboard.FocusedElement);
        Assert.Equal(fixture.B.Id, Assert.IsType<VideoItem>(focused.DataContext).Id);
        Assert.False(fixture.Window.GalleryPopupIsOpenForTests);
    });

    [Fact]
    public Task CompactGallery_SearchBringIntoViewReturnsHeaderFromBottom() => Run(async fixture =>
    {
        await fixture.RealizeAsync(fixture.A); SetClientSize(fixture.Window, 415, 880);
        fixture.ViewModel.Videos.Clear();
        for (var i = 0; i < 12; i++) fixture.ViewModel.Videos.Add(new VideoItem { Title = $"영상 {i:D2}", LibraryPath = $"compact-{i}.mp4" });
        await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel);
        var host = fixture.Window.GalleryHostForTests; var search = fixture.Window.SearchTextBox;
        fixture.Window.UpdateLayout(); host.ScrollToIndex(11); fixture.Window.UpdateLayout(); WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        Assert.True(host.VerticalOffset > 0); Assert.True(search.TranslatePoint(new Point(), host).Y < 0);
        search.Focus(); search.BringIntoView(); fixture.Window.UpdateLayout(); WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        Assert.Equal(0, host.VerticalOffset);
        var bounds = search.TransformToAncestor(host).TransformBounds(new Rect(search.RenderSize));
        Assert.InRange(bounds.Top, 0, host.ViewportHeight); Assert.InRange(bounds.Bottom, 0, host.ViewportHeight);
        Assert.Same(search, Keyboard.FocusedElement);
    });

    [Theory]
    [InlineData(415, 880)]
    [InlineData(1280, 720)]
    public Task ProductionGalleryTenThousand_RealizesExpectedTopMiddleEnd(double width, double height) => Run(async fixture =>
    {
        await fixture.RealizeAsync(fixture.A); SetClientSize(fixture.Window, width, height);
        fixture.ViewModel.Videos.Clear();
        for (var i = 0; i < 10000; i++) fixture.ViewModel.Videos.Add(new VideoItem { Title = $"영상 {i:D5}", LibraryPath = $"fixture-{i}.mp4", Actors = new[] { "배우" }, Studio = "제작사", Genres = new[] { "장르" } });
        await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel);
        fixture.Window.UpdateLayout(); WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
        var list = fixture.Window.GetVideoListForTests(); Assert.Equal(10000, list.Items.Count);
        Assert.Equal(width==415?185.5:338,fixture.Window.GalleryHostForTests.Metrics.CardWidth,1);
        foreach (var index in new[] { 0, 5000, 9999 })
        {
            fixture.Window.ScrollVideoToIndex(index); fixture.Window.UpdateLayout();
            await fixture.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            fixture.Window.UpdateLayout(); WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
            if (index == 0) { var columns = fixture.Window.GalleryHostForTests.Metrics.Columns; var first = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(0)); var lastInRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(columns - 1)); var nextRow = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromIndex(columns)); Assert.Equal(first.TranslatePoint(new Point(), list).Y, lastInRow.TranslatePoint(new Point(), list).Y, 1); Assert.True(nextRow.TranslatePoint(new Point(), list).Y > first.TranslatePoint(new Point(), list).Y + 1); }
            var count = Enumerable.Range(0, 10000).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
            Assert.InRange(count, 1, 70); Assert.True(list.ItemContainerGenerator.ContainerFromIndex(index) != null, $"expected {index}, realized {count}, metrics {fixture.Window.GalleryHostForTests.Metrics}, extent {fixture.Window.GetVideoScrollViewer()?.ExtentHeight}");
        }
        if(width==415)
        {
            var host=fixture.Window.GalleryHostForTests;var footer=host.Suffix!;var y=footer.TranslatePoint(new Point(),host).Y;
            Assert.InRange(y,0,host.ActualHeight-footer.ActualHeight+1);Assert.True(host.Header!.TranslatePoint(new Point(),host).Y<0);Assert.True(host.Prefix!.TranslatePoint(new Point(),host).Y<0);
        }    });

    [Fact]
    public Task GalleryReferenceFixture_HostsNativeContent() => WpfTestHost.RunAsync(async () =>
    {
        if (Environment.GetEnvironmentVariable("AIRI_GALLERY_CAPTURE") != "1") return;
        using var fixture = new Fixture(useStoredScanner: true);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var sheet = new BitmapImage(new Uri(Path.Combine(repo, "docs/jira/AIRI-2/2026-11-19-cover-gallery-wpf-reference/assets/cover-sheet.png")));
        var crops = new[] { new Int32Rect(235, 277, 423, 146), new Int32Rect(683, 277, 423, 146), new Int32Rect(1133, 276, 422, 147), new Int32Rect(235, 538, 423, 132), new Int32Rect(683, 538, 423, 132), new Int32Rect(1133, 538, 422, 132) };
        var titles = new[] { "여름의 기록", "밤의 산책", "파도의 시간", "오래된 정원", "우리의 계절", "먼 곳의 편지", "숲의 문장", "도시의 틈", "바다를 건너", "느린 아침", "마지막 플랫폼", "빛을 따라서" };
        var dates = new[] { "2025-08-14", "2024-11-06", "2025-05-22", "2023-04-02", "2024-03-18", "2022-09-16", "2025-10-08", "2024-07-23", "2025-02-10", "2023-12-01", "2024-09-11", "2025-01-19" };
        var descriptions = new[] { "낯선 바닷가에서 보낸 여름, 오래 기억하고 싶은 하루의 기록.", "익숙한 거리를 천천히 걷다 마주한, 평범해서 특별한 밤.", "서로 다른 속도로 살아온 두 사람이 바다 앞에서 잠시 멈춘다.", "오랜만에 돌아온 집. 잊고 지낸 기억들이 정원의 계절과 함께 되살아난다.", "계절이 바뀌는 동안 조금씩 달라지는 우리들의 이야기.", "기차의 창밖으로 흐르는 풍경과 끝내 보내지 못한 편지.", "숲을 걸으며 기록한 사계절의 작은 변화들.", "", "섬에서 섬으로, 바람과 물결을 따라가는 여정.", "아무 일도 일어나지 않아 더 오래 남는 아침.", "막차가 떠난 뒤, 낯선 편지 한 장이 플랫폼에 남겨진다.", "오래된 카메라를 들고 빛이 머무는 장소를 찾아간다." };
        var actors = new[] { new[] { "김서연", "이도윤" }, new[] { "김서연", "박지훈" }, new[] { "김서연", "이도윤" }, new[] { "김서연", "최유진" }, new[] { "김서연", "박지훈" }, new[] { "김서연", "최유진" }, new[] { "최유진" }, new[] { "박지훈" }, new[] { "이도윤" }, new[] { "정하늘" }, new[] { "박지훈", "정하늘" }, new[] { "최유진" } };
        string?[] studios = { "스튜디오 온", "노을필름", "스튜디오 온", null, "노을필름", "스튜디오 온", "달빛영화", "노을필름", "스튜디오 온", null, "달빛영화", "달빛영화" };
        var coverIndices = new[] { 0, 1, 2, 3, 4, 5, 3, 1, 2, 0, 5, 4 };
        var referenceLibrary = new Airi.Domain.LibraryData();
        for (var i = 0; i < 12; i++)
        {
            var key = $"reference-cover-{i}.png"; var crop = new CroppedBitmap(sheet, crops[coverIndices[i]]); crop.Freeze(); fixture.Loader.FixedSources[key] = crop;
            var item = new VideoItem { Title = titles[i], LibraryPath = $"fixture-{i}.mp4", ReleaseDate = DateOnly.Parse(dates[i]), Actors = actors[i], Studio = studios[i], Genres = i < 6 ? new[] { "드라마" } : i == 9 ? Array.Empty<string>() : i == 10 ? new[] { "스릴러" } : new[] { "다큐멘터리" }, ThumbnailPath = key, Tags = new[] { "여행", "일상" }, Description = descriptions[i] };
            var sourcePath = Path.Combine(fixture.Root, $"{titles[i]}.mp4");
            if(i!=10)File.WriteAllBytes(sourcePath, new byte[] { 0 });
            referenceLibrary.Videos.Add(new Airi.Domain.VideoEntry(sourcePath, new Airi.Domain.VideoMeta(item.Title, item.ReleaseDate, item.Actors, i == 11 ? string.Empty : key, item.Tags, item.Description) { Studio = item.Studio, Genres = item.Genres }, 3200000000, DateTime.UtcNow, DateTime.UtcNow) { Id = item.Id });
        }
        await fixture.Store.SaveAsync(referenceLibrary);
        WpfTestHost.Show(fixture.Window);
        await fixture.Window.InitializationTask;
        await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel);
        fixture.Window.Left = 40; fixture.Window.Top = 40; fixture.Window.ShowInTaskbar = true;
        fixture.ViewModel.AddFacetFilter(Airi.Domain.GalleryFacet.Actors, "김서연"); fixture.ViewModel.AddFacetFilter(Airi.Domain.GalleryFacet.Genres, "드라마");
        await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel); fixture.ViewModel.SelectedVideo = fixture.ViewModel.Videos.First(item => item.Title == "여름의 기록");
        var output = Path.Combine(repo, "TestResults/AIRI2/ui-native-spike"); Directory.CreateDirectory(output);
        var sizes = new[] { new Size(1280, 720), new Size(1280, 900), new Size(1024, 768), new Size(415, 880) };
        if (Environment.GetEnvironmentVariable("AIRI_CAPTURE_CLIENT") is { Length: > 0 } requested)
        {
            var parts = requested.Split('x');
            if (parts.Length == 2 && double.TryParse(parts[0], out var w) && double.TryParse(parts[1], out var h)) sizes = new[] { new Size(w, h) };
        }
        foreach (var size in sizes)
        {
            SetClientSize(fixture.Window, size.Width, size.Height); fixture.Window.GalleryHostForTests.ResetOffset(); fixture.Window.UpdateLayout();
            await fixture.Window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render); WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
            var captureState=Environment.GetEnvironmentVariable("AIRI_CAPTURE_STATE")??"gallery";
            var active=fixture.ViewModel.SelectedVideo!;
            if(captureState=="empty"){fixture.ViewModel.SearchQuery="__no_fixture_result__";await VideoDeletionFixture.WaitForQueryAsync(fixture.ViewModel);}
            if(captureState is "detail" or "edit" or "batch" or "preview")
            {
                var list=fixture.Window.GetVideoListForTests();var anchor=(ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(active);
                fixture.Window.OpenGalleryForTests(active,anchor);
                if(captureState=="batch"){foreach(var item in list.Items.Cast<VideoItem>().Take(2))item.IsChecked=true;fixture.ViewModel.EditCommand.Execute(null);await fixture.ViewModel.GalleryActionTask;}
                else if(captureState is "edit" or "preview"){fixture.ViewModel.EditCommand.Execute(null);await fixture.ViewModel.GalleryActionTask;}
                if(captureState=="preview"){fixture.ViewModel.EditorDraft!.Fields.First(field=>field.Field==Airi.Domain.MetadataField.Title).Value+=" 수정";fixture.ViewModel.PreviewChangesCommand.Execute(null);}
                fixture.Window.UpdateLayout();WpfTestHost.DrainDispatcher(fixture.Window.Dispatcher);
            }
            fixture.Window.Title = $"Airi — AIRI-2 fixture {size.Width}x{size.Height} {captureState}";
            void WriteReady()
            {
                var root = (FrameworkElement)fixture.Window.Content; var dpi = VisualTreeHelper.GetDpi(fixture.Window);
                var list = fixture.Window.GetVideoListForTests(); var first = list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem;
                var firstY = first?.TranslatePoint(new Point(), list).Y;
                var firstRow = Enumerable.Range(0, Math.Min(12, list.Items.Count)).Select(i => list.ItemContainerGenerator.ContainerFromIndex(i)).OfType<ListBoxItem>().Count(item => Math.Abs(item.TranslatePoint(new Point(), list).Y - (firstY ?? -10000)) < 1);
                var state = new { Utc = DateTime.UtcNow, ProcessId = Environment.ProcessId, fixture.Window.Title, fixture.Window.IsVisible, RequestedClient = size, Client = root.RenderSize, Window = new { fixture.Window.Width, fixture.Window.Height, fixture.Window.ActualWidth, fixture.Window.ActualHeight }, Dpi = new { dpi.DpiScaleX, dpi.DpiScaleY }, Metrics = fixture.Window.GalleryHostForTests.Metrics, FirstRowColumns = firstRow, ListSize = list.RenderSize, ListPadding = list.Padding, InnerViewport = fixture.Window.GetVideoScrollViewer()?.ViewportWidth, FirstContainer = first?.RenderSize, FirstDesired = first?.DesiredSize, fixture.ViewModel.ResultCount, fixture.ViewModel.IncompleteCount };
                File.WriteAllText(Path.Combine(output, "capture-ready.json"), System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(output, "capture-ready.txt"), $"{DateTime.UtcNow:O} {fixture.Window.Title} visible={fixture.Window.IsVisible} client={root.RenderSize} dpi={dpi.DpiScaleX},{dpi.DpiScaleY} columns={firstRow}");
            }
            fixture.Window.SizeChanged += (_, _) => WriteReady();
            fixture.Window.Activated += (_, _) => fixture.Window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() => { SetClientSize(fixture.Window, size.Width, size.Height); WriteReady(); }));
            WriteReady();
            if (Environment.GetEnvironmentVariable("AIRI_CAPTURE_STOP_FILE") is { Length: > 0 } stopFile)
            {
                var deadline = DateTime.UtcNow.AddMinutes(10);
                while (!File.Exists(stopFile) && DateTime.UtcNow < deadline) await Task.Delay(250);
                return; // Fixture hosting only; screenshot acceptance is performed externally.
            }
            if (int.TryParse(Environment.GetEnvironmentVariable("AIRI_CAPTURE_HOLD_MS"), out var hold) && hold > 0) await Task.Delay(Math.Min(hold, 60000));
            await Task.Delay(100);
            var root = (FrameworkElement)fixture.Window.Content; var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); Assert.True(pixels.Distinct().Count() > 20, "Native content render is blank.");
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, $"gallery-{size.Width}x{size.Height}.png")); encoder.Save(stream);
        }
    });
    private static void SetClientSize(Window window, double width, double height)
    {
        var root = (FrameworkElement)window.Content;
        for (var i = 0; i < 2; i++) { window.Width += width - root.ActualWidth; window.Height += height - root.ActualHeight; window.UpdateLayout(); WpfTestHost.DrainDispatcher(window.Dispatcher); window.UpdateLayout(); }
    }
    [Fact]
    public Task RealLauncherBoundary_DoubleClickInvokesOnceAndMissingTrashCannotPlay() => WpfTestHost.RunAsync(async () =>
    {
        var launcher = new CountingVideoLauncher(); using var fixture = new Fixture(videoLauncher: launcher);
        fixture.MakeAvailable(fixture.A); var container = await fixture.RealizeAsync(fixture.A); var list = fixture.Window.GetVideoListForTests();
        RaiseDoubleClick(list, container); Assert.Equal(1, launcher.PlayCount); Assert.Equal(fixture.A.SourcePath, launcher.LastPath);
        fixture.A.Presence = VideoPresenceState.Missing; RaiseDoubleClick(list, container); Assert.Equal(1, launcher.PlayCount);
        fixture.A.Presence = VideoPresenceState.Available; fixture.A.RecycleState = Airi.Domain.LibraryOperationState.Trashed; RaiseDoubleClick(list, container); Assert.Equal(1, launcher.PlayCount);
    });
    private static int MeasureWidth(double width)
    {
        return MainWindow.CalculateThumbnailDecodeWidth(width, 1d);
    }

    private static MouseButtonEventArgs RaiseDoubleClick(ListBox list, DependencyObject source)
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source = source
        };
        list.RaiseEvent(args);
        return args;
    }

    private static IEnumerable<T> EnumerateVisualDescendants<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in EnumerateVisualDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static ImageSource CreateBitmap(byte value)
    {
        var pixels = Enumerable.Repeat(value, 8 * 8 * 4).ToArray();
        var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Task Run(Func<Fixture, Task> test) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture();
        await test(fixture);
    });

    private static Task RunWithPreview(
        Func<Fixture, Task> test,
        bool? clientAreaAnimationsEnabledOverride = null) => WpfTestHost.RunAsync(async () =>
    {
        using var fixture = new Fixture(
            enablePreview: true,
            clientAreaAnimationsEnabledOverride: clientAreaAnimationsEnabledOverride);
        await test(fixture);
    });

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            WpfTestHost.DrainDispatcher(Application.Current.Dispatcher);
            await Task.Delay(20);
        }
        Assert.True(condition(), "WPF item was not realized before timeout.");
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;

        public Fixture(
            bool enablePreview = false,
            bool? clientAreaAnimationsEnabledOverride = null,
            bool useStoredScanner = false,
            IVideoLauncher? videoLauncher = null)
        {
            _root = Path.Combine(Path.GetTempPath(), "AiriMainWindowThumbnailTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            Loader = new RecordingLoader();
            Probe = ThumbnailPerformanceProbe.CreateEnabled();
            Probe.BeginMeasurementPhase(ThumbnailMeasurementPhase.Cold);
            var metadata = new WebMetadataService(
                Array.Empty<IWebVideoMetaSource>(),
                new ThumbnailCache(_root),
                NullTranslationService.Instance,
                "KO");
            Store = new LibraryStore(Path.Combine(_root, "videos.json"));
            ViewModel = new MainViewModel(
                Store,
                useStoredScanner ? new StoredFixtureScanner(Store) : new LibraryScanner(new FileSystemScanner()),
                metadata,
                Probe,
                Loader);
            if (enablePreview)
            {
                PreviewService = new BlockingPreviewService();
                PreviewController = new TooltipPreviewController(PreviewService, TimeProvider.System);
            }
            Window = new MainWindow(
                ViewModel,
                Probe,
                Loader,
                PreviewController,
                clientAreaAnimationsEnabledOverride,
                videoLauncher);
            A = CreateItem("a.jpg");
            B = CreateItem("b.jpg");
        }

        public string Root => _root;
        public LibraryStore Store { get; }
        public RecordingLoader Loader { get; }
        public ThumbnailPerformanceProbe Probe { get; }
        public MainViewModel ViewModel { get; }
        public MainWindow Window { get; }
        public BlockingPreviewService? PreviewService { get; }
        public TooltipPreviewController? PreviewController { get; }
        public VideoItem A { get; }
        public VideoItem B { get; }

        public Image CreateImage(VideoItem item)
        {
            var image = new Image { DataContext = item, Width = 234, Height = 160 };
            image.Measure(new Size(234, 160));
            image.Arrange(new Rect(0, 0, 234, 160));
            return image;
        }

        public void MakeAvailable(VideoItem item)
        {
            var sourcePath = Path.Combine(_root, "actual-item.mp4");
            File.WriteAllBytes(sourcePath, new byte[] { 0 });
            item.UpdateFileState(
                sourcePath,
                1,
                File.GetLastWriteTimeUtc(sourcePath),
                VideoPresenceState.Available,
                File.GetCreationTimeUtc(sourcePath));
        }

        public async Task<ListBoxItem> RealizeAsync(VideoItem item)
        {
            WpfTestHost.Show(Window);
            await WaitUntilAsync(() => Window.InitializationStarted, TimeSpan.FromSeconds(5));
            await Window.InitializationTask.WaitAsync(TimeSpan.FromSeconds(10));
            ViewModel.Videos.Clear();
            ViewModel.Videos.Add(item);
            Window.UpdateLayout();
            WpfTestHost.DrainDispatcher(Window.Dispatcher);
            var list = Window.GetVideoListForTests();
            await WaitUntilAsync(
                () => list.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem,
                TimeSpan.FromSeconds(5));
            return Assert.IsType<ListBoxItem>(
                list.ItemContainerGenerator.ContainerFromItem(item));
        }

        public void Dispose()
        {
            if (Application.Current.Windows.Cast<Window>().Contains(Window))
            {
                Window.Close();
                WpfTestHost.DrainDispatcher(Window.Dispatcher);
            }
            else
            {
                ViewModel.Dispose();
            }

            if (Probe.IsActive)
            {
                Probe.EndMeasurementPhase();
            }
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private VideoItem CreateItem(string path)
        {
            var item = new VideoItem
            {
                LibraryPath = $"./Videos/{path}.mp4",
                ThumbnailPath = path,
                ThumbnailUri = string.Empty
            };
            item.ReleaseThumbnail(Loader.FallbackSource);
            return item;
        }
    }

    private sealed class BlockingPreviewService : IVideoPreviewService
    {
        private readonly TaskCompletionSource _called =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IPreparedVideoPreview> _prepare =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PrepareCallCount { get; private set; }
        public CancellationToken PrepareToken { get; private set; }
        public VideoPreviewRequest? Request { get; private set; }

        public Task<IPreparedVideoPreview> PrepareAsync(
            VideoPreviewRequest request,
            CancellationToken cancellationToken)
        {
            PrepareCallCount++;
            PrepareToken = cancellationToken;
            Request = request;
            cancellationToken.Register(() => _prepare.TrySetCanceled(cancellationToken));
            _called.TrySetResult();
            return _prepare.Task;
        }

        public Task WaitUntilCalledAsync() => _called.Task;
    }

    private sealed class AlwaysCurrentPreviewSink : ITooltipPreviewSink
    {
        public bool IsCurrent(Guid sessionId) => true;

        public ValueTask<DispatcherCallbackResult> SetPhaseAsync(
            Guid sessionId,
            TooltipPreviewPhase phase,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new DispatcherCallbackResult(true, TimeProvider.System.GetTimestamp(), TimeSpan.Zero));

        public ValueTask<FramePresentationResult> ShowFrameAsync(
            Guid sessionId,
            int pixelWidth,
            int pixelHeight,
            VideoPreviewFrame frame,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new FramePresentationResult(true, TimeProvider.System.GetTimestamp(), TimeSpan.Zero));
    }

    private sealed class RecordingLoader : IThumbnailImageLoader
    {
        private bool _delayNextRequest;

        public RecordingLoader()
        {
            FallbackSource = CreateBitmap(0);
        }

        public ImageSource FallbackSource { get; }
        public List<string> Paths { get; } = new();
        public List<int> Widths { get; } = new();
        public List<CancellationToken> CancellationTokens { get; } = new();
        public List<ImageSource> Sources { get; } = new();
        public Dictionary<string, ImageSource> FixedSources { get; } = new();

        public void DelayNextRequest() => _delayNextRequest = true;

        public Task<ThumbnailImageResult> LoadAsync(string thumbnailPath, int decodePixelWidth, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(thumbnailPath);
            Widths.Add(decodePixelWidth);
            CancellationTokens.Add(cancellationToken);
            var source = FixedSources.TryGetValue(thumbnailPath, out var fixedSource) ? fixedSource : CreateBitmap((byte)Math.Clamp(decodePixelWidth % 255, 1, 254));
            Sources.Add(source);
            if (!_delayNextRequest)
            {
                return Task.FromResult(new ThumbnailImageResult(source, false));
            }

            _delayNextRequest = false;
            var completion = new TaskCompletionSource<ThumbnailImageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return completion.Task;
        }

        private static ImageSource CreateBitmap(byte value)
        {
            var pixels = Enumerable.Repeat(value, 8 * 8 * 4).ToArray();
            var bitmap = BitmapSource.Create(8, 8, 96, 96, PixelFormats.Bgra32, null, pixels, 8 * 4);
            bitmap.Freeze();
            return bitmap;
        }
    }

    private sealed class CountingVideoLauncher : IVideoLauncher
    {
        public int PlayCount { get; private set; }
        public string? LastPath { get; private set; }
        public VideoLaunchResult Play(string path) { PlayCount++; LastPath = path; return new(true); }
        public VideoLaunchResult ShowInFolder(string path) => new(true);
    }
    private sealed class StoredFixtureScanner(LibraryStore store) : ILibraryScanner
    {
        public async Task<LibraryScanResult> ScanAsync(Airi.Domain.LibraryData library, IReadOnlyList<Airi.Domain.TargetFolder> targets, CancellationToken token)
        {
            var stored = await store.LoadAsync(token);
            return new LibraryScanResult(stored.Videos.Where(entry=>File.Exists(entry.Path)).Select(entry => new FileSnapshot(entry.Path, entry.Path, entry.SizeBytes, entry.LastModifiedUtc, entry.CreatedUtc)).ToArray(), Array.Empty<FileSnapshot>(), stored.Videos.Where(entry=>!File.Exists(entry.Path)).ToArray(), Array.Empty<UpdatedFile>());
        }
    }
}
