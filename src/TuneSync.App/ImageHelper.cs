using System.IO;
using System.Windows.Media.Imaging;

namespace TuneSync.App;

/// <summary>Turns raw image bytes into a frozen, cross-thread-safe WPF BitmapImage. TuneSync.Core
/// stays WPF-free, so this conversion lives here rather than in a Core service.</summary>
public static class ImageHelper
{
    public static BitmapImage? ToBitmapImage(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
