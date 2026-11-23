using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
namespace Airi.Controls;

public sealed class GalleryIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(GalleryIcon), new FrameworkPropertyMetadata("film", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(typeof(GalleryIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public GalleryIcon() { Width = 17; Height = 17; IsHitTestVisible = false; }
    protected override void OnRender(DrawingContext dc)
    {
        var pen = new Pen(Foreground, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        string path = Kind switch
        {
            "search" => "M16,16 L20,20",
            "folder" => "M3,7 A2,2 0 0 1 5,5 L10,5 12,7 19,7 A2,2 0 0 1 21,9 L21,18 A2,2 0 0 1 19,20 L5,20 A2,2 0 0 1 3,18 Z",
            "person" => "M5,21 L5,19 A7,7 0 0 1 19,19 L19,21",
            "studio" => "M4,6 L6,3 22,3 20,6 M8,3 L6,6 M14,3 L12,6 M20,3 L18,6 M4,11 L20,11 M4,6 L20,6 20,21 4,21 Z",
            "tag" => "M3,4 L3,11 13,21 21,13 11,3 4,3 Z",
            "trash" => "M3,6 L21,6 M9,6 L9,3 15,3 15,6 M5,6 L6,21 18,21 19,6 M10,10 L10,17 M14,10 L14,17",
            "check" => "M7.5,12 L10.5,15 16.5,9",
            "checkbox" => "M6,3 L18,3 A3,3 0 0 1 21,6 L21,18 A3,3 0 0 1 18,21 L6,21 A3,3 0 0 1 3,18 L3,6 A3,3 0 0 1 6,3 Z M7.5,12 L10.5,15 16.5,9",
            "warning" => "M12,3 L22,21 2,21 Z M12,9 L12,14 M12,17 L12,17.2",
            "edit" => "M15,4 L20,9 M4,20 L5,14 16,3 A2,2 0 0 1 19,3 L21,5 A2,2 0 0 1 21,8 L10,19 Z",
            "play" => "M8,4 L21,12 8,20 Z",
            "refresh" => "M20,7 L20,3 17,6 A8,8 0 1 0 19,17 M20,3 L15,3",
            "sort" => "M8,4 L8,20 M4,16 L8,20 12,16 M15,5 L21,5 M15,10 L19,10 M15,15 L17,15",
            "shuffle" => "M3,5 L6,5 18,19 21,19 M18,16 L21,19 18,22 M3,19 L6,19 10,14 M14,10 L18,5 21,5 M18,2 L21,5 18,8",
            "close" => "M6,6 L18,18 M6,18 L18,6",
            "chevron" => "M7,10 L12,15 17,10",
            "back" => "M10,5 L3,12 10,19 M3,12 L21,12",
            "info" => "M12,11 L12,17 M12,7 L12,7.2",
            _ => "M3,4 L21,4 21,20 3,20 Z M7,4 L7,20 M17,4 L17,20 M3,9 L7,9 M3,15 L7,15 M17,9 L21,9 M17,15 L21,15"
        };
        if (Kind is "search") dc.DrawEllipse(null, pen, new Point(10.5, 10.5), 6.5, 6.5);
        if (Kind is "person") dc.DrawEllipse(null, pen, new Point(12, 8), 3, 3);
        if (Kind is "check" or "info") dc.DrawEllipse(null, pen, new Point(12, 12), 9, 9);
        if (Kind is "tag") dc.DrawEllipse(null, pen, new Point(7.5, 7.5), 1, 1);
        dc.DrawGeometry(null, pen, Geometry.Parse(path)); dc.Pop();
    }
}
public sealed class GalleryRoundedBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        // The image may have a larger desired size under UniformToFill. Clip the finite cover bounds,
        // rather than that image's desired rectangle, so all four corners share the same radius.
        Clip = new RectangleGeometry(new Rect(new Point(), arranged), CornerRadius.TopLeft, CornerRadius.TopLeft);
        return arranged;
    }
}
public sealed class GalleryVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) { var result = value is bool b ? b : value is string s ? !string.IsNullOrWhiteSpace(s) : value is int i ? i > 0 : value is not null; if ((p as string) == "Inverse") result = !result; return result ? Visibility.Visible : Visibility.Collapsed; }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public sealed class GalleryListLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if(parameter?.ToString()=="제작사") return value is string studio&&!string.IsNullOrWhiteSpace(studio)?studio:"제작사 미지정";
        if (value is IEnumerable<string> values) { var items = values.ToArray(); return items.Length > 0 ? string.Join(" · ", items) : $"{parameter ?? "값"} 미지정"; }
        if (value is string text && DateOnly.TryParseExact(text, "yyyy-MM-dd", culture, System.Globalization.DateTimeStyles.None, out var date)) return date.ToString("yyyy.MM.dd", culture);
        return value;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
public sealed class GalleryFolderLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) { var text = value?.ToString() ?? string.Empty; var name = System.IO.Path.GetFileName(text.TrimEnd('\\', '/')); return string.IsNullOrEmpty(name) ? text : name; }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}