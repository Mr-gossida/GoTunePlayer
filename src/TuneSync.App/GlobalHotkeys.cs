using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App;

/// <summary>Keys that control the player from anywhere in Windows (<see cref="HotkeySettings"/>):
/// each enabled combination is registered with Windows (RegisterHotKey) on a hidden message
/// window, and the keyboard's media keys too when that is on. A combination another program
/// already holds can't be taken: it is reported (<see cref="Problem"/>) so the settings page can
/// say so. While a new key is being typed in the settings the hotkeys are let go
/// (<see cref="Suspend"/>), so the keys reach the box instead of triggering the action.</summary>
public sealed class GlobalHotkeys : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const int MediaIdBase = 0x100;

    private readonly AppSettings _settings;
    private readonly PlaybackViewModel _playback;
    private HwndSource? _window;
    private readonly Dictionary<int, HotkeyAction> _registered = [];
    private readonly Dictionary<HotkeyAction, string> _problems = [];
    private int _suspended;

    public GlobalHotkeys(AppSettings settings, PlaybackViewModel playback)
    {
        _settings = settings;
        _playback = playback;
    }

    /// <summary>What the main window does for the actions that are about windows (the mini
    /// player, the HUD).</summary>
    public Action<HotkeyAction>? WindowAction { get; set; }

    /// <summary>Raised after the keys were (re)registered: problems may have changed.</summary>
    public event Action? Applied;

    /// <summary>Why an action's key isn't working, or null when it is (or is off).</summary>
    public string? Problem(HotkeyAction action) => _problems.GetValueOrDefault(action);

    public void Start()
    {
        if (_window is not null) return;
        // a message-only window: never shown, only receives the hotkey messages
        _window = new HwndSource(new HwndSourceParameters("GoTuneHotkeys") { ParentWindow = new IntPtr(-3), WindowStyle = 0 });
        _window.AddHook(Hook);
        Apply();
    }

    /// <summary>Registers the keys as the settings say now (after any change to them).</summary>
    public void Apply()
    {
        if (_window is null) return;
        UnregisterAll();
        // let go while a key is being typed in, but keep showing what was wrong with the others
        if (_suspended > 0) return;
        _problems.Clear();
        var hotkeys = _settings.Hotkeys;
        if (hotkeys.Enabled)
        {
            var taken = new List<HotkeyBinding>();
            foreach (var binding in hotkeys.Bindings.Where(b => b.Enabled))
            {
                if (taken.FirstOrDefault(b => b.SameKeys(binding)) is { } other)
                {
                    _problems[binding.Action] = $"'{HotkeyText.ActionName(other.Action)}'와(과) 같은 키입니다";
                    continue;
                }
                if (VirtualKey(binding.Key) is not { } vk)
                {
                    _problems[binding.Action] = "쓸 수 없는 키입니다";
                    continue;
                }
                // volume and seeking repeat while held; the rest fire once per press
                var repeats = binding.Action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown or HotkeyAction.SeekBack or HotkeyAction.SeekForward;
                var id = (int)binding.Action + 1;
                if (RegisterHotKey(_window.Handle, id, (uint)binding.Modifiers | (repeats ? 0 : ModNoRepeat), vk))
                {
                    _registered[id] = binding.Action;
                    taken.Add(binding);
                }
                else
                {
                    _problems[binding.Action] = "다른 프로그램이 이미 쓰고 있는 키입니다";
                }
            }
        }
        if (hotkeys.MediaKeys)
        {
            // Media keys: if another player holds them, they stay with it - nothing to report.
            (uint Vk, HotkeyAction Action)[] media =
            [
                (0xB3, HotkeyAction.PlayPause), (0xB0, HotkeyAction.Next), (0xB1, HotkeyAction.Previous), (0xB2, HotkeyAction.Stop),
            ];
            for (var i = 0; i < media.Length; i++)
                if (RegisterHotKey(_window.Handle, MediaIdBase + i, ModNoRepeat, media[i].Vk))
                    _registered[MediaIdBase + i] = media[i].Action;
        }
        Applied?.Invoke();
    }

    /// <summary>Lets go of every key until <see cref="Resume"/> (while a key is being typed in).</summary>
    public void Suspend()
    {
        if (_suspended++ == 0) Apply();
    }

    public void Resume()
    {
        if (_suspended > 0 && --_suspended == 0) Apply();
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _registered.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            Run(action);
        }
        return IntPtr.Zero;
    }

    /// <summary>Does what a hotkey asks for.</summary>
    public void Run(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.Previous: _playback.PlayPreviousCommand.Execute(null); break;
            case HotkeyAction.PlayPause: _playback.TogglePlayPauseCommand.Execute(null); break;
            case HotkeyAction.Stop: _playback.StopCommand.Execute(null); break;
            case HotkeyAction.Next: _playback.PlayNextCommand.Execute(null); break;
            case HotkeyAction.SeekBack: _playback.SeekBy(-5); break;
            case HotkeyAction.SeekForward: _playback.SeekBy(+5); break;
            case HotkeyAction.Repeat: _playback.CycleRepeatCommand.Execute(null); break;
            case HotkeyAction.Shuffle: _playback.ToggleShuffleCommand.Execute(null); break;
            case HotkeyAction.Mute: _playback.ToggleMuteCommand.Execute(null); break;
            case HotkeyAction.VolumeUp: _playback.ChangeVolume(+0.05); break;
            case HotkeyAction.VolumeDown: _playback.ChangeVolume(-0.05); break;
            case HotkeyAction.MiniPlayer:
            case HotkeyAction.Hud:
            case HotkeyAction.HudClear:
            case HotkeyAction.HudClickThrough:
                WindowAction?.Invoke(action);
                break;
        }
    }

    private static uint? VirtualKey(string name)
    {
        if (!Enum.TryParse<Key>(name, ignoreCase: true, out var key) || key == Key.None) return null;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        return vk == 0 ? null : (uint)vk;
    }

    private void UnregisterAll()
    {
        if (_window is null) return;
        foreach (var id in _registered.Keys) UnregisterHotKey(_window.Handle, id);
        _registered.Clear();
    }

    public void Dispose()
    {
        UnregisterAll();
        _window?.Dispose();
        _window = null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>How hotkeys read on screen.</summary>
public static class HotkeyText
{
    public static string ActionName(HotkeyAction action) => action switch
    {
        HotkeyAction.Previous => "이전 곡",
        HotkeyAction.PlayPause => "재생/일시정지",
        HotkeyAction.Stop => "정지",
        HotkeyAction.Next => "다음 곡",
        HotkeyAction.SeekBack => "뒤로 가기 (5초)",
        HotkeyAction.SeekForward => "앞으로 가기 (5초)",
        HotkeyAction.Repeat => "반복 재생",
        HotkeyAction.Shuffle => "랜덤 재생",
        HotkeyAction.Mute => "음소거",
        HotkeyAction.VolumeUp => "소리 높임",
        HotkeyAction.VolumeDown => "소리 낮춤",
        HotkeyAction.MiniPlayer => "미니 플레이어 전환",
        HotkeyAction.Hud => "HUD 전환",
        HotkeyAction.HudClear => "HUD 클리어 모드 켜기/끄기",
        HotkeyAction.HudClickThrough => "HUD 마우스 통과 켜기/끄기",
        _ => action.ToString(),
    };

    public static string Modifiers(HotkeyModifiers modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        return string.Join("+", parts);
    }

    /// <summary>A key's name as shown ("PageDown", "BackSpace", "5", "Num 5", "F9").</summary>
    public static string Key(string name)
    {
        if (!Enum.TryParse<System.Windows.Input.Key>(name, true, out var key)) return name;
        return key switch
        {
            System.Windows.Input.Key.PageUp => "PageUp",
            System.Windows.Input.Key.PageDown => "PageDown",
            System.Windows.Input.Key.Back => "BackSpace",
            System.Windows.Input.Key.Return => "Enter",
            System.Windows.Input.Key.Capital => "CapsLock",
            >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 => ((int)(key - System.Windows.Input.Key.D0)).ToString(),
            >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9 => $"Num {(int)(key - System.Windows.Input.Key.NumPad0)}",
            System.Windows.Input.Key.OemPlus => "+",
            System.Windows.Input.Key.OemMinus => "-",
            System.Windows.Input.Key.OemComma => ",",
            System.Windows.Input.Key.OemPeriod => ".",
            _ => key.ToString(),
        };
    }

    /// <summary>The name a key is saved under (one name for keys WPF knows by two).</summary>
    public static string Save(System.Windows.Input.Key key) => key switch
    {
        System.Windows.Input.Key.PageUp => "PageUp",
        System.Windows.Input.Key.PageDown => "PageDown",
        System.Windows.Input.Key.Return => "Enter",
        _ => key.ToString(),
    };

    public static string Combination(HotkeyBinding binding) => $"{Modifiers(binding.Modifiers)}+{Key(binding.Key)}";
}
