using TuneSync.Core.Audio;

namespace TuneSync.Core.Tests;

/// <summary>The device volume GoTune writes down while it holds it (hardware volume), to put back
/// after a run that couldn't. Only the file is tried here: nothing touches a real device.</summary>
public class HeldVolumeTests
{
    private static string NewFile() => Path.Combine(Path.GetTempPath(), $"gotune-test-{Guid.NewGuid():N}.json");

    [Fact]
    public void Take_IsWrittenDownUntilPutBack()
    {
        var file = NewFile();
        try
        {
            var held = new HeldVolume(file);
            Assert.Equal(new HeldVolume.Level(0.5f, false), held.Take("device", new(0.5f, false)));
            Assert.True(File.Exists(file));
            held.Release("device", putBack: true);
            Assert.False(File.Exists(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AfterARunThatNeverLetGo_TheEarlierLevelIsTheOneToPutBack()
    {
        var file = NewFile();
        try
        {
            // the last run took the device at 50 %, turned it down to 10 % and muted it, then crashed
            new HeldVolume(file).Take("device", new(0.5f, false));
            var next = new HeldVolume(file);
            Assert.Equal(new HeldVolume.Level(0.5f, false), next.Take("device", new(0.1f, true)));
            next.Release("device", putBack: true);

            // turned up past it since: that level is Windows' own now
            new HeldVolume(file).Take("device", new(0.5f, false));
            Assert.Equal(new HeldVolume.Level(0.8f, false), new HeldVolume(file).Take("device", new(0.8f, false)));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void UnpluggedBeforeBeingPutBack_StaysWrittenDown()
    {
        var file = NewFile();
        try
        {
            var held = new HeldVolume(file);
            held.Take("device", new(0.5f, false));
            held.Release("device", putBack: false);
            Assert.Equal(new HeldVolume.Level(0.5f, false), held.Take("device", new(0.2f, false)));

            // another device's level is its own
            Assert.Equal(new HeldVolume.Level(0.3f, true), held.Take("other", new(0.3f, true)));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
