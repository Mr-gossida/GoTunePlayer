namespace TuneSync.Core.Models;

/// <summary>What a hotkey does.</summary>
public enum HotkeyAction
{
    Previous,
    PlayPause,
    Stop,
    Next,
    SeekBack,
    SeekForward,
    Repeat,
    Shuffle,
    Mute,
    VolumeUp,
    VolumeDown,
    MiniPlayer,
    Hud,
    /// <summary>HUD 클리어 모드 on/off.</summary>
    HudClear,
    /// <summary>HUD 마우스 통과 on/off - also the way back when the HUD can't be clicked.</summary>
    HudClickThrough,
}

/// <summary>Modifier keys, with the values Windows' RegisterHotKey uses.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
}

/// <summary>One hotkey: an action, whether it is on, and its key combination. The key is the
/// name of a WPF <c>System.Windows.Input.Key</c> ("PageUp", "R"...), kept as text so the
/// settings file stays readable and Core needs no WPF.</summary>
public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public bool Enabled { get; set; }
    public HotkeyModifiers Modifiers { get; set; } = HotkeyModifiers.Control | HotkeyModifiers.Alt;
    public string Key { get; set; } = "";

    public HotkeyBinding Clone() => new() { Action = Action, Enabled = Enabled, Modifiers = Modifiers, Key = Key };

    /// <summary>Same keys pressed (whether or not either is on).</summary>
    public bool SameKeys(HotkeyBinding other) =>
        Modifiers == other.Modifiers && string.Equals(Key, other.Key, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Keys that work anywhere in Windows while GoTune runs, even when another program has
/// the focus. Every action has exactly one binding, in a fixed order (<see cref="Normalize"/>).</summary>
public sealed class HotkeySettings
{
    /// <summary>All the hotkeys at once (each still has its own switch).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The keyboard's media keys (play/pause, previous, next, stop) control GoTune.</summary>
    public bool MediaKeys { get; set; } = true;

    public List<HotkeyBinding> Bindings { get; set; } = Defaults();

    /// <summary>The usual layout of a Windows music player: Ctrl+Alt with PageUp/PageDown for the
    /// previous/next song, Insert to play or pause and End to stop - those four on - and the
    /// rest ready but off until wanted.</summary>
    public static List<HotkeyBinding> Defaults() =>
    [
        Bind(HotkeyAction.Previous, true, "PageUp"),
        Bind(HotkeyAction.PlayPause, true, "Insert"),
        Bind(HotkeyAction.Stop, true, "End"),
        Bind(HotkeyAction.Next, true, "PageDown"),
        Bind(HotkeyAction.SeekBack, false, "Left"),
        Bind(HotkeyAction.SeekForward, false, "Right"),
        Bind(HotkeyAction.Repeat, false, "R"),
        Bind(HotkeyAction.Shuffle, false, "S"),
        Bind(HotkeyAction.Mute, false, "M"),
        Bind(HotkeyAction.VolumeUp, false, "Up"),
        Bind(HotkeyAction.VolumeDown, false, "Down"),
        Bind(HotkeyAction.MiniPlayer, false, "Back"),
        Bind(HotkeyAction.Hud, false, "D"),
        Bind(HotkeyAction.HudClear, false, "C"),
        Bind(HotkeyAction.HudClickThrough, false, "T"),
    ];

    private static HotkeyBinding Bind(HotkeyAction action, bool enabled, string key) =>
        new() { Action = action, Enabled = enabled, Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, Key = key };

    public HotkeyBinding this[HotkeyAction action] => Bindings.First(b => b.Action == action);

    /// <summary>One binding per action, in the default order: missing ones (a settings file from
    /// an older version) get their default, duplicates and unknown actions go, an empty key
    /// becomes the default key.</summary>
    public void Normalize()
    {
        var defaults = Defaults();
        var kept = new List<HotkeyBinding>();
        foreach (var fallback in defaults)
        {
            var binding = Bindings?.FirstOrDefault(b => b is not null && b.Action == fallback.Action);
            if (binding is null)
            {
                kept.Add(fallback);
                continue;
            }
            if (string.IsNullOrWhiteSpace(binding.Key))
            {
                binding.Key = fallback.Key;
                binding.Modifiers = fallback.Modifiers;
            }
            binding.Modifiers &= HotkeyModifiers.Alt | HotkeyModifiers.Control | HotkeyModifiers.Shift;
            kept.Add(binding);
        }
        Bindings = kept;
    }

    /// <summary>Back to the defaults (the switches for all and for the media keys included).</summary>
    public void Reset()
    {
        Enabled = true;
        MediaKeys = true;
        Bindings = Defaults();
    }
}
