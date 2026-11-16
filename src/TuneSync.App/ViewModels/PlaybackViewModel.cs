using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Drives the always-visible playback bar: play/pause/seek/volume over a player engine
/// (<see cref="Audio.IPlayerEngine"/>), plus the synced-lyrics state the 플레이 page shows (falls
/// back to plain text when the file's lyrics aren't LRC-timestamped).</summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly LibraryViewModel _library;
    private readonly AppSettings _settings;
    private readonly EqualizerViewModel _equalizer;
    /// <summary>What plays the current song; a fresh one for each song opened (see <see cref="OpenSource"/>).</summary>
    private Audio.IPlayerEngine _player = new Audio.NoEngine();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _volumeSaveTimer;
    private bool _isSeeking;
    private TimeSpan? _seekTarget;
    private DateTime _seekStartedAt;
    private TimeSpan _preSeekPosition;

    public ObservableCollection<LyricsLineViewModel> LyricsLines { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NowPlayingTitle), nameof(NowPlayingSubtitle), nameof(NowPlayingDetails), nameof(HasCurrentFile))]
    private LibraryFileViewModel? _currentFile;

    private int _openVersion;
    private (TimeSpan Position, bool Play)? _pendingRestore;
    private bool _isReleased;

    /// <summary>"앨범 · 2024 · FLAC" under the big title.</summary>
    public string NowPlayingDetails => CurrentFile is not { } f ? "" : string.Join(" · ",
        new[] { f.Album, f.YearText }.Where(x => !string.IsNullOrWhiteSpace(x)));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleToolTip))]
    private bool _isShuffle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepeatToolTip))]
    private RepeatMode _repeat;

    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Settings › 재생 중 화면 켜짐 유지.</summary>
    [ObservableProperty]
    private bool _keepScreenOn;

    partial void OnIsPlayingChanged(bool value) => UpdateKeepAwake();

    partial void OnKeepScreenOnChanged(bool value)
    {
        _settings.KeepScreenOnWhilePlaying = value;
        AppSettingsStore.Save(_settings);
        UpdateKeepAwake();
    }

    /// <summary>While playing (and the option is on), tells Windows not to turn the display off
    /// or sleep; any other time, gives that back. Always called on the UI thread, which owns the
    /// request for as long as the app runs.</summary>
    private void UpdateKeepAwake()
    {
        const uint EsContinuous = 0x80000000, EsSystemRequired = 0x1, EsDisplayRequired = 0x2;
        SetThreadExecutionState(KeepScreenOn && IsPlaying ? EsContinuous | EsSystemRequired | EsDisplayRequired : EsContinuous);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);

    [ObservableProperty]
    private double _positionSeconds;

    [ObservableProperty]
    private double _durationSeconds = 1; // never 0 - keeps the Slider's range valid before a file loads

    [ObservableProperty]
    private string _positionText = "00:00";

    [ObservableProperty]
    private string _durationText = "00:00";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    private double _volume;

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _hasSyncedLyrics;

    [ObservableProperty]
    private string? _plainLyrics;

    [ObservableProperty]
    private int _currentLyricsIndex = -1;

    private readonly Network.NetworkLibrary _network;

    public PlaybackViewModel(LibraryViewModel library, AppSettings settings, Network.NetworkLibrary network, EqualizerViewModel equalizer)
    {
        _library = library;
        _settings = settings;
        _network = network;
        _equalizer = equalizer;
        // A slider moved in the equalizer reaches the playing song at once.
        equalizer.ConfigChanged += config =>
        {
            if (_player is Audio.ToneEngine engine) engine.Config = config;
        };
        library.FilesRemoving += OnFilesRemoving;
        LibraryFileViewModel.ArtistFirst = settings.QueueArtistFirst;
        PickSeekLook();
        PickSpectrumLook();
        // 임의 / 랜덤: a new look for every song.
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(CurrentFile)) return;
            if (SeekBarStyle == SeekBarStyle.Random) PickSeekLook();
            if (SpectrumStyle == SpectrumStyle.Random) PickSpectrumLook();
        };
        _volume = Math.Clamp(settings.Volume, 0, 1);
        _isShuffle = settings.Shuffle;
        _repeat = settings.Repeat;
        _isQueueVisible = settings.ShowQueue;
        _isLyricsVisible = settings.ShowLyrics;
        _isMiniTopmost = settings.MiniTopmost;
        _isMiniQueueExpanded = settings.MiniQueueExpanded;
        _keepScreenOn = settings.KeepScreenOnWhilePlaying;
        WatchForSessionToResume();

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(200) };

        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();

        // A volume drag fires dozens of changes; persist once it settles rather than on each one.
        _volumeSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _volumeSaveTimer.Tick += (_, _) =>
        {
            _volumeSaveTimer.Stop();
            _settings.Volume = Volume;
            AppSettingsStore.Save(_settings);
        };
    }

    public string NowPlayingTitle => CurrentFile?.DisplayTitle ?? "재생 중인 곡 없음";

    public string NowPlayingSubtitle => CurrentFile?.Artist ?? "";

    public bool HasCurrentFile => CurrentFile is not null;

    /// <summary>Windows Media keeps the playing file open, which blocks writing its tags or
    /// renaming/moving it ("being used by another process"). If the playing file is among
    /// <paramref name="paths"/>, this closes it and returns a callback that reopens it - at its
    /// new path if it moved - and continues from the same position. Null when nothing was released.</summary>
    /// <summary>Where the song is: the player's position, or - while the player is closed because
    /// its file is released for a tag save - the position the seek bar kept showing.</summary>
    private TimeSpan CurrentOrShownPosition()
    {
        if (_seekTarget is { } target) return target;
        var position = _isReleased ? TimeSpan.Zero : _player.Position;
        return position < TimeSpan.FromSeconds(0.5) && PositionSeconds > 0.5 ? TimeSpan.FromSeconds(PositionSeconds) : position;
    }

    public async Task<Action<string?>?> ReleaseIfPlayingAsync(IEnumerable<string> paths)
    {
        if (CurrentFile is not { } file
            || !paths.Contains(file.Path, StringComparer.OrdinalIgnoreCase)) return null;

        var position = _seekTarget ?? _player.Position;
        var wasPlaying = IsPlaying;
        _openVersion++; // cancel any open still in flight
        _isReleased = true; // keep showing the old position instead of the closed player's 0:00
        _player.Close();
        IsPlaying = false;
        PlayablePath.DeleteLastCopy();
        await Task.Delay(400); // Close is asynchronous; give Windows Media time to drop its file handle

        return newPath => Reopen(newPath ?? file.Path, position, wasPlaying);
    }

    private async void Reopen(string path, TimeSpan position, bool play)
    {
        var version = ++_openVersion;
        string resolved;
        try
        {
            resolved = await Task.Run(() => PlayablePath.Resolve(path));
        }
        catch (Exception ex)
        {
            Toast.Error("재생을 이어갈 수 없습니다", ex.Message);
            return;
        }
        if (version != _openVersion) return;

        _pendingRestore = (position, play);
        _isReleased = false;
        OpenSource(SourceFor(resolved));
    }

    /// <summary>The player's events; ones from a player already replaced are ignored.</summary>
    private void Wire(Audio.IPlayerEngine player)
    {
        player.MediaOpened += sender =>
        {
            if (!ReferenceEquals(sender, _player)) return;
            RefreshDuration();

            if (_pendingRestore is { } restore)
            {
                // Reopened after the file was released for editing: continue where it was.
                _pendingRestore = null;
                _player.Position = restore.Position;
                ShowPosition(restore.Position);
                if (!restore.Play)
                {
                    _player.Pause();
                    IsPlaying = false;
                    return;
                }
            }

            _player.Play();
            IsPlaying = true;
        };
        player.MediaEnded += sender =>
        {
            if (ReferenceEquals(sender, _player)) AdvanceOrStop();
        };
        player.MediaFailed += (sender, e) =>
        {
            if (!ReferenceEquals(sender, _player)) return;
            // The app's own player can't play this one (a format only Windows' media player
            // reads, or no sound device it can open): Windows' player takes over - the song
            // plays, just without the equalizer - from the same spot.
            if (e.CanFallBack && _openSource is { } source && CurrentFile is { } current)
            {
                if (e.WasOpen) _pendingRestore = (CurrentOrShownPosition(), IsPlaying);
                if (e.Unsupported) _windowsOnly.Add(current.Path);
                try
                {
                    OpenSource(source, windows: true);
                    return;
                }
                catch
                {
                    // not something Windows' player can be pointed at either: report it below
                }
            }
            // A network song whose stream broke off (its download was stopped as another song
            // was picked): open it again in a fresh player before giving up on it.
            if (_streamWatch is { Retries: < 3 } watch && CurrentFile is { } failed
                && TuneSync.Core.Models.RemotePath.IsRemote(failed.Path))
            {
                ReopenStream(watch, failed.Path, ResumeAfterFailure(watch));
                return;
            }
            Stop();
            ResetTimeline();
            Toast.Error("재생할 수 없습니다",
                $"{CurrentFile?.FileName ?? "파일"}\n{e.Error?.Message ?? "알 수 없는 오류"}");
        };
    }

    public void PlayFile(LibraryFileViewModel file) => PlayFile(file, fromHistory: false);

    private async void PlayFile(LibraryFileViewModel file, bool fromHistory)
    {
        // Remember where we came from, so "이전" can retrace the real (shuffled) order.
        if (!fromHistory && CurrentFile is { } previous && previous != file)
        {
            RememberPlayed(previous);
        }
        _shuffleBag.Remove(file); // picked by hand or by shuffle - either way it has now played
        if (file.UpNextOrder > 0) CancelUpNext(file);

        var version = ++_openVersion;
        _pendingRestore = null;
        _isReleased = false;
        _seekTarget = null;
        // Close (not just Stop): re-opening the source that's already loaded doesn't raise
        // MediaOpened again, which left the new play with no duration and a pinned seek bar.
        _player.Close();
        IsPlaying = false;
        ResetTimeline();
        CurrentFile = file;
        LoadLyrics(file);
        LoadWaveform(file.Path);
        LoadSpectrum(file.Path);
        LoadCover(file);
        LoadQuality(file);

        var path = file.Path;
        if (PlayablePath.NeedsWorkaround(path))
        {
            try
            {
                path = await Task.Run(() => PlayablePath.Resolve(file.Path));
            }
            catch (Exception ex)
            {
                if (version != _openVersion) return;
                Toast.Error("재생할 수 없습니다", $"{file.FileName}\n{ex.Message}");
                return;
            }
            if (version != _openVersion) return; // another track was picked while this one resolved
        }

        try
        {
            OpenSource(SourceFor(path));
        }
        catch (Exception ex)
        {
            Toast.Error("재생할 수 없습니다", file.FileName + Environment.NewLine + ex.Message);
            return;
        }
        PrefetchNext();
        // Play() happens once MediaOpened fires - opening is async.
    }

    /// <summary>Windows Media sometimes can't report a length when the file opens (seen with MP3s
    /// on a network share): fall back to the length TagLib read during the scan, and take the
    /// player's own value if it shows up later. Without a real length the seek bar would treat
    /// the track as one second long and pin the thumb to the end.</summary>
    private void RefreshDuration()
    {
        var seconds = _player.NaturalDuration?.TotalSeconds ?? 0;
        if (seconds <= 0 && CurrentFile is not null) seconds = CurrentFile.Model.Duration.TotalSeconds;
        if (seconds <= 0) return;

        DurationSeconds = Math.Max(1, seconds);
        DurationText = FormatTime(TimeSpan.FromSeconds(DurationSeconds));
    }

    partial void OnCurrentFileChanged(LibraryFileViewModel? oldValue, LibraryFileViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= CurrentFile_PropertyChanged;
            oldValue.IsNowPlaying = false;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += CurrentFile_PropertyChanged;
            newValue.IsNowPlaying = true;
        }
    }

    /// <summary>Clears position/duration so a new (or failed) track never shows the previous
    /// track's length.</summary>
    private void ResetTimeline()
    {
        PositionSeconds = 0;
        PositionText = "00:00";
        DurationSeconds = 1;
        DurationText = "00:00";
    }

    partial void OnVolumeChanged(double value)
    {
        _player.Volume = value;
        if (value > 0 && IsMuted) IsMuted = false;
        _volumeSaveTimer.Stop();
        _volumeSaveTimer.Start();
    }

    partial void OnIsMutedChanged(bool value) => _player.IsMuted = value;

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private void TogglePlayPause()
    {
        if (CurrentFile is null)
        {
            // Nothing loaded yet - start from the top of the current list, like a media player.
            if (_library.PlaylistView.Cast<LibraryFileViewModel>().FirstOrDefault() is { } first)
                PlayFile(first);
            return;
        }

        if (IsPlaying)
        {
            _player.Pause();
            IsPlaying = false;
        }
        else
        {
            _player.Play();
            IsPlaying = true;
        }
    }

    [RelayCommand]
    private void Stop()
    {
        _player.Stop();
        _isReleased = false;
        IsPlaying = false;
        _seekTarget = null;
        PositionSeconds = 0;
        PositionText = "00:00";
        SetCurrentLyricsIndex(-1);
    }

    public int VolumePercent => (int)Math.Round(Volume * 100);

    public void BeginSeek() => _isSeeking = true;

    public void EndSeek(double seconds)
    {
        _isSeeking = false;
        _introUntil = null; // the intro is only for a song's natural start, never after a seek
        ApplySeek(TimeSpan.FromSeconds(seconds));
    }

    /// <summary>Where playback is right now, read straight from the player (PositionSeconds only
    /// updates every 200 ms - too coarse for tapping lyric lines in time).</summary>
    public TimeSpan CurrentPosition => _seekTarget ?? _player.Position;

    /// <summary>Jumps to a point in the current track and makes sure it's playing - used when a
    /// synced lyrics line is clicked.</summary>
    public void SeekTo(TimeSpan time)
    {
        if (CurrentFile is null) return;
        if (!IsPlaying)
        {
            _player.Play();
            IsPlaying = true;
        }
        _introUntil = null;
        ApplySeek(time);
    }

    /// <summary>Keyboard ←/→: jumps back or ahead without changing play/pause.</summary>
    public void SeekBy(double seconds)
    {
        if (CurrentFile is null) return;
        var current = _seekTarget?.TotalSeconds ?? PositionSeconds;
        var target = Math.Clamp(current + seconds, 0, Math.Max(0, DurationSeconds - 0.5));
        _introUntil = null;
        ApplySeek(TimeSpan.FromSeconds(target));
    }

    /// <summary>Keyboard Ctrl+↑/↓ and the mouse wheel on the volume slider: 5% steps.</summary>
    public void ChangeVolume(double delta) => Volume = Math.Round(Math.Clamp(Volume + delta, 0, 1), 2);

    private void ApplySeek(TimeSpan target)
    {
        if (CurrentFile is null) return;
        _preSeekPosition = _player.Position;
        _player.Position = target;
        _seekTarget = target;
        _seekStartedAt = DateTime.UtcNow;
        ShowPosition(target);
    }

    private void UpdatePosition()
    {
        if (CurrentFile is null || _isSeeking || _isReleased) return;

        if (DurationSeconds <= 1) RefreshDuration();
        var pos = _player.Position;
        WatchStream(pos);

        // MediaPlayer keeps reporting the old position for a moment after a seek; showing that
        // would snap the slider back and then forward again, so hold the target until the seek
        // has taken effect - either it reached the target, or (VBR MP3s rarely land exactly) it
        // has clearly left the old timeline the stale position would still be following.
        if (_seekTarget is { } target)
        {
            var elapsed = DateTime.UtcNow - _seekStartedAt;
            var reachedTarget = Math.Abs((pos - target).TotalSeconds) < 1.0;
            var stalePosition = _preSeekPosition + (IsPlaying ? elapsed : TimeSpan.Zero);
            var leftOldTimeline = Math.Abs((pos - stalePosition).TotalSeconds) > 1.5;
            if (!reachedTarget && !leftOldTimeline && elapsed < TimeSpan.FromSeconds(2)) return;
            _seekTarget = null;
        }

        ShowPosition(pos);
    }

    private void ShowPosition(TimeSpan pos)
    {
        PositionSeconds = Math.Min(pos.TotalSeconds, DurationSeconds);
        PositionText = FormatTime(pos);

        if (HasSyncedLyrics)
        {
            var index = -1;
            for (var i = 0; i < LyricsLines.Count; i++)
            {
                if (LyricsLines[i].Time > pos) break;
                index = i;
            }
            SetCurrentLyricsIndex(index);
        }
        UpdateIntro(pos);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    public void Dispose()
    {
        _timer.Stop();
        _volumeSaveTimer.Stop();
        _waveformAnalysis.Cancel();
        _spectrumAnalysis.Cancel();
        _player.Close();
        PlayablePath.DeleteLastCopy();
    }
}
