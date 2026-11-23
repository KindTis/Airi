using System.Windows;

namespace Airi.Controls;

public sealed record GalleryTooltipLayout(double Width, double Height, double Padding, double Gap, double CoverWidth, double CoverHeight, double BodyHeight)
{
    private const double ViewportInset = 12;
    private const double PointerGap = 16;

    public static GalleryTooltipLayout Calculate(Size viewport, double cardCoverWidth, double informationHeight = 0,
        double maximumWidth = double.PositiveInfinity)
    {
        const double padding = 12, gap = 12;
        var availableWidth = Math.Min(viewport.Width - 2 * ViewportInset, maximumWidth);
        // Two 12-DIP sides, image/information gap, and the 24-DIP shortcut footer.
        const double chromeHeight = 2 * padding + gap + 24;
        var contentHeight = Math.Max(1, viewport.Height - 2 * ViewportInset - chromeHeight);
        var bodyHeight = Math.Clamp(Math.Ceiling(informationHeight), 0, contentHeight - 1);
        var coverLimit = contentHeight - bodyHeight;
        var coverWidth = Math.Min(Math.Max(1, cardCoverWidth) * 1.5,
            Math.Min(Math.Max(1, availableWidth - 2 * padding), coverLimit * GalleryLayoutMetrics.CoverAspectRatio));
        var coverHeight = coverWidth / GalleryLayoutMetrics.CoverAspectRatio;
        return new(coverWidth + 2 * padding, coverHeight + bodyHeight + chromeHeight,
            padding, gap, coverWidth, coverHeight, bodyHeight);
    }

    public static Rect Place(Point pointer, Size viewport, Size popup, ref bool left)
    {
        var leftEdge = Math.Min(viewport.Width - ViewportInset, pointer.X - PointerGap);
        var rightEdge = Math.Max(ViewportInset, pointer.X + PointerGap);
        var leftSpace = leftEdge - ViewportInset;
        var rightSpace = viewport.Width - ViewportInset - rightEdge;
        var currentSpace = left ? leftSpace : rightSpace;
        var otherSpace = left ? rightSpace : leftSpace;
        // Keep the current side while it fits. If neither fits, use the roomier side.
        if (popup.Width > currentSpace && otherSpace > currentSpace)
        {
            left = !left;
            currentSpace = otherSpace;
        }

        var width = Math.Min(popup.Width, Math.Max(0, currentSpace));
        var x = left ? leftEdge - width : rightEdge;
        var y = Math.Clamp(pointer.Y, ViewportInset, Math.Max(ViewportInset, viewport.Height - popup.Height - ViewportInset));
        return new Rect(x, y, width, popup.Height);
    }
}
