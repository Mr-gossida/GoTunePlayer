using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class AppSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tunesync-settings-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var settings = AppSettingsStore.Load(_path);

        Assert.Empty(settings.LibraryFolders);
        Assert.Equal(MergeConflictResolution.KeepLargerFile, settings.MergeConflictResolution);
        Assert.True(settings.LyricsSyncUseGpu);
        Assert.Equal(LyricsSyncCpuUsage.Normal, settings.LyricsSyncCpuUsage);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        var settings = new AppSettings
        {
            LibraryFolders =
            [
                new LibraryFolderEntry { Path = @"D:\Music\KPop", IncludeSubfolders = true },
                new LibraryFolderEntry { Path = @"D:\Music\Pop", IncludeSubfolders = false },
            ],
            LastMergeSourceFolder = @"D:\Source",
            LastMergeTargetFolder = @"D:\Target",
            MergeConflictResolution = MergeConflictResolution.RenameWithSuffix,
            RenameIsAlbumMode = true,
            AutoFillIsAutomaticMode = true,
            LyricsSyncUseGpu = false,
            LyricsSyncCpuUsage = LyricsSyncCpuUsage.Low,
        };

        AppSettingsStore.Save(settings, _path);
        var reloaded = AppSettingsStore.Load(_path);

        Assert.Equal(2, reloaded.LibraryFolders.Count);
        Assert.Equal(settings.LibraryFolders[0].Path, reloaded.LibraryFolders[0].Path);
        Assert.Equal(settings.LibraryFolders[0].IncludeSubfolders, reloaded.LibraryFolders[0].IncludeSubfolders);
        Assert.Equal(settings.LibraryFolders[1].Path, reloaded.LibraryFolders[1].Path);
        Assert.Equal(settings.LastMergeSourceFolder, reloaded.LastMergeSourceFolder);
        Assert.Equal(settings.LastMergeTargetFolder, reloaded.LastMergeTargetFolder);
        Assert.Equal(settings.MergeConflictResolution, reloaded.MergeConflictResolution);
        Assert.Equal(settings.RenameIsAlbumMode, reloaded.RenameIsAlbumMode);
        Assert.Equal(settings.AutoFillIsAutomaticMode, reloaded.AutoFillIsAutomaticMode);
        Assert.False(reloaded.LyricsSyncUseGpu);
        Assert.Equal(LyricsSyncCpuUsage.Low, reloaded.LyricsSyncCpuUsage);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileIsCorrupt()
    {
        File.WriteAllText(_path, "{ not valid json");

        var settings = AppSettingsStore.Load(_path);

        Assert.Empty(settings.LibraryFolders);
    }

    [Fact]
    public void CloseAction_DefaultsToExit_AndRoundTrips()
    {
        Assert.Equal(CloseAction.Exit, AppSettingsStore.Load(_path).CloseAction);

        AppSettingsStore.Save(new AppSettings { CloseAction = CloseAction.Tray, TrayNoticeShown = true }, _path);
        var reloaded = AppSettingsStore.Load(_path);

        Assert.Equal(CloseAction.Tray, reloaded.CloseAction);
        Assert.True(reloaded.TrayNoticeShown);
    }

    [Fact]
    public void Load_UnknownCloseAction_FallsBackToExit()
    {
        File.WriteAllText(_path, "{ \"CloseAction\": 7 }");

        Assert.Equal(CloseAction.Exit, AppSettingsStore.Load(_path).CloseAction);
    }
}
