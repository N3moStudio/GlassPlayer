using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace GlassPlayer;

public sealed class ArtworkConverter : IValueConverter
{
    readonly RecentCache<string, BitmapImage> thumbnails = new(32, TimeSpan.FromMinutes(3));
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string address || string.IsNullOrWhiteSpace(address)) return null;
        if (thumbnails.TryGet(address, out var cached)) return cached;
        var image = Create(address, 96);
        if (image != null) thumbnails.Set(address, image);
        return image;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    internal static BitmapImage? Create(string? address, int pixels)
    {
        if (!Uri.TryCreate(SizedAddress(address, pixels), UriKind.Absolute, out var uri)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.DecodePixelWidth = pixels;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = uri;
            image.EndInit();
            if (image.CanFreeze) image.Freeze();
            return image;
        }
        catch { return null; }
    }
    internal static string? SizedAddress(string? address, int pixels)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "https") return address;
        if (uri.Host == "sndcdn.com" || uri.Host.EndsWith(".sndcdn.com", StringComparison.OrdinalIgnoreCase))
        {
            var size = pixels <= 100 ? "large" : "t500x500";
            return System.Text.RegularExpressions.Regex.Replace(address!, @"-(original|large|t\d+x\d+)\.(jpg|png)(?=\?|$)", "-" + size + ".$2");
        }
        return address;
    }
}
