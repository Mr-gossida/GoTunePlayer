using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppSettings _settings;
    private readonly MergeViewModel _merge;

    public SettingsViewModel(AppSettings settings, MergeViewModel merge, PlaybackViewModel playback)
    {
        _settings = settings;
        _merge = merge;
        Playback = playback;
        Library = playback.Library;
        _theme = settings.Theme;
        _quarantineFolderName = settings.MergeQuarantineFolderName;
        _screenSaverEnabled = settings.ScreenSaverEnabled;
        _screenSaverMinutes = settings.ScreenSaverMinutes;
    }

    // --- screen saver ---

    [ObservableProperty]
    private bool _screenSaverEnabled;

    [ObservableProperty]
    private int _screenSaverMinutes;

    public IReadOnlyList<Choice<int>> ScreenSaverDelayOptions { get; } =
    [
        new(1, "1분 뒤"),
        new(3, "3분 뒤"),
        new(5, "5분 뒤"),
        new(10, "10분 뒤"),
        new(15, "15분 뒤"),
        new(30, "30분 뒤"),
    ];

    partial void OnScreenSaverEnabledChanged(bool value)
    {
        _settings.ScreenSaverEnabled = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnScreenSaverMinutesChanged(int value)
    {
        _settings.ScreenSaverMinutes = value;
        AppSettingsStore.Save(_settings);
    }

    [RelayCommand]
    private static void PreviewScreenSaver() => ScreenSaver.Show();

    public IReadOnlyList<Choice<AppTheme>> ThemeOptions { get; } =
    [
        new(AppTheme.System, "Windows 설정 따르기"),
        new(AppTheme.Light, "라이트"),
        new(AppTheme.Dark, "다크"),
    ];

    /// <summary>For playback options (the switch binds straight to it, which also saves).</summary>
    public PlaybackViewModel Playback { get; }

    /// <summary>For library options (폴더·파일을 추가할 때).</summary>
    public LibraryViewModel Library { get; }

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private string _quarantineFolderName;

    public string VersionText =>
        $"버전 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

    public static string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TuneSync");

    partial void OnThemeChanged(AppTheme value)
    {
        _settings.Theme = value;
        AppSettingsStore.Save(_settings);
        ThemeHelper.Apply(value, Application.Current.MainWindow);
    }

    partial void OnQuarantineFolderNameChanged(string value)
    {
        var name = value.Trim();
        if (name.Length == 0) return;
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            Toast.Warning("사용할 수 없는 이름", "폴더 이름에 \\ / : * ? \" < > | 문자는 쓸 수 없습니다.");
            return;
        }

        _settings.MergeQuarantineFolderName = name;
        AppSettingsStore.Save(_settings);
        _merge.ReloadQuarantineFolderName();
    }

    [RelayCommand]
    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true });
    }
}
