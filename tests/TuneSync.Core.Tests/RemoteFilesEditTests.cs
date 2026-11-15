using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Tests;

public class RemoteFilesEditTests
{
    [Fact]
    public async Task Edit_WaitsForAReaderToLetGo()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tunesync-edit-{Guid.NewGuid():N}.mp3");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestAssets", "sample.mp3"), path);
        try
        {
            // a reader that doesn't share writing, like the spectrum analysis of a song that just started
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var release = Task.Run(async () => { await Task.Delay(600); reader.Dispose(); });

            RemoteFiles.Edit(path, f => f.Tag.Lyrics = "[00:01.00]hello");
            await release;

            using var check = TagLib.File.Create(path);
            Assert.Equal("[00:01.00]hello", check.Tag.Lyrics);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
