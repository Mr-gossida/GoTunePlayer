using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class CoverArtServiceTests
{
    private static byte[] ReadAsset(string fileName) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName));

    [Theory]
    [InlineData("sample.mp3")]
    [InlineData("sample.flac")]
    [InlineData("sample.m4a")]
    public void SetThenGetCover_RoundTrips(string fixtureName)
    {
        var path = TestFixtures.CopyFixture(fixtureName);
        try
        {
            Assert.Null(CoverArtService.GetCover(path));

            var coverBytes = ReadAsset("cover_a.jpg");
            CoverArtService.SetCover(path, coverBytes);

            var read = CoverArtService.GetCover(path);
            Assert.NotNull(read);
            Assert.Equal(coverBytes.Length, read!.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetThumbnail_ReturnsJpegBytes_ForValidImage()
    {
        var thumb = CoverArtService.GetThumbnail(ReadAsset("cover_a.jpg"));

        Assert.NotNull(thumb);
        Assert.NotEmpty(thumb!.JpegBytes);
    }

    [Fact]
    public void GetThumbnail_RefusesAnAbsurdSize_BeforeDecodingIt()
    {
        // wider than any cover (one row, so it stays small here; 20000×20000 would take 1.6 GB)
        static byte[] Png(int width, int height)
        {
            using var bitmap = new System.Drawing.Bitmap(width, height);
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        Assert.Null(CoverArtService.GetThumbnail(Png(ImageLimits.MaxSide + 1, 1)));
        Assert.NotNull(CoverArtService.GetThumbnail(Png(ImageLimits.MaxSide, 1)));
    }

    [Fact]
    public void GetThumbnail_ReturnsNull_ForNullOrEmptyInput()
    {
        Assert.Null(CoverArtService.GetThumbnail(null));
        Assert.Null(CoverArtService.GetThumbnail([]));
    }

    [Fact]
    public void ComputeThumbHash_IsStable_ForSameImageAcrossRescans()
    {
        var hashA = CoverArtService.ComputeThumbHash(ReadAsset("cover_a.jpg"));
        var hashARescan = CoverArtService.ComputeThumbHash(ReadAsset("cover_a_rescan.jpg"));

        Assert.NotNull(hashA);
        Assert.Equal(hashA, hashARescan);
    }

    [Fact]
    public void ComputeThumbHash_Differs_ForDifferentImages()
    {
        var hashA = CoverArtService.ComputeThumbHash(ReadAsset("cover_a.jpg"));
        var hashB = CoverArtService.ComputeThumbHash(ReadAsset("cover_b.jpg"));

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void CompareVisualDifference_IsSmall_ForSameImageAcrossRescans()
    {
        var diff = CoverArtService.CompareVisualDifference(ReadAsset("cover_a.jpg"), ReadAsset("cover_a_rescan.jpg"));

        Assert.NotNull(diff);
        Assert.True(diff < 5.0, $"Expected a small difference for re-scanned same image, got {diff}");
    }

    [Fact]
    public void CompareVisualDifference_IsLarge_ForDifferentImages()
    {
        var diff = CoverArtService.CompareVisualDifference(ReadAsset("cover_a.jpg"), ReadAsset("cover_b.jpg"));

        Assert.NotNull(diff);
        Assert.True(diff > 50.0, $"Expected a large difference for genuinely different images, got {diff}");
    }
}
