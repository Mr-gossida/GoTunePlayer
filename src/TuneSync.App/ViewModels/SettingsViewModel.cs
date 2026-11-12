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

    private readonly Network.NetworkLibrary _network;

    public SettingsViewModel(AppSettings settings, MergeViewModel merge, PlaybackViewModel playback, Network.NetworkLibrary network)
    {
        _settings = settings;
        _merge = merge;
        _network = network;
        Playback = playback;
        Library = playback.Library;
        _theme = settings.Theme;
        _quarantineFolderName = settings.MergeQuarantineFolderName;
        _screenSaverEnabled = settings.ScreenSaverEnabled;
        _screenSaverMinutes = settings.ScreenSaverMinutes;
        _cacheMegabytes = settings.StreamCacheMegabytes;
        _startWithWindows = AutoStart.IsEnabled();
        RefreshNetwork();
    }

    // --- network ---

    public System.Collections.ObjectModel.ObservableCollection<RemoteSource> NetworkSources { get; } = [];

    public bool HasNetworkSources => NetworkSources.Count > 0;

    /// <summary>Called when the settings page is shown (and after changes).</summary>
    public void RefreshNetwork()
    {
        NetworkSources.Clear();
        foreach (var source in _network.Sources) NetworkSources.Add(source);
        OnPropertyChanged(nameof(HasNetworkSources));
        _ = RefreshCacheUsageAsync();
    }

    [RelayCommand]
    private async Task AddNetworkSourceAsync()
    {
        await Library.AddNetworkLocationCommand.ExecuteAsync(null);
        RefreshNetwork();
    }

    [RelayCommand]
    private async Task RemoveNetworkSourceAsync(RemoteSource? source)
    {
        if (source is null) return;
        var confirmed = await Dialogs.ConfirmAsync("네트워크 위치 제거",
            $"'{source.Name}'을(를) 제거합니다.\n이 위치에서 추가한 음악은 재생 목록에서 빠지고, 저장된 비밀번호도 지워집니다.\n서버의 파일은 그대로 남습니다.",
            "제거");
        if (!confirmed) return;
        _network.Remove(source.Id);
        await Library.RemoveSourceAsync(source.Id);
        RefreshNetwork();
        Toast.Success("네트워크 위치를 제거했습니다", source.Name);
    }

    [ObservableProperty]
    private int _cacheMegabytes;

    public IReadOnlyList<Choice<int>> CacheSizeOptions { get; } =
    [
        new(1024, "1 GB"),
        new(2048, "2 GB"),
        new(5120, "5 GB"),
        new(10240, "10 GB"),
        new(20480, "20 GB"),
    ];

    [ObservableProperty]
    private string _cacheUsageText = "";

    partial void OnCacheMegabytesChanged(int value)
    {
        _settings.StreamCacheMegabytes = value;
        AppSettingsStore.Save(_settings);
        Task.Run(_network.Cache.Trim).ContinueWith(_ => RefreshCacheUsageAsync(), TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task RefreshCacheUsageAsync()
    {
        var used = await Task.Run(() => _network.Cache.UsedBytes);
        CacheUsageText = used < 1024 * 1024 ? "비어 있음" : $"{used / 1024d / 1024d / 1024d:0.##} GB 사용 중";
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await Task.Run(_network.Cache.Clear);
        await RefreshCacheUsageAsync();
        Toast.Info("캐시를 비웠습니다", "네트워크 음악은 다음에 재생할 때 다시 내려받습니다.");
    }

    [RelayCommand]
    private static void OpenCacheFolder()
    {
        Directory.CreateDirectory(Network.StreamingCache.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Network.StreamingCache.Folder}\"") { UseShellExecute = true });
    }

    // --- start with Windows ---

    [ObservableProperty]
    private bool _startWithWindows;

    partial void OnStartWithWindowsChanged(bool value)
    {
        try
        {
            AutoStart.Set(value);
            _settings.StartWithWindows = value;
            AppSettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Toast.Error("자동 실행을 설정하지 못했습니다", ex.Message);
        }
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
