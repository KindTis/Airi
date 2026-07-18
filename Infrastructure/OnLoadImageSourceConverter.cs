using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Airi.Infrastructure
{
    public sealed class OnLoadImageSourceConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string source ||
                !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                return DependencyProperty.UnsetValue;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (uri.IsFile)
                {
                    using var stream = new FileStream(
                        uri.LocalPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    bitmap.StreamSource = stream;
                    bitmap.EndInit();
                }
                else
                {
                    bitmap.UriSource = uri;
                    bitmap.EndInit();
                }
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex) when (
                ex is IOException or NotSupportedException or UnauthorizedAccessException)
            {
                return DependencyProperty.UnsetValue;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
