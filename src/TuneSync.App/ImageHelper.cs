using System.IO;
using System.Windows.Media.Imaging;
using TuneSync.Core.Services;

namespace TuneSync.App;

/// <summary>Turns raw image bytes into a frozen, cross-thread-safe WPF BitmapImage. TuneSync.Core
/// stays WPF-free, so this conversion lives here rather than in a Core service.
///
/// Covers come from files and from the internet, so their size is read from the header first and
/// an absurd one is refused before anything is decoded (<see cref="ImageLimits"/>).</summary>
public static class ImageHelper
{
    public static BitmapImage? ToBitmapImage(byte[]? bytes, int decodePixelWidth = 0)
    {
        if (bytes is null || bytes.Length == 0) return null;
        if (!TryGetPixelSize(bytes, out var width, out var height) || !ImageLimits.IsSensibleSize(width, height)) return null;
        return Decode(bytes, decodePixelWidth, 0);
    }

    /// <summary>An image decoded to be shown, and the size it's stored at (the bitmap's own is
    /// the reduced one).</summary>
    public sealed record DisplayImage(BitmapImage Image, int PixelWidth, int PixelHeight)
    {
        public string SizeText => $"{PixelWidth}×{PixelHeight}";
    }

    /// <summary>Decodes <paramref name="bytes"/> with its longer side at most
    /// <paramref name="maxSide"/> pixels (a smaller image keeps its size); null when it isn't an
    /// image WPF reads or claims an absurd size. Any thread; the bitmap is frozen.</summary>
    public static DisplayImage? ToDisplayImage(byte[]? bytes, int maxSide)
    {
        if (bytes is null || bytes.Length == 0) return null;
        if (!TryGetPixelSize(bytes, out var width, out var height) || !ImageLimits.IsSensibleSize(width, height)) return null;
        var image = width >= height
            ? Decode(bytes, width > maxSide ? maxSide : 0, 0)
            : Decode(bytes, 0, height > maxSide ? maxSide : 0);
        return image is null ? null : new DisplayImage(image, width, height);
    }

    /// <summary>The size an image is stored at, read from its header without decoding it.</summary>
    public static bool TryGetPixelSize(byte[] bytes, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            (width, height) = (frame.PixelWidth, frame.PixelHeight);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static BitmapImage? Decode(byte[] bytes, int decodePixelWidth, int decodePixelHeight)
    {
        try
        {
            var image = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = ms;
            // Decoding straight to the size it's shown at keeps a 3000px scan from costing ~36 MB.
            if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
            else if (decodePixelHeight > 0) image.DecodePixelHeight = decodePixelHeight;
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
