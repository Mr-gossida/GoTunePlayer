namespace TuneSync.Core.Models;

/// <summary>Everything that should survive a restart: the last folder picked on each page, plus
/// the rule/mode choices made there. Loaded once at startup and re-saved whenever one of these
/// changes - see <see cref="Services.AppSettingsStore"/>.</summary>
public sealed class AppSettings
{
    /// <summary>The music library is built from any number of watched folders (like a real media
    /// player), added/removed via the +/- buttons rather than a single "current folder".</summary>
    public List<LibraryFolderEntry> LibraryFolders { get; set; } = [];

    /// <summary>Songs taken out of the play list (재생 목록에서 빼기) although their folder is
    /// registered. The files themselves are untouched.</summary>
    public List<string> ExcludedSongs { get; set; } = [];

    /// <summary>What adding folders/files does to the existing list: ask each time, add to it, or
    /// replace it.</summary>
    public LibraryAddMode LibraryAddMode { get; set; } = LibraryAddMode.Ask;

    /// <summary>Network locations (WebDAV, FTP, SFTP, Samba). Passwords live in Windows
    /// Credential Manager, not here.</summary>
    public List<RemoteSource> RemoteSources { get; set; } = [];

    /// <summary>How much disk the streaming cache of network songs may use.</summary>
    public int StreamCacheMegabytes { get; set; } = 2048;

    /// <summary>Start TuneSync (minimized) when signing in to Windows.</summary>
    public bool StartWithWindows { get; set; }

    public string? LastMergeSourceFolder { get; set; }
    public string? LastMergeTargetFolder { get; set; }
    public MergeConflictResolution MergeConflictResolution { get; set; } = MergeConflictResolution.KeepLargerFile;
    public string MergeQuarantineFolderName { get; set; } = "_TuneSync_Quarantine";
    public bool MergeIncludeSubfolders { get; set; } = true;

    public string? LastRenameFolder { get; set; }
    public bool RenameIncludeSubfolders { get; set; }
    public bool RenameIsAlbumMode { get; set; }
    public RenameMode RenameMode { get; set; } = RenameMode.Auto;
    public bool MergeRemoveEmptyFolders { get; set; }
    public string RenameSingleTrackPattern { get; set; } = "{Artist} - {Title}";
    public string RenameAlbumTrackPattern { get; set; } = "{Track} {Title}";

    public bool AutoFillIsAutomaticMode { get; set; }

    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>Playback volume, 0..1.</summary>
    public double Volume { get; set; } = 0.8;

    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;

    public PlaylistSortKey PlaylistSort { get; set; } = PlaylistSortKey.FileName;
    public bool PlaylistSortDescending { get; set; }
    public PlaylistGroupKey PlaylistGroup { get; set; } = PlaylistGroupKey.None;

    /// <summary>Last main-window bounds; null until the window has been closed once.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Full player or one of the mini players; the app reopens in the last one used.</summary>
    public PlayerMode PlayerMode { get; set; } = PlayerMode.Full;

    public bool ShowQueue { get; set; } = true;

    /// <summary>How the play list is drawn: rows with covers, dense rows, or a cover grid.</summary>
    public QueueViewMode QueueViewMode { get; set; } = QueueViewMode.Standard;

    /// <summary>Play list rows lead with the artist instead of the title.</summary>
    public bool QueueArtistFirst { get; set; }

    /// <summary>Grid view: the wished-for tile width.</summary>
    public double QueueTileSize { get; set; } = 132;

    /// <summary>Width of the play list panel (dragging its edge).</summary>
    public double QueueWidth { get; set; } = 320;

    /// <summary>직접 정렬: the play list order arranged by dragging songs, by path.</summary>
    public List<string> CustomOrder { get; set; } = [];
    public bool ShowLyrics { get; set; } = true;

    /// <summary>What plays along on the seek bar.</summary>
    public SeekBarStyle SeekBarStyle { get; set; } = SeekBarStyle.Wave;

    /// <summary>Seek bar looks 무작위 doesn't pick (unchecked in 설정). Kept as the ones left out,
    /// so a look added later is in the pool from the start.</summary>
    public List<SeekBarStyle> SeekBarRandomExcluded { get; set; } = [];

    /// <summary>Spectrum looks 무작위 doesn't pick.</summary>
    public List<SpectrumStyle> SpectrumRandomExcluded { get; set; } = [];

    /// <summary>Background effects 무작위 doesn't pick.</summary>
    public List<BackgroundEffect> BackgroundRandomExcluded { get; set; } = [];

    /// <summary>The spectrum analyzer's look (under the song info and on the screen saver).</summary>
    public SpectrumStyle SpectrumStyle { get; set; } = SpectrumStyle.Led;

    /// <summary>The small bar showing how far a song's intro has played before the lyrics start.</summary>
    public bool ShowIntroGauge { get; set; } = true;

    /// <summary>The app's font family name (설정 › 일반 › 앱 글꼴); empty = Windows' UI font.</summary>
    public string AppFontFamily { get; set; } = "";

    /// <summary>The lyrics' font family name (설정 › 재생 › 가사 글꼴); empty = the app font.</summary>
    public string LyricsFontFamily { get; set; } = "";

    /// <summary>Mini player stays above other windows.</summary>
    public bool MiniTopmost { get; set; } = true;

    /// <summary>The vertical mini player's fold-out queue is open.</summary>
    public bool MiniQueueExpanded { get; set; }

    /// <summary>Last spot and width of each mini player.</summary>
    public WindowPlacement? MiniHorizontal { get; set; }
    public WindowPlacement? MiniVertical { get; set; }

    /// <summary>How tall the vertical mini player was dragged with its queue out (0 = default).</summary>
    public double MiniVerticalQueueHeight { get; set; }

    /// <summary>The song loaded when the app was last closed, and how far into it - the app
    /// reopens on it, paused at that spot.</summary>
    public string? LastFilePath { get; set; }
    public double LastPositionSeconds { get; set; }

    /// <summary>While music is playing, keep the display on and the PC awake (so the lyrics stay
    /// on screen). Released as soon as playback pauses or stops.</summary>
    public bool KeepScreenOnWhilePlaying { get; set; } = true;

    /// <summary>The moving light behind 음악 집중 모드 and the screen saver. Every step of it
    /// redraws the whole screen, which keeps an integrated GPU busy: Saver moves it less often,
    /// Off leaves the backdrop still.</summary>
    public AuroraMode AuroraMode { get; set; } = AuroraMode.On;

    /// <summary>Which moving light it is (the aurora, or another; all cost about the same).</summary>
    public BackgroundEffect BackgroundEffect { get; set; } = BackgroundEffect.Aurora;

    /// <summary>What the player opens with when the app starts.</summary>
    public StartupResume StartupResume { get; set; } = StartupResume.LastPosition;

    /// <summary>Start playing right away when the app opens (instead of waiting, paused).</summary>
    public bool AutoPlayOnStartup { get; set; }

    /// <summary>The player's screen saver: after this many minutes without input while music
    /// plays, a full-screen "now playing" view takes over.</summary>
    public bool ScreenSaverEnabled { get; set; } = true;
    public int ScreenSaverMinutes { get; set; } = 5;

    /// <summary>The full player's minimize button turns it into the mini player instead of
    /// sending it to the taskbar.</summary>
    public bool MinimizeToMini { get; set; }

    /// <summary>What the main window's close button (and the mini player's) does: quit, or hide
    /// to the notification area and keep playing.</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Exit;

    /// <summary>The one-time "still playing in the notification area" notice has been shown.</summary>
    public bool TrayNoticeShown { get; set; }

    /// <summary>How the screen saver is left: any input (default), or only the Esc key.</summary>
    public ScreenSaverExit ScreenSaverExit { get; set; } = ScreenSaverExit.AnyInput;

    /// <summary>With the background effect on 무작위 and several monitors: every screen of the
    /// screen saver shows the same picked effect (one light across them), or each picks its own.</summary>
    public bool ScreenSaverSameLook { get; set; } = true;

    /// <summary>With the background effect on 무작위: the screen saver (and focus mode) picks a new
    /// effect for every song, fading from one to the next; off, one pick each time it opens.</summary>
    public bool ScreenSaverLookPerSong { get; set; }

    /// <summary>"싱크 가사 만들기" uses the graphics card when there's one (about four times
    /// faster, and the CPU stays free); off, it runs on the CPU only.</summary>
    public bool LyricsSyncUseGpu { get; set; } = true;

    /// <summary>How much of the CPU "싱크 가사 만들기" may use for what runs on it.</summary>
    public LyricsSyncCpuUsage LyricsSyncCpuUsage { get; set; } = LyricsSyncCpuUsage.Normal;

    /// <summary>The equalizer and tone controls.</summary>
    public ToneSettings Tone { get; set; } = new();

    /// <summary>Keys that control the player from anywhere in Windows.</summary>
    public HotkeySettings Hotkeys { get; set; } = new();

    /// <summary>The playback device and how the sound goes to it.</summary>
    public OutputSettings Output { get; set; } = new();

    /// <summary>The always-on-top HUD.</summary>
    public HudSettings Hud { get; set; } = new();
}

public enum CloseAction
{
    /// <summary>Closing the window quits the app.</summary>
    Exit,
    /// <summary>Closing hides the player to the notification area; music keeps playing.</summary>
    Tray,
}

public enum LyricsSyncCpuUsage
{
    Low,
    Normal,
    Max,
}

/// <summary>The moving light behind 음악 집중 모드 and the screen saver (Effects/*.hlsl).</summary>
public enum BackgroundEffect
{
    /// <summary>오로라: flowing bands of light.</summary>
    Aurora,
    /// <summary>물빛: the caustic net of light from rippling water.</summary>
    Caustics,
    /// <summary>빛망울: soft out-of-focus lights drifting up.</summary>
    Bokeh,
    /// <summary>성운: clouds of colored gas and twinkling stars.</summary>
    Nebula,
    /// <summary>라바 램프: blobs of light rising, merging and parting.</summary>
    Lava,
    /// <summary>One of the others, picked anew each time the stage opens.</summary>
    Random,
    // (6 and 7 were 창문 빗방울 and 불꽃놀이, taken out: they couldn't look good at this cost. A
    // setting that still holds one is read as Aurora.)
    /// <summary>그라데이션:soft fields of the cover's colors slowly flowing into each other.</summary>
    CoverGradient = 8,
    /// <summary>비: slanting rain over a soft glow, landing on water with spreading rings.</summary>
    Rain = 9,
    /// <summary>워프: flying forward through stars streaming out of the middle of the screen.</summary>
    Warp = 10,
}

public enum AuroraMode
{
    /// <summary>Smooth: about 15 steps a second.</summary>
    On,
    /// <summary>About 8 steps a second: half the light's steps, a little less fluid.</summary>
    Saver,
    /// <summary>No light: the backdrop stands still and is redrawn only where something over it changes.</summary>
    Off,
}

public enum ScreenSaverExit
{
    /// <summary>Moving the mouse, clicking or any key brings the player back.</summary>
    AnyInput,
    /// <summary>Only Esc (or the on-screen Esc button) does; other input just shows how.</summary>
    EscapeKey,
}

public enum LibraryAddMode
{
    /// <summary>Ask each time (기존 목록에 추가 / 새로 등록).</summary>
    Ask,

    /// <summary>Add to the folders/files already in the list.</summary>
    Append,

    /// <summary>Replace the whole list with the new selection.</summary>
    Replace,
}

public enum PlayerMode
{
    Full,
    MiniHorizontal,
    MiniVertical,
}

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum RepeatMode
{
    /// <summary>Play through the list once and stop after the last track.</summary>
    Off,

    /// <summary>Start over from the beginning after the last track.</summary>
    All,

    /// <summary>Keep replaying the current track.</summary>
    One,
}

public enum StartupResume
{
    /// <summary>The last song, at the spot where it was left.</summary>
    LastPosition,
    /// <summary>The last song, from its beginning.</summary>
    SongStart,
    /// <summary>Nothing loaded (with auto-play: the first song of the queue).</summary>
    Nothing,
}

public enum PlaylistSortKey
{
    FileName,
    Path,
    Artist,
    Title,
    Album,
    Duration,
    /// <summary>The order the songs were dragged into.</summary>
    Custom,
}

public enum SeekBarStyle
{
    /// <summary>Drifting waves in the cover's colors.</summary>
    Wave,
    /// <summary>Flickering flames with sparks.</summary>
    Flame,
    /// <summary>One of the looks, picked anew for each song.</summary>
    Random,
    // Added later: after Random, as settings store the number.
    /// <summary>A glowing neon line that pulses with the music.</summary>
    Neon,
    /// <summary>The song's own loudness as thin bars along the played part.</summary>
    Bars,
    /// <summary>A fountain of droplets from the thumb. (This slot was the lightning bolt, then
    /// threads of light; settings that chose either get the fountain.)</summary>
    Fountain,
    /// <summary>Twinkling stars drifting up from the played part.</summary>
    Sparkle,
    /// <summary>Raindrops falling onto the played part, rippling where they land. (This slot was
    /// a stream of light specks; settings that chose it get the rain.)</summary>
    Rain,
    /// <summary>A shimmering aurora curtain hanging above the played part.</summary>
    Aurora,
    /// <summary>No effect: a plain line, nothing animated.</summary>
    None,
    /// <summary>Rockets bursting over the played part on the beats.</summary>
    Fireworks,
    /// <summary>Fireflies wandering over the played part, blinking.</summary>
    Fireflies,
    /// <summary>The played line discharging like a Tesla coil: forking arcs all along it, arcs jumping along it on the beats.</summary>
    Electric,
    /// <summary>Petals drifting down over the played part, tumbling as they fall.</summary>
    Petals,
    /// <summary>Musical notes rising from all along the played part.</summary>
    Notes,
    /// <summary>Soap bubbles rising from the played part, popping on the beats. (This slot was the
    /// plasma ball; settings that chose it get the bubbles.)</summary>
    Bubbles,
    /// <summary>Silk ribbons tied to the thumb, swaying and twisting back over the played part.</summary>
    Ribbon,
    /// <summary>Shooting stars falling at a slant onto the played part, flashing where they land.</summary>
    Meteor,
}

public enum SpectrumStyle
{
    Off,
    /// <summary>LED-segment bars with falling peak caps.</summary>
    Led,
    /// <summary>Smooth rounded bars.</summary>
    Bars,
    /// <summary>A flowing filled curve.</summary>
    Wave,
    /// <summary>Bars mirrored around the middle.</summary>
    Mirror,
    /// <summary>Columns of dots.</summary>
    Dots,
    /// <summary>One of the looks, picked anew for each song.</summary>
    Random,
    // Added later: after Random, as settings store the number.
    /// <summary>A glowing outline with no fill.</summary>
    Neon,
    /// <summary>Bars running through the colors of the rainbow.</summary>
    Rainbow,
    /// <summary>Bars standing on a mirror-like floor with their reflection.</summary>
    Reflect,
    /// <summary>Thin stems with a round head, the peak hanging above as a ring.</summary>
    Pins,
    /// <summary>An oscilloscope trace with a phosphor afterglow.</summary>
    Scope,
}

public enum QueueViewMode
{
    Standard,
    Compact,
    Grid,
}

public enum PlaylistGroupKey
{
    None,
    Folder,
    Artist,
    Album,
}

public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool IsMaximized { get; set; }
}
