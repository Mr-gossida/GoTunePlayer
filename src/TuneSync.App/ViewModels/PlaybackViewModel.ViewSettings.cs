using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

// Saved choices about how the player looks: which panels show, the seek bar and spectrum looks,
// and the play list's view.
public sealed partial class PlaybackViewModel
{
    /// <summary>The queue panel on the right of the full player.</summary>
    [ObservableProperty]
    private bool _isQueueVisible;

    /// <summary>Lyrics next to the cover; off shows a larger cover alone.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLyricsAreaShown))]
    private bool _isLyricsVisible;

    /// <summary>The equalizer is showing in the lyrics' place on the 플레이 page (set by the page).
    /// Not the lyrics setting: the 가사 button stays as the user left it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLyricsAreaShown))]
    private bool _isEqualizerShown;

    /// <summary>The area beside the cover is showing: the lyrics, or the equalizer in their place.</summary>
    public bool IsLyricsAreaShown => IsLyricsVisible || IsEqualizerShown;

    [ObservableProperty]
    private bool _isMiniTopmost;

    [ObservableProperty]
    private bool _isMiniQueueExpanded;

    partial void OnIsMiniQueueExpandedChanged(bool value) { _settings.MiniQueueExpanded = value; AppSettingsStore.Save(_settings); }

    [RelayCommand]
    private void ToggleMiniQueue() => IsMiniQueueExpanded = !IsMiniQueueExpanded;

    /// <summary>For the mini player's own queue list.</summary>
    public LibraryViewModel Library => _library;

    partial void OnIsQueueVisibleChanged(bool value) { _settings.ShowQueue = value; AppSettingsStore.Save(_settings); }
    partial void OnIsLyricsVisibleChanged(bool value) { _settings.ShowLyrics = value; AppSettingsStore.Save(_settings); }
    partial void OnIsMiniTopmostChanged(bool value) { _settings.MiniTopmost = value; AppSettingsStore.Save(_settings); }

    [RelayCommand]
    private void ToggleQueue() => IsQueueVisible = !IsQueueVisible;

    [RelayCommand]
    private void ToggleLyrics() => IsLyricsVisible = !IsLyricsVisible;

    [RelayCommand]
    private void ToggleMiniTopmost() => IsMiniTopmost = !IsMiniTopmost;

    // ---- seek bar and spectrum looks (saved) ----

    public SeekBarStyle SeekBarStyle
    {
        get => _settings.SeekBarStyle;
        set
        {
            if (_settings.SeekBarStyle == value) return;
            _settings.SeekBarStyle = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
            PickSeekLook();
        }
    }

    /// <summary>The seek bar's look right now: the chosen one, or with 임의 one picked per song.</summary>
    [ObservableProperty]
    private SeekBarStyle _seekLook;

    /// <summary>The seek bar looks in the order the settings list them (임의 last).</summary>
    public static readonly SeekBarStyle[] SeekBarStyleOrder =
        [SeekBarStyle.Wave, SeekBarStyle.Flame, SeekBarStyle.Neon, SeekBarStyle.Bars, SeekBarStyle.Fountain, SeekBarStyle.Rain, SeekBarStyle.Aurora,
         SeekBarStyle.Sparkle, SeekBarStyle.Fireworks, SeekBarStyle.Fireflies, SeekBarStyle.Electric, SeekBarStyle.Petals, SeekBarStyle.Notes,
         SeekBarStyle.Random, SeekBarStyle.None];

    private void PickSeekLook()
    {
        if (SeekBarStyle != SeekBarStyle.Random)
        {
            SeekLook = SeekBarStyle;
            return;
        }
        // A different one from the last song's, so the change shows.
        var choices = SeekBarStyleOrder.Where(l => l is not (SeekBarStyle.Random or SeekBarStyle.None) && l != SeekLook).ToArray();
        SeekLook = choices[Random.Shared.Next(choices.Length)];
    }

    public static string SeekBarStyleName(SeekBarStyle style) => style switch
    {
        SeekBarStyle.Wave => "물결",
        SeekBarStyle.Flame => "불꽃",
        SeekBarStyle.Neon => "네온",
        SeekBarStyle.Bars => "파형 막대",
        SeekBarStyle.Fountain => "분수",
        SeekBarStyle.Rain => "빗방울",
        SeekBarStyle.Aurora => "오로라",
        SeekBarStyle.Sparkle => "별빛",
        SeekBarStyle.Random => "랜덤 (곡마다)",
        SeekBarStyle.None => "없음",
        SeekBarStyle.Fireworks => "불꽃놀이",
        SeekBarStyle.Fireflies => "반딧불",
        SeekBarStyle.Electric => "테슬라 코일",
        SeekBarStyle.Petals => "꽃잎",
        SeekBarStyle.Notes => "음표",
        _ => style.ToString(),
    };

    public SpectrumStyle SpectrumStyle
    {
        get => _settings.SpectrumStyle;
        set
        {
            if (_settings.SpectrumStyle == value) return;
            _settings.SpectrumStyle = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
            PickSpectrumLook();
        }
    }

    /// <summary>The spectrum's look right now: the chosen one, or with 랜덤 one picked per song.</summary>
    [ObservableProperty]
    private SpectrumStyle _spectrumLook;

    /// <summary>The spectrum looks in the order the settings list them and a click cycles through.</summary>
    public static readonly SpectrumStyle[] SpectrumStyleOrder =
    [
        SpectrumStyle.Off, SpectrumStyle.Led, SpectrumStyle.Bars, SpectrumStyle.Wave, SpectrumStyle.Mirror, SpectrumStyle.Dots,
        SpectrumStyle.Neon, SpectrumStyle.Rainbow, SpectrumStyle.Reflect, SpectrumStyle.Pins, SpectrumStyle.Scope, SpectrumStyle.Random,
    ];

    private static readonly SpectrumStyle[] RandomLooks =
        [.. SpectrumStyleOrder.Where(s => s is not SpectrumStyle.Off and not SpectrumStyle.Random)];

    private void PickSpectrumLook()
    {
        if (SpectrumStyle != SpectrumStyle.Random)
        {
            SpectrumLook = SpectrumStyle;
            return;
        }
        // A different one from what's showing, so a new song (or the click) visibly changes it.
        var choices = RandomLooks.Where(l => l != SpectrumLook).ToArray();
        SpectrumLook = choices[Random.Shared.Next(choices.Length)];
    }

    /// <summary>Clicking the spectrum steps through <see cref="SpectrumStyleOrder"/>, back to 없음 after 랜덤.</summary>
    public void CycleSpectrumStyle()
    {
        var all = SpectrumStyleOrder;
        SpectrumStyle = all[(Array.IndexOf(all, SpectrumStyle) + 1) % all.Length];
    }

    public static string SpectrumStyleName(SpectrumStyle style) => style switch
    {
        SpectrumStyle.Off => "없음",
        SpectrumStyle.Led => "LED 막대",
        SpectrumStyle.Bars => "둥근 막대",
        SpectrumStyle.Wave => "물결",
        SpectrumStyle.Mirror => "대칭 막대",
        SpectrumStyle.Dots => "도트",
        SpectrumStyle.Neon => "네온 선",
        SpectrumStyle.Rainbow => "무지개 막대",
        SpectrumStyle.Reflect => "반사 막대",
        SpectrumStyle.Pins => "핀",
        SpectrumStyle.Scope => "오실로스코프",
        SpectrumStyle.Random => "랜덤 (곡마다)",
        _ => style.ToString(),
    };

    // ---- how the play list looks (saved) ----

    public QueueViewMode QueueViewMode
    {
        get => _settings.QueueViewMode;
        set
        {
            if (_settings.QueueViewMode == value) return;
            _settings.QueueViewMode = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>Rows lead with the artist instead of the title.</summary>
    public bool QueueArtistFirst
    {
        get => _settings.QueueArtistFirst;
        set
        {
            if (_settings.QueueArtistFirst == value) return;
            _settings.QueueArtistFirst = value;
            AppSettingsStore.Save(_settings);
            LibraryFileViewModel.ArtistFirst = value;
            foreach (var file in _library.Files) file.NotifyNameOrder();
            OnPropertyChanged();
        }
    }

    public double QueueTileSize
    {
        get => _settings.QueueTileSize;
        set
        {
            value = Math.Clamp(value, 96, 200);
            if (Math.Abs(_settings.QueueTileSize - value) < 0.5) return;
            _settings.QueueTileSize = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }

    /// <summary>The play list panel's width; saved when a resize ends (not on every pixel).</summary>
    public double QueueWidth
    {
        get => _settings.QueueWidth;
        set
        {
            if (Math.Abs(_settings.QueueWidth - value) < 0.5) return;
            _settings.QueueWidth = value;
            AppSettingsStore.Save(_settings);
        }
    }

    /// <summary>설정 › 인트로 진행 게이지: whether the intro shows its progress bar.</summary>
    public bool ShowIntroGauge
    {
        get => _settings.ShowIntroGauge;
        set
        {
            if (_settings.ShowIntroGauge == value) return;
            _settings.ShowIntroGauge = value;
            AppSettingsStore.Save(_settings);
            OnPropertyChanged();
        }
    }
}
