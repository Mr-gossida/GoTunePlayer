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

    public SettingsViewModel(AppSettings settings, MergeViewModel merge, PlaybackViewModel playback, Network.NetworkLibrary network, EqualizerViewModel equalizer,
        HotkeysViewModel hotkeys, OutputViewModel output, HudViewModel hud)
    {
        Hud = hud;
        Hotkeys = hotkeys;
        Output = output;
        Equalizer = equalizer;
        _settings = settings;
        _merge = merge;
        _network = network;
        Playback = playback;
        Library = playback.Library;
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.BackgroundEffect)) OnPropertyChanged(nameof(IsBackgroundRandom));
        };
        _theme = settings.Theme;
        _quarantineFolderName = settings.MergeQuarantineFolderName;
        _screenSaverEnabled = settings.ScreenSaverEnabled;
        _screenSaverMinutes = settings.ScreenSaverMinutes;
        _screenSaverExit = settings.ScreenSaverExit;
        _screenSaverSameLook = settings.ScreenSaverSameLook;
        _minimizeToMini = settings.MinimizeToMini;
        _closeAction = settings.CloseAction;
        _lyricsSyncUseGpu = settings.LyricsSyncUseGpu;
        _lyricsSyncCpuUsage = settings.LyricsSyncCpuUsage;
        _cacheMegabytes = settings.StreamCacheMegabytes;
        _startWithWindows = AutoStart.IsEnabled();
        RefreshNetwork();
    }

    /// <summary>설정 › 단축키.</summary>
    public HotkeysViewModel Hotkeys { get; }

    // --- fonts: 설정 › 일반 › 앱 글꼴, 설정 › 재생 › 가사 글꼴 (AppFonts) ---

    private IReadOnlyList<AppFonts.Option>? _appFontOptions, _lyricsFontOptions;

    /// <summary>Windows' UI font first, then every installed font (each shown in itself).</summary>
    public IReadOnlyList<AppFonts.Option> AppFontOptions => _appFontOptions ??=
        [new("", $"Windows 기본 ({SystemFonts.MessageFontFamily.Source})", SystemFonts.MessageFontFamily), .. AppFonts.InstalledFonts()];

    /// <summary>"앱 글꼴과 같게" first, then every installed font.</summary>
    public IReadOnlyList<AppFonts.Option> LyricsFontOptions => _lyricsFontOptions ??=
        [new("", "앱 글꼴과 같게", SystemFonts.MessageFontFamily), .. AppFonts.InstalledFonts()];

    public string AppFontFamily
    {
        get => _settings.AppFontFamily;
        set
        {
            value ??= "";
            if (_settings.AppFontFamily == value) return;
            _settings.AppFontFamily = value;
            AppSettingsStore.Save(_settings);
            AppFonts.Apply(_settings); // the whole app changes at once: that's the preview too
            OnPropertyChanged();
        }
    }

    public string LyricsFontFamily
    {
        get => _settings.LyricsFontFamily;
        set
        {
            value ??= "";
            if (_settings.LyricsFontFamily == value) return;
            _settings.LyricsFontFamily = value;
            AppSettingsStore.Save(_settings);
            AppFonts.Apply(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>설정 › 음향 › 출력.</summary>
    public OutputViewModel Output { get; }

    /// <summary>설정 › 일반 › HUD.</summary>
    public HudViewModel Hud { get; }

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

    /// <summary>위치 추가: the location only (connection checked, then saved). Its music folders are
    /// chosen afterwards with "폴더 추가" on it.</summary>
    [RelayCommand]
    private void AddNetworkSource()
    {
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow;
        var before = _network.Sources.Select(s => s.Id).ToHashSet();
        var added = Network.RemoteSourceWindow.AddLocation(owner, _network);
        RefreshNetwork();
        if (added is null) return;
        if (before.Contains(added.Id))
            Toast.Info($"'{added.Name}'은(는) 이미 있는 위치라 정보를 새로 고쳤습니다", "같은 주소와 사용자 이름의 위치는 하나로 둡니다.");
        else
            Toast.Success($"'{added.Name}' 위치를 추가했습니다", "오른쪽 '폴더 추가'로 이 위치에서 들을 음악 폴더를 고르세요.");
    }

    /// <summary>폴더 추가 on a saved location: connects with its saved details and goes straight to
    /// its folders; the chosen one joins the library.</summary>
    [RelayCommand]
    private async Task AddFolderFromSourceAsync(RemoteSource? source)
    {
        if (source is null) return;
        await Library.AddFromSavedSourceCommand.ExecuteAsync(source);
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
    private static void OpenCacheFolder() => SystemTools.OpenFolder(Network.StreamingCache.Folder, create: true);

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

    // --- what plays when the app starts ---

    public IReadOnlyList<Choice<StartupResume>> StartupResumeOptions { get; } =
    [
        new(StartupResume.LastPosition, "마지막으로 듣던 위치부터"),
        new(StartupResume.SongStart, "마지막 곡을 처음부터"),
        new(StartupResume.Nothing, "이어 듣지 않음"),
    ];

    public StartupResume StartupResume
    {
        get => _settings.StartupResume;
        set
        {
            if (_settings.StartupResume == value) return;
            _settings.StartupResume = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public bool AutoPlayOnStartup
    {
        get => _settings.AutoPlayOnStartup;
        set
        {
            if (_settings.AutoPlayOnStartup == value) return;
            _settings.AutoPlayOnStartup = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice<SeekBarStyle>> SeekBarStyleOptions { get; } =
        [.. PlaybackViewModel.SeekBarStyleOrder.Select(s => new Choice<SeekBarStyle>(s, PlaybackViewModel.SeekBarStyleName(s)))];

    public SeekBarStyle SeekBarStyle
    {
        get => Playback.SeekBarStyle;
        set
        {
            Playback.SeekBarStyle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSeekBarRandom));
        }
    }

    // ---- 랜덤's pools: with 랜덤 chosen, which looks it may pick (checkboxes under the card) ----

    public bool IsSeekBarRandom => SeekBarStyle == SeekBarStyle.Random;
    public bool IsSpectrumRandom => SpectrumStyle == SpectrumStyle.Random;
    public bool IsBackgroundRandom => Playback.BackgroundEffect == BackgroundEffect.Random;

    private IReadOnlyList<RandomPoolItem>? _seekBarRandomPool, _spectrumRandomPool, _backgroundRandomPool;

    public IReadOnlyList<RandomPoolItem> SeekBarRandomPool => _seekBarRandomPool ??=
        Pool(PlaybackViewModel.RandomSeekLooks, PlaybackViewModel.SeekBarStyleName, _settings.SeekBarRandomExcluded);

    public IReadOnlyList<RandomPoolItem> SpectrumRandomPool => _spectrumRandomPool ??=
        Pool(PlaybackViewModel.SpectrumStyleOrder.Where(s => s is not (SpectrumStyle.Off or SpectrumStyle.Random)).ToArray(),
            PlaybackViewModel.SpectrumStyleName, _settings.SpectrumRandomExcluded);

    public IReadOnlyList<RandomPoolItem> BackgroundRandomPool => _backgroundRandomPool ??=
        Pool(PlaybackViewModel.RandomBackgrounds, b => BackgroundEffectOptions.First(o => o.Value == b).Label, _settings.BackgroundRandomExcluded);

    /// <summary>The checkboxes for one pool; ticking one saves which are left out.</summary>
    private IReadOnlyList<RandomPoolItem> Pool<T>(IReadOnlyList<T> all, Func<T, string> name, List<T> excluded) where T : notnull
    {
        var items = new List<RandomPoolItem>();
        foreach (var look in all)
            items.Add(new RandomPoolItem(look, name(look), !excluded.Contains(look), () =>
            {
                excluded.Clear();
                excluded.AddRange(items.Where(i => !i.IsChecked).Select(i => (T)i.Value));
                AppSettingsStore.Save(_settings);
                Playback.RandomPoolChanged();
            }));
        return items;
    }

    /// <summary>The pool's "모두 선택" button: checks every look (or, all checked already, clears them).</summary>
    [RelayCommand]
    private void CheckAll(IReadOnlyList<RandomPoolItem>? pool)
    {
        if (pool is null) return;
        var all = !pool.All(i => i.IsChecked); // all checked already: the button clears them instead
        foreach (var item in pool) item.IsChecked = all;
    }

    public IReadOnlyList<Choice<SpectrumStyle>> SpectrumStyleOptions { get; } =
        [.. PlaybackViewModel.SpectrumStyleOrder.Select(s => new Choice<SpectrumStyle>(s, PlaybackViewModel.SpectrumStyleName(s)))];

    public SpectrumStyle SpectrumStyle
    {
        get => Playback.SpectrumStyle;
        set
        {
            Playback.SpectrumStyle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsSpectrumRandom));
        }
    }

    public bool ShowIntroGauge
    {
        get => Playback.ShowIntroGauge;
        set
        {
            if (Playback.ShowIntroGauge == value) return;
            Playback.ShowIntroGauge = value;
            OnPropertyChanged();
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

    /// <summary>The screen saver card's line: "켜짐 · 5분 뒤 · ESC 키로만 종료".</summary>
    public string ScreenSaverSummary => !ScreenSaverEnabled ? "꺼짐"
        : $"켜짐 · {ScreenSaverDelayOptions.FirstOrDefault(o => o.Value == ScreenSaverMinutes)?.Label ?? $"{ScreenSaverMinutes}분 뒤"} · "
          + (ScreenSaverExit == ScreenSaverExit.EscapeKey ? "ESC 키로만 종료" : "마우스·키보드로 종료");

    partial void OnScreenSaverEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ScreenSaverSummary));
        _settings.ScreenSaverEnabled = value;
        AppSettingsStore.Save(_settings);
    }

    [ObservableProperty]
    private bool _minimizeToMini;

    partial void OnMinimizeToMiniChanged(bool value)
    {
        _settings.MinimizeToMini = value;
        AppSettingsStore.Save(_settings);
    }

    [ObservableProperty]
    private CloseAction _closeAction;

    public IReadOnlyList<Choice<CloseAction>> CloseActionOptions { get; } =
    [
        new(CloseAction.Exit, "프로그램 종료"),
        new(CloseAction.Tray, "시스템 트레이로 숨기기"),
    ];

    partial void OnCloseActionChanged(CloseAction value)
    {
        _settings.CloseAction = value;
        AppSettingsStore.Save(_settings);
        // the tray icon appears (or goes) right away, not on the next close
        (Application.Current?.MainWindow as ShellWindow)?.UpdateTray();
    }

    // ---- 가사 싱크 (applies from the next "자동으로 맞추기") ----

    [ObservableProperty]
    private bool _lyricsSyncUseGpu;

    /// <summary>Where it runs. "자동" is the graphics card when one works and the CPU otherwise -
    /// the same as asking for the graphics card, which falls back the same way - so it's the only
    /// other choice besides the CPU alone.</summary>
    public IReadOnlyList<Choice<bool>> LyricsSyncDeviceOptions { get; } =
    [
        new(true, "자동 (그래픽 카드 우선)"),
        new(false, "CPU만"),
    ];

    partial void OnLyricsSyncUseGpuChanged(bool value)
    {
        _settings.LyricsSyncUseGpu = value;
        AppSettingsStore.Save(_settings);
    }

    [ObservableProperty]
    private LyricsSyncCpuUsage _lyricsSyncCpuUsage;

    public IReadOnlyList<Choice<LyricsSyncCpuUsage>> LyricsSyncCpuUsageOptions { get; } =
    [
        new(LyricsSyncCpuUsage.Low, "낮음 (약 25%)"),
        new(LyricsSyncCpuUsage.Normal, "보통 (약 50%)"),
        new(LyricsSyncCpuUsage.Max, "최대 (가장 빠름)"),
    ];

    partial void OnLyricsSyncCpuUsageChanged(LyricsSyncCpuUsage value)
    {
        _settings.LyricsSyncCpuUsage = value;
        AppSettingsStore.Save(_settings);
    }

    [ObservableProperty]
    private ScreenSaverExit _screenSaverExit;

    /// <summary>설정 › 화면 보호기 › 배경 효과: which light (bound to Playback.BackgroundEffect).</summary>
    public IReadOnlyList<Choice<BackgroundEffect>> BackgroundEffectOptions { get; } =
    [
        new(BackgroundEffect.Random, "무작위"),
        new(BackgroundEffect.Aurora, "오로라"),
        new(BackgroundEffect.Caustics, "물빛"),
        new(BackgroundEffect.Bokeh, "빛망울"),
        new(BackgroundEffect.Nebula, "성운"),
        new(BackgroundEffect.Lava, "라바 램프"),
        new(BackgroundEffect.CoverGradient, "그라데이션"),
        new(BackgroundEffect.Rain, "비"),
        new(BackgroundEffect.Warp, "워프"),
    ];

    /// <summary>설정 › 화면 보호기 › 배경 효과: how it moves (bound to Playback.AuroraMode).</summary>
    public IReadOnlyList<Choice<AuroraMode>> AuroraModeOptions { get; } =
    [
        new(AuroraMode.On, "켜기"),
        new(AuroraMode.Saver, "절전 (천천히 움직임)"),
        new(AuroraMode.Off, "끄기"),
    ];

    [ObservableProperty]
    private bool _screenSaverSameLook;

    /// <summary>설정 › 화면 보호기 › 여러 모니터의 배경 (only shown while the background is 랜덤).</summary>
    public IReadOnlyList<Choice<bool>> ScreenSaverSameLookOptions { get; } =
    [
        new(true, "모두 같게"),
        new(false, "모니터마다 다르게"),
    ];

    partial void OnScreenSaverSameLookChanged(bool value)
    {
        _settings.ScreenSaverSameLook = value;
        AppSettingsStore.Save(_settings);
    }

    public IReadOnlyList<Choice<ScreenSaverExit>> ScreenSaverExitOptions { get; } =
    [
        new(ScreenSaverExit.AnyInput, "마우스·키보드 조작 시"),
        new(ScreenSaverExit.EscapeKey, "ESC 키를 누를 때만"),
    ];

    partial void OnScreenSaverExitChanged(ScreenSaverExit value)
    {
        OnPropertyChanged(nameof(ScreenSaverSummary));
        _settings.ScreenSaverExit = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnScreenSaverMinutesChanged(int value)
    {
        OnPropertyChanged(nameof(ScreenSaverSummary));
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

    /// <summary>설정 › 음향.</summary>
    public EqualizerViewModel Equalizer { get; }

    /// <summary>For library options (폴더·파일을 추가할 때).</summary>
    public LibraryViewModel Library { get; }

    [ObservableProperty]
    private AppTheme _theme;

    [ObservableProperty]
    private string _quarantineFolderName;

    public string VersionText =>
        $"버전 {Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"}";

    public static string DataFolder => AppDataFolder.Path;

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
    private static void OpenDataFolder() => SystemTools.OpenFolder(DataFolder, create: true);
}
