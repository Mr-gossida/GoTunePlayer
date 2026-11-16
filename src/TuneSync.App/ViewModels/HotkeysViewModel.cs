using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>설정 › 단축키: the switch for all of them, the media keys, and a row per action.
/// Every change is registered with Windows at once (so a key another program holds shows up
/// right there) and saved.</summary>
public sealed partial class HotkeysViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private bool _loading;

    public HotkeysViewModel(AppSettings settings, GlobalHotkeys hotkeys)
    {
        _settings = settings;
        Service = hotkeys;
        hotkeys.Applied += ShowProblems;
        Load();
    }

    public GlobalHotkeys Service { get; }

    public ObservableCollection<HotkeyRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private bool _mediaKeys;

    public IReadOnlyList<Choice<HotkeyModifiers>> ModifierOptions { get; } =
    [
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "Ctrl+Alt"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Shift, "Ctrl+Shift"),
        new(HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Alt+Shift"),
        new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "Ctrl+Alt+Shift"),
    ];

    partial void OnEnabledChanged(bool value)
    {
        if (_loading) return;
        _settings.Hotkeys.Enabled = value;
        Changed();
    }

    partial void OnMediaKeysChanged(bool value)
    {
        if (_loading) return;
        _settings.Hotkeys.MediaKeys = value;
        Changed();
    }

    [RelayCommand]
    private void Reset()
    {
        _settings.Hotkeys.Reset();
        Load();
        Changed();
    }

    private void Load()
    {
        _loading = true;
        Enabled = _settings.Hotkeys.Enabled;
        MediaKeys = _settings.Hotkeys.MediaKeys;
        Rows.Clear();
        foreach (var binding in _settings.Hotkeys.Bindings) Rows.Add(new HotkeyRowViewModel(binding, this));
        _loading = false;
        ShowProblems();
    }

    /// <summary>A row was changed: register again, and keep it.</summary>
    internal void Changed()
    {
        Service.Apply();
        AppSettingsStore.Save(_settings);
    }

    private void ShowProblems()
    {
        foreach (var row in Rows) row.Problem = Service.Problem(row.Action);
        HasProblems = Rows.Any(r => r.HasProblem);
    }

    /// <summary>Some key isn't working (the page then says why and what to do).</summary>
    [ObservableProperty]
    private bool _hasProblems;
}

public sealed partial class HotkeyRowViewModel : ObservableObject
{
    private readonly HotkeyBinding _binding;
    private readonly HotkeysViewModel _owner;

    public HotkeyRowViewModel(HotkeyBinding binding, HotkeysViewModel owner)
    {
        _binding = binding;
        _owner = owner;
        _enabled = binding.Enabled;
        _modifiers = binding.Modifiers;
        _key = binding.Key;
    }

    public HotkeyAction Action => _binding.Action;

    public string Name => HotkeyText.ActionName(Action);

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private HotkeyModifiers _modifiers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KeyText))]
    private string _key;

    public string KeyText => HotkeyText.Key(Key);

    /// <summary>Why this key isn't working (taken by another program, the same as another row),
    /// or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    public bool HasProblem => Problem is not null;

    private bool _setting;

    partial void OnEnabledChanged(bool value)
    {
        _binding.Enabled = value;
        if (!_setting) _owner.Changed();
    }

    partial void OnModifiersChanged(HotkeyModifiers value)
    {
        if (value == HotkeyModifiers.None) return;
        _binding.Modifiers = value;
        if (!_setting) _owner.Changed();
    }

    partial void OnKeyChanged(string value)
    {
        _binding.Key = value;
        if (!_setting) _owner.Changed();
    }

    /// <summary>A combination typed into the key box: its key, and its modifiers when any were held.</summary>
    public void Set(HotkeyModifiers modifiers, string key)
    {
        _setting = true;
        if (modifiers != HotkeyModifiers.None) Modifiers = modifiers;
        Enabled = true; // a key typed in is a key wanted
        Key = key;
        _setting = false;
        _owner.Changed();
    }
}
