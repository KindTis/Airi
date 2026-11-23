using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using Airi.Infrastructure;
using Airi.ViewModels;
namespace Airi.Controls;

public sealed class GalleryDisplayTitleConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type type, object parameter, CultureInfo culture)
    {
        var title = values.ElementAtOrDefault(0) as string;
        if (!string.IsNullOrWhiteSpace(title)) return title;
        var path = values.Skip(1).OfType<string>().FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        var fileName = path is null ? null : Path.GetFileName(path);
        return string.IsNullOrWhiteSpace(fileName) ? "제목 미지정" : fileName;
    }
    public object[] ConvertBack(object value, Type[] types, object parameter, CultureInfo culture) => types.Select(_ => Binding.DoNothing).ToArray();
}
public sealed class GalleryTextOrPlaceholderConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is string text && !string.IsNullOrWhiteSpace(text) ? text : parameter?.ToString() ?? "미지정";
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class GalleryThumbnailPreviewConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        if (value is not string text || !Uri.TryCreate(text, UriKind.Absolute, out var uri) || !uri.IsFile)
            return DependencyProperty.UnsetValue;

        try
        {
            using var stream = new FileStream(uri.LocalPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return ThumbnailImageLoader.DecodeFrozenBitmap(stream, 440);
        }
        catch (Exception error) when (ThumbnailImageLoader.IsExpectedImageFailure(error))
        {
            return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class GalleryChangeLabelConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture)
    {
        if (value is not GalleryChange change) return string.Empty;
        if (parameter?.ToString() == "Before") return string.IsNullOrWhiteSpace(change.Before) ? "미지정" : change.Before;
        if (!string.IsNullOrWhiteSpace(change.After)) return change.After;
        return string.IsNullOrWhiteSpace(change.Before) && change.LockChanged ? "빈 값 유지 · 수동 보호 변경" : "미지정";
    }
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class GalleryMetadataFieldLabelConverter : IValueConverter
{
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value?.ToString() switch
    {
        "Title" => "제목", "Date" => "출시일", "Actors" => "배우", "Studio" => "제작사",
        "Genres" => "장르", "Tags" => "태그", "Description" => "설명", "Thumbnail" => "썸네일", _ => "변경 항목"
    };
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => Binding.DoNothing;
}
