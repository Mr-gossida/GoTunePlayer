namespace TuneSync.Core.Tests;

/// <summary>Copies a committed sample under TestAssets/ into a fresh temp file so tests can
/// freely mutate tags/covers without ever touching (or needing to restore) the original.</summary>
internal static class TestFixtures
{
    public static string CopyFixture(string fileName)
    {
        var sourcePath = Path.Combine(AppContext.BaseDirectory, "TestAssets", fileName);
        var destPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}{Path.GetExtension(fileName)}");
        File.Copy(sourcePath, destPath);
        return destPath;
    }
}
