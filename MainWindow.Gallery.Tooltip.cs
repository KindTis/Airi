using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Airi.Controls;
using Airi.Views;

namespace Airi;

public partial class MainWindow
{
    private Point _tooltipPointer;
    private bool _tooltipLeft, _tooltipMovePending, _tooltipTracking;
    private GalleryTooltipLayout? _tooltipLayout;
    internal GalleryTooltipLayout ApplyGalleryTooltipLayout(Size viewport, double maximumWidth = double.PositiveInfinity)
    {
        var limitedHeight = viewport.Height <= 420;
        DetailTitle.MaxHeight = limitedHeight ? 26 : 52;
        DetailDescriptionText.MaxHeight = limitedHeight ? 18 : 54;
        DetailGenresText.MaxHeight = DetailTagsText.MaxHeight = limitedHeight ? 18 : 36;
        DetailRecycleReason.MaxHeight = limitedHeight ? 16 : 32;
        var sectionSpacing = new Thickness(0, limitedHeight ? 3 : 7, 0, 0);
        DetailFactsSection.Margin = DetailFile.Margin = sectionSpacing;
        DetailFactsSection.Padding = DetailFile.Padding = sectionSpacing;
        var cardWidth = GalleryHost.Metrics.CardWidth;
        var layout = GalleryTooltipLayout.Calculate(viewport, cardWidth, maximumWidth: maximumWidth);
        // Height limits can shrink the image and therefore the text width. Re-measure the bounded
        // summaries until their wrapping settles; no scrolling or scaled text is introduced.
        for (var pass = 0; pass < 8; pass++)
        {
            DetailReadOnly.Measure(new Size(layout.CoverWidth, double.PositiveInfinity));
            var next = GalleryTooltipLayout.Calculate(viewport, cardWidth, DetailReadOnly.DesiredSize.Height, maximumWidth);
            var settled = Math.Abs(next.CoverWidth - layout.CoverWidth) < .01;
            layout = next;
            if (settled) break;
        }
        if (layout == _tooltipLayout) return layout;
        _tooltipLayout = layout;
        DetailSurface.Width = layout.Width;
        DetailSurface.Height = layout.Height;
        DetailSurface.Padding = new Thickness(layout.Padding);
        DetailBody.Height = layout.CoverHeight + layout.Gap + layout.BodyHeight;
        DetailBodyGap.Height = new GridLength(layout.Gap);
        DetailCoverFrame.Width = layout.CoverWidth;
        DetailCoverFrame.Height = layout.CoverHeight;
        return layout;
    }

    internal Rect PlaceGalleryTooltip(Size viewport, Point pointer)
    {
        var preferred = GalleryTooltipLayout.Calculate(viewport, GalleryHost.Metrics.CardWidth);
        var available = GalleryTooltipLayout.Place(pointer, viewport, new Size(preferred.Width, preferred.Height), ref _tooltipLeft);
        var layout = ApplyGalleryTooltipLayout(viewport, available.Width);
        var placement = GalleryTooltipLayout.Place(pointer, viewport, new Size(layout.Width, layout.Height), ref _tooltipLeft);

        // Keep the transparent shadow inside the client so WPF does not nudge the visible panel away from its anchor.
        const double shadow = 40;
        DetailSurface.Margin = new Thickness(
            Math.Clamp(placement.Left, 0, shadow), Math.Clamp(placement.Top, 0, shadow),
            Math.Clamp(viewport.Width - placement.Right, 0, shadow), Math.Clamp(viewport.Height - placement.Bottom, 0, shadow));
        return placement;
    }

    private void OnGalleryPointerMove(object sender, MouseEventArgs e)
    {
        _tooltipPointer = e.GetPosition((IInputElement)Content);
        _tooltipMovePending = true;
    }
    private void StartGalleryPointerTracking()
    {
        if (_tooltipTracking) return;
        _tooltipTracking = true;
        CompositionTarget.Rendering += OnGalleryPointerRendering;
    }
    private void StopGalleryPointerTracking()
    {
        if (!_tooltipTracking) return;
        _tooltipTracking = false;
        _tooltipMovePending = false;
        CompositionTarget.Rendering -= OnGalleryPointerRendering;
    }
    private void OnGalleryPointerRendering(object? sender, EventArgs e)
    {
        if (!_tooltipMovePending || !GalleryDetail.IsOpen) return;
        _tooltipMovePending = false;
        PositionGalleryPopup();
    }
    private void OnGalleryPopupClosed(object? sender, EventArgs e) => StopGalleryPointerTracking();

    private GalleryMetadataEditorWindow? _galleryEditorWindow;
    private bool _closingGalleryEditor;
    private GalleryMetadataEditorWindow GalleryEditorWindow
    {
        get
        {
            if (_galleryEditorWindow is not null) return _galleryEditorWindow;
            var editor = new GalleryMetadataEditorWindow(ViewModel);
            editor.ChooseThumbnailRequested += OnChooseThumbnailClick;
            editor.GenerateThumbnailRequested += OnGenerateGalleryThumbnailClick;
            editor.ResetThumbnailRequested += OnResetGalleryThumbnailClick;
            editor.Closing += OnGalleryEditorClosing;
            TextCompositionManager.AddPreviewTextInputStartHandler(editor, (_, _) => _galleryImeComposing = true);
            TextCompositionManager.AddPreviewTextInputUpdateHandler(editor, (_, _) => _galleryImeComposing = true);
            TextCompositionManager.AddPreviewTextInputHandler(editor, (_, _) => _galleryImeComposing = false);
            editor.LostKeyboardFocus += (_, _) => _galleryImeComposing = false;
            return _galleryEditorWindow = editor;
        }
    }
    internal GalleryMetadataEditorWindow GalleryEditorWindowForTests => GalleryEditorWindow;
    // Existing draft/thumbnail operations keep their controls and bindings after the move.
    private StackPanel EditorPanel => GalleryEditorWindow.EditorPanel;
    private TextBlock EditorTitle => GalleryEditorWindow.EditorTitle;
    private StackPanel EditorFields => GalleryEditorWindow.EditorFields;
    private StackPanel ThumbnailTools => GalleryEditorWindow.ThumbnailTools;
    private ItemsControl ChangesList => GalleryEditorWindow.ChangesList;
    private Button EditorPreview => GalleryEditorWindow.EditorPreview;
    private Button EditorCancel => GalleryEditorWindow.EditorCancel;
    private Button EditorApply => GalleryEditorWindow.EditorApply;
    private Button EditorBack => GalleryEditorWindow.EditorBack;
    private void OnGalleryEditorClosing(object? sender, CancelEventArgs e)
    {
        if (_closingGalleryEditor) return;
        e.Cancel = true;
        if (ViewModel.CancelEditCommand.CanExecute(null)) ViewModel.CancelEditCommand.Execute(null);
        if (!ViewModel.IsEditing) _galleryEditorWindow?.Hide();
    }
    private void CloseGalleryEditorWindow()
    {
        if (_galleryEditorWindow is null) return;
        _closingGalleryEditor = true;
        try { _galleryEditorWindow.Close(); }
        finally { _galleryEditorWindow = null; _closingGalleryEditor = false; }
    }

    private bool HandleGalleryPlaybackKey(Key key, ModifierKeys modifiers, bool repeat, DependencyObject? focused)
    {
        if (key != Key.Enter || modifiers != ModifierKeys.None || repeat || IsGallerySelectionMode || ViewModel.IsEditing ||
            focused is TextBoxBase or PasswordBox || (!IsVideoListFocus(focused) && !VideoList.IsMouseOver) || ViewModel.SelectedVideo is not { } selected) return false;
        TryPlayVideo(selected);
        return true;
    }
}
