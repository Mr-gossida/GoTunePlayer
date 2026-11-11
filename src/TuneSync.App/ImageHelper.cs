using System.IO;
using System.Windows.Media.Imaging;

namespace TuneSync.App;

/// <summary>Turns raw image bytes into a frozen, cross-thread-safe WPF BitmapImage. TuneSync.Core
/// stays WPF-free, so this conversion lives here rather than in a Core service.</summary>
public static class ImageHelper
{
    public static BitmapImage? ToBitmapImage(byte[]? bytes, int decodePixelWidth = 0)
    {
        if (bytes is null || bytes.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            // Decoding straight to the size it's shown at keeps a 3000px scan from costing ~36 MB.
            if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A soft, pre-blurred copy of a cover for the player backdrop. Blurring a 48px copy
    /// once (box blur, three passes ≈ Gaussian) and letting the window simply stretch it costs
    /// nothing per frame, unlike a live BlurEffect over the whole window, which made resizing
    /// stutter. Any thread; returns a frozen bitmap.</summary>
    public static BitmapSource? BlurredBackdrop(BitmapSource? source)
    {
        if (source is null || source.PixelWidth == 0 || source.PixelHeight == 0) return null;
        try
        {
            const int size = 48;
            var scaled = new TransformedBitmap(source, new System.Windows.Media.ScaleTransform(
                (double)size / source.PixelWidth, (double)size / source.PixelHeight));
            var bgra = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
            var pixels = new byte[h * stride];
            bgra.CopyPixels(pixels, stride, 0);

            var temp = new byte[pixels.Length];
            for (var pass = 0; pass < 3; pass++)
            {
                BoxBlur(pixels, temp, w, h, radius: 4, horizontal: true);
                BoxBlur(temp, pixels, w, h, radius: 4, horizontal: false);
            }

            var result = BitmapSource.Create(w, h, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void BoxBlur(byte[] src, byte[] dst, int w, int h, int radius, bool horizontal)
    {
        var lines = horizontal ? h : w;
        var length = horizontal ? w : h;
        for (var line = 0; line < lines; line++)
        {
            for (var i = 0; i < length; i++)
            {
                int b = 0, g = 0, r = 0, a = 0, n = 0;
                for (var k = -radius; k <= radius; k++)
                {
                    var j = Math.Clamp(i + k, 0, length - 1); // clamp: no dark fringe at the edges
                    var idx = horizontal ? (line * w + j) * 4 : (j * w + line) * 4;
                    b += src[idx]; g += src[idx + 1]; r += src[idx + 2]; a += src[idx + 3]; n++;
                }
                var o = horizontal ? (line * w + i) * 4 : (i * w + line) * 4;
                dst[o] = (byte)(b / n); dst[o + 1] = (byte)(g / n); dst[o + 2] = (byte)(r / n); dst[o + 3] = (byte)(a / n);
            }
        }
    }
}
