using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Grimmcair.Desktop.Services;

public static class ResourceImage
{
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.Ordinal);
    public static ImageSource? Load(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            if (!ResourcePack.Contains(name)) return null;
            using var source = new MemoryStream(ResourcePack.ReadBytes(name), writable: false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = source;
            image.EndInit();
            image.Freeze();
            Cache[name] = image;
            return image;
        }
    }
}

public sealed class ResourceImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string name ? ResourceImage.Load(name) : null;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
