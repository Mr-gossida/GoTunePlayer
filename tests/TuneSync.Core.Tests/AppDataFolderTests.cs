using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class AppDataFolderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tunesync-datafolder-{Guid.NewGuid():N}");
    private readonly string _new;
    private readonly string _legacy;

    public AppDataFolderTests()
    {
        Directory.CreateDirectory(_root);
        _new = Path.Combine(_root, "GoTune");
        _legacy = Path.Combine(_root, "TuneSync");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_fresh_install_uses_the_new_folder_without_creating_it()
    {
        Assert.Equal(_new, AppDataFolder.Resolve(_new, _legacy));
        Assert.False(Directory.Exists(_new));
        Assert.False(Directory.Exists(_legacy));
    }

    [Fact]
    public void The_old_folder_is_moved_over_whole()
    {
        Directory.CreateDirectory(Path.Combine(_legacy, "models"));
        File.WriteAllText(Path.Combine(_legacy, "settings.json"), "{\"Volume\":0.4}");
        File.WriteAllText(Path.Combine(_legacy, "models", "small.bin"), "model");

        Assert.Equal(_new, AppDataFolder.Resolve(_new, _legacy));

        Assert.False(Directory.Exists(_legacy));
        Assert.Equal("{\"Volume\":0.4}", File.ReadAllText(Path.Combine(_new, "settings.json")));
        Assert.Equal("model", File.ReadAllText(Path.Combine(_new, "models", "small.bin")));
    }

    [Fact]
    public void An_existing_new_folder_wins_and_the_old_one_is_left_alone()
    {
        Directory.CreateDirectory(_new);
        File.WriteAllText(Path.Combine(_new, "settings.json"), "new");
        Directory.CreateDirectory(_legacy);
        File.WriteAllText(Path.Combine(_legacy, "settings.json"), "old");

        Assert.Equal(_new, AppDataFolder.Resolve(_new, _legacy));

        Assert.Equal("new", File.ReadAllText(Path.Combine(_new, "settings.json")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_legacy, "settings.json")));
    }

    [Fact]
    public void When_the_old_folder_cannot_be_moved_it_keeps_being_used()
    {
        Directory.CreateDirectory(_legacy);
        var held = Path.Combine(_legacy, "settings.json");
        File.WriteAllText(held, "old");

        // Another copy of the app holding a file open, as an older version still running would.
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(_legacy, AppDataFolder.Resolve(_new, _legacy));
            Assert.False(Directory.Exists(_new));
        }

        // Next start, with nothing holding it: the move goes through.
        Assert.Equal(_new, AppDataFolder.Resolve(_new, _legacy));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_new, "settings.json")));
    }

    [Fact]
    public void Moving_twice_is_harmless()
    {
        Directory.CreateDirectory(_legacy);
        File.WriteAllText(Path.Combine(_legacy, "settings.json"), "old");

        Assert.Equal(_new, AppDataFolder.Resolve(_new, _legacy));
        Assert.Equal(_new, AppDataFolder.Resolve(_new, _legacy));
        Assert.Equal("old", File.ReadAllText(Path.Combine(_new, "settings.json")));
    }
}
