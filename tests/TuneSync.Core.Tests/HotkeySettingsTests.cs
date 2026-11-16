using System.Text.Json;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class HotkeySettingsTests
{
    [Fact]
    public void Defaults_AreTheUsualPlayerKeys_FourOnTheRestOff()
    {
        var hotkeys = new HotkeySettings();
        Assert.True(hotkeys.Enabled);
        Assert.True(hotkeys.MediaKeys);
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, hotkeys.Bindings.Count);
        foreach (var binding in hotkeys.Bindings) Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Alt, binding.Modifiers);

        (HotkeyAction, string, bool)[] expected =
        [
            (HotkeyAction.Previous, "PageUp", true), (HotkeyAction.PlayPause, "Insert", true),
            (HotkeyAction.Stop, "End", true), (HotkeyAction.Next, "PageDown", true),
            (HotkeyAction.SeekBack, "Left", false), (HotkeyAction.SeekForward, "Right", false),
            (HotkeyAction.Repeat, "R", false), (HotkeyAction.Shuffle, "S", false), (HotkeyAction.Mute, "M", false),
            (HotkeyAction.VolumeUp, "Up", false), (HotkeyAction.VolumeDown, "Down", false),
            (HotkeyAction.MiniPlayer, "Back", false), (HotkeyAction.Hud, "D", false),
        ];
        foreach (var (action, key, enabled) in expected)
        {
            Assert.Equal(key, hotkeys[action].Key);
            Assert.Equal(enabled, hotkeys[action].Enabled);
        }
    }

    [Fact]
    public void Normalize_FillsMissingActions_DropsDuplicates_KeepsChoices()
    {
        var hotkeys = new HotkeySettings
        {
            Bindings =
            [
                new() { Action = HotkeyAction.Next, Enabled = false, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Shift, Key = "N" },
                new() { Action = HotkeyAction.Next, Enabled = true, Key = "X" },
                new() { Action = HotkeyAction.Mute, Enabled = true, Key = "" },
                null!,
            ],
        };
        hotkeys.Normalize();
        Assert.Equal(Enum.GetValues<HotkeyAction>().Length, hotkeys.Bindings.Count);
        Assert.Equal(Enum.GetValues<HotkeyAction>(), hotkeys.Bindings.Select(b => b.Action));
        Assert.Equal("N", hotkeys[HotkeyAction.Next].Key);
        Assert.False(hotkeys[HotkeyAction.Next].Enabled);
        Assert.Equal(HotkeyModifiers.Control | HotkeyModifiers.Shift, hotkeys[HotkeyAction.Next].Modifiers);
        Assert.Equal("M", hotkeys[HotkeyAction.Mute].Key); // empty key: the default one
        Assert.True(hotkeys[HotkeyAction.Mute].Enabled);
        Assert.Equal("PageUp", hotkeys[HotkeyAction.Previous].Key); // missing: the default
    }

    [Fact]
    public void SavedAndLoaded_KeepsTheChanges_AndAnOldFileGetsTheDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new AppSettings();
            settings.Hotkeys.MediaKeys = false;
            settings.Hotkeys[HotkeyAction.Shuffle].Enabled = true;
            settings.Hotkeys[HotkeyAction.Shuffle].Key = "F9";
            AppSettingsStore.Save(settings, path);
            var loaded = AppSettingsStore.Load(path);
            Assert.False(loaded.Hotkeys.MediaKeys);
            Assert.True(loaded.Hotkeys[HotkeyAction.Shuffle].Enabled);
            Assert.Equal("F9", loaded.Hotkeys[HotkeyAction.Shuffle].Key);

            // a settings file written before hotkeys existed
            File.WriteAllText(path, JsonSerializer.Serialize(new { Volume = 0.5 }));
            var old = AppSettingsStore.Load(path);
            Assert.True(old.Hotkeys.Enabled);
            Assert.Equal("Insert", old.Hotkeys[HotkeyAction.PlayPause].Key);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
