namespace TuneSync.Core.Services;

/// <summary>The largest picture decoded, wherever it comes from (a file's cover, the internet).
/// Its size is read from the header first and an absurd one is refused before anything is
/// decoded: a picture that claims 20000×20000 takes a few MB in a file but 1.6 GB and seconds to
/// decode - and the library scan makes thumbnails of several covers at once.</summary>
public static class ImageLimits
{
    /// <summary>The widest or tallest image decoded (real covers are a few thousand pixels).</summary>
    public const int MaxSide = 10000;

    /// <summary>The most pixels an image decoded may have: 50 MP, 200 MB decoded.</summary>
    public const long MaxPixels = 50_000_000;

    public static bool IsSensibleSize(int width, int height) =>
        width > 0 && height > 0 && width <= MaxSide && height <= MaxSide && (long)width * height <= MaxPixels;
}
