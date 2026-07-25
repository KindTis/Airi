using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Airi.Services.VideoPreview;

namespace Airi.Infrastructure;

public sealed class TooltipPreviewState : INotifyPropertyChanged
{
    private ImageSource? _coverSource;
    private WriteableBitmap? _previewSource;
    private bool _isPreviewVisible;
    private bool _isDelayIndicatorVisible;
    private TooltipPreviewPhase _phase = TooltipPreviewPhase.Cover;

    public TooltipPreviewState(
        Guid sessionId,
        ImageSource? initialCover,
        bool isDelayIndicatorVisible = false)
    {
        SessionId = sessionId;
        _coverSource = initialCover;
        _isDelayIndicatorVisible = isDelayIndicatorVisible;
    }

    public Guid SessionId { get; }
    public ImageSource? CoverSource => _coverSource;
    public ImageSource? PreviewSource => _previewSource;
    public bool IsPreviewVisible => _isPreviewVisible;
    public bool IsDelayIndicatorVisible => _isDelayIndicatorVisible;
    public TooltipPreviewPhase Phase => _phase;
    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdateCover(ImageSource? source)
    {
        if (ReferenceEquals(_coverSource, source)) return;
        _coverSource = source;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverSource)));
    }

    public void ShowFrame(int width, int height, VideoPreviewFrame frame)
    {
        if (frame.BgraPixels.Length != checked(width * height * 4))
        {
            throw new ArgumentException("BGRA frame size does not match its dimensions.", nameof(frame));
        }
        if (_previewSource is null ||
            _previewSource.PixelWidth != width ||
            _previewSource.PixelHeight != height)
        {
            _previewSource = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewSource)));
        }
        _previewSource.WritePixels(
            new Int32Rect(0, 0, width, height),
            frame.BgraPixels,
            width * 4,
            0);
        SetPhase(TooltipPreviewPhase.Playing);
        HideDelayIndicator();
    }

    public void SetPhase(TooltipPreviewPhase phase)
    {
        _phase = phase;
        if (phase is TooltipPreviewPhase.Cover or TooltipPreviewPhase.Closed)
        {
            HideDelayIndicator();
            _previewSource = null;
            _isPreviewVisible = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PreviewSource)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPreviewVisible)));
        }
        else if (phase == TooltipPreviewPhase.Playing)
        {
            _isPreviewVisible = _previewSource is not null;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPreviewVisible)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Phase)));
    }

    private void HideDelayIndicator()
    {
        if (!_isDelayIndicatorVisible)
        {
            return;
        }

        _isDelayIndicatorVisible = false;
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(nameof(IsDelayIndicatorVisible)));
    }
}
