using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using TagLib;

namespace TuneSync.Core.Services;

/// <summary>Reads/writes embedded front-cover art and compares covers the way a human would:
/// same-image-different-scan vs. genuinely-different-picture, not just "does a picture exist".
/// Windows-only (System.Drawing/GDI+) - fine here since TuneSync only ships as a WPF app.</summary>
public static class CoverArtService
{
    public static byte[]? GetCover(string path)
    {
        using var file = TagLib.File.Create(path);
        var pic = file.Tag.Pictures.FirstOrDefault();
        return pic?.Data?.Data;
    }

    public static void SetCover(string path, byte[] imageBytes, string mimeType = "image/jpeg")
    {
        using var file = TagLib.File.Create(path);
        var picture = new Picture(new ByteVector(imageBytes))
        {
            Type = PictureType.FrontCover,
            Description = "Front Cover",
            MimeType = mimeType,
        };
        file.Tag.Pictures = [picture];
        file.Save();
    }

    public static BitmapImageBytes? GetThumbnail(byte[]? imageBytes, int size = 160)
    {
        if (imageBytes is null || imageBytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var src = Image.FromStream(ms);
            using var thumb = new Bitmap(size, size, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, size, size);
            }
            using var outMs = new MemoryStream();
            thumb.Save(outMs, ImageFormat.Jpeg);
            return new BitmapImageBytes(outMs.ToArray());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>16x16 grayscale perceptual-ish hash: re-encodes/re-scans of the same art collapse
    /// to the same key, so it can be used to group "same cover" across a whole library cheaply.</summary>
    public static string? ComputeThumbHash(byte[]? imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0) return null;
        try
        {
            using var ms = new MemoryStream(imageBytes);
            using var src = Image.FromStream(ms);
            using var thumb = new Bitmap(16, 16, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(thumb))
                g.DrawImage(src, 0, 0, 16, 16);

            var bytes = new byte[16 * 16];
            var i = 0;
            for (var y = 0; y < 16; y++)
                for (var x = 0; x < 16; x++)
                {
                    var p = thumb.GetPixel(x, y);
                    bytes[i++] = (byte)((p.R + p.G + p.B) / 3);
                }
            return Convert.ToHexString(MD5.HashData(bytes));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Mean absolute per-channel difference on a 24x24 RGB downscale. Empirically:
    /// ~0.1-1.1 = same picture (different scan/compression), ~100 = a genuinely different picture.</summary>
    public static double? CompareVisualDifference(byte[]? a, byte[]? b)
    {
        if (a is null || b is null) return null;
        try
        {
            using var thumbA = ToThumb24(a);
            using var thumbB = ToThumb24(b);
            if (thumbA is null || thumbB is null) return null;

            long total = 0;
            for (var y = 0; y < 24; y++)
                for (var x = 0; x < 24; x++)
                {
                    var pa = thumbA.GetPixel(x, y);
                    var pb = thumbB.GetPixel(x, y);
                    total += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                }
            return total / (24.0 * 24.0 * 3.0);
        }
        catch
        {
            return null;
        }
    }

    private static Bitmap? ToThumb24(byte[] imageBytes)
    {
        using var ms = new MemoryStream(imageBytes);
        using var src = Image.FromStream(ms);
        var thumb = new Bitmap(24, 24, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(thumb);
        g.DrawImage(src, 0, 0, 24, 24);
        return thumb;
    }
}

/// <summary>A re-encoded (JPEG) thumbnail's raw bytes, kept format-agnostic so the WPF layer can
/// turn it into a BitmapImage without TuneSync.Core taking a WPF dependency.</summary>
public sealed class BitmapImageBytes(byte[] jpegBytes)
{
    public byte[] JpegBytes { get; } = jpegBytes;
}
