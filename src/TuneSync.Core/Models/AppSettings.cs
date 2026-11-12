namespace TuneSync.Core.Models;

/// <summary>Everything that should survive a restart: the last folder picked on each page, plus
/// the rule/mode choices made there. Loaded once at startup and re-saved whenever one of these
/// changes - see <see cref="Services.AppSettingsStore"/>.</summary>
public sealed class AppSettings
{
    /// <summary>The music library is built from any number of watched folders (like a real media
    /// player), added/removed via the +/- buttons rather than a single "current folder".</summary>
    public List<LibraryFolderEntry> LibraryFolders { get; set; } = [];

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
    public bool ShowLyrics { get; set; } = true;

    /// <summary>The small bar showing how far a song's intro has played before the lyrics start.</summary>
    public bool ShowIntroGauge { get; set; } = true;

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

    /// <summary>What the player opens with when the app starts.</summary>
    public StartupResume StartupResume { get; set; } = StartupResume.LastPosition;

    /// <summary>Start playing right away when the app opens (instead of waiting, paused).</summary>
    public bool AutoPlayOnStartup { get; set; }

    /// <summary>The player's screen saver: after this many minutes without input while music
    /// plays, a full-screen "now playing" view takes over.</summary>
    public bool ScreenSaverEnabled { get; set; } = true;
    public int ScreenSaverMinutes { get; set; } = 5;
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
