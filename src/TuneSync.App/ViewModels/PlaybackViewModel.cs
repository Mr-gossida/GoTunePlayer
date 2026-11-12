using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Drives the always-visible playback bar: play/pause/seek/volume over a
/// <see cref="MediaPlayer"/>, plus the synced-lyrics state the 플레이 page shows (falls back to
/// plain text when the file's lyrics aren't LRC-timestamped).</summary>
public sealed partial class PlaybackViewModel : ObservableObject, IDisposable
{
    private readonly LibraryViewModel _library;
    private readonly AppSettings _settings;
    private readonly MediaPlayer _player = new();
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
    private readonly List<LibraryFileViewModel> _shuffleBag = [];
    private readonly Stack<LibraryFileViewModel> _history = new();
    private readonly Dictionary<string, WaveformData?> _waveformCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _waveformCacheOrder = new();
    private CancellationTokenSource? _waveformCts;
    private int _coverVersion;

    /// <summary>The playing track's cover at full quality for the large art on the 플레이 page and
    /// the playback bar. The library only keeps a small thumbnail per file (to stay light with
    /// thousands of songs), which looked blurry blown up to 240px+ on a scaled display.</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _currentCover;

    /// <summary>The same cover decoded at 256px, for the small spots (bottom bar, narrow-layout
    /// header, mini player strip). Scaling the 1000px original down to ~60-90px in high quality
    /// is CPU work WPF repeats whenever such an element reappears, which caused a visible hitch.</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapImage? _currentCoverSmall;

    /// <summary>A tiny pre-blurred copy of the cover, stretched behind the player.</summary>
    [ObservableProperty]
    private System.Windows.Media.Imaging.BitmapSource? _backdropCover;

    /// <summary>The queue panel on the right of the full player.</summary>
    [ObservableProperty]
    private bool _isQueueVisible;

    /// <summary>Lyrics next to the cover; off shows a larger cover alone.</summary>
    [ObservableProperty]
    private bool _isLyricsVisible;

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

    /// <summary>The line being sung now (mini players), or the artist when there are no synced lyrics.</summary>
    public string CurrentLyricText => LyricAt(CurrentLyricsIndex) ?? (HasSyncedLyrics ? "♪" : NowPlayingSubtitle);
    public string PreviousLyricText => HasSyncedLyrics ? LyricAt(CurrentLyricsIndex - 1) ?? "" : "";
    public string NextLyricText => HasSyncedLyrics ? LyricAt(CurrentLyricsIndex + 1) ?? "" : "";

    private string? LyricAt(int index) =>
        index >= 0 && index < LyricsLines.Count && !string.IsNullOrWhiteSpace(LyricsLines[index].Text) ? LyricsLines[index].Text : null;

    partial void OnCurrentLyricsIndexChanged(int value) => RaiseLyricTexts();

    private void RaiseLyricTexts()
    {
        OnPropertyChanged(nameof(CurrentLyricText));
        OnPropertyChanged(nameof(PreviousLyricText));
        OnPropertyChanged(nameof(NextLyricText));
    }

    /// <summary>"앨범 · 2024 · FLAC" under the big title.</summary>
    public string NowPlayingDetails => CurrentFile is not { } f ? "" : string.Join(" · ",
        new[] { f.Album, f.YearText }.Where(x => !string.IsNullOrWhiteSpace(x)));

    // --- audio quality: "FLAC · 24bit · 96kHz · 2,304kbps" with a Hi-Res / Lossless badge ---

    private int _qualityVersion;

    [ObservableProperty]
    private string _qualityText = "";

    /// <summary>"Hi-Res", "Lossless", or empty for lossy formats.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQualityBadge))]
    private string _qualityBadge = "";

    public bool HasQualityBadge => QualityBadge.Length > 0;

    /// <summary>Read off the UI thread (it opens the file); a placeholder from the extension
    /// shows meanwhile so the line never flickers empty.</summary>
    private async void LoadQuality(LibraryFileViewModel file)
    {
        var version = ++_qualityVersion;
        QualityText = System.IO.Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();
        QualityBadge = "";
        AudioQuality? quality;
        try
        {
            quality = await Task.Run(() => TagService.ReadAudioQuality(PlayablePath.ResolveForReading(file.Path)));
        }
        catch
        {
            return; // unreadable right now: keep the format alone
        }
        if (version != _qualityVersion || quality is null) return;
        QualityText = quality.Summary;
        QualityBadge = quality.Badge ?? "";
    }

    /// <summary>The playing track's real loudness over time (for the seek bar); null while it's
    /// being analyzed or when the format can't be decoded.</summary>
    [ObservableProperty]
    private WaveformData? _waveform;

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

    public PlaybackViewModel(LibraryViewModel library, AppSettings settings, Network.NetworkLibrary network)
    {
        _library = library;
        _settings = settings;
        _network = network;
        _volume = Math.Clamp(settings.Volume, 0, 1);
        _isShuffle = settings.Shuffle;
        _repeat = settings.Repeat;
        _isQueueVisible = settings.ShowQueue;
        _isLyricsVisible = settings.ShowLyrics;
        _isMiniTopmost = settings.MiniTopmost;
        _isMiniQueueExpanded = settings.MiniQueueExpanded;
        _keepScreenOn = settings.KeepScreenOnWhilePlaying;
        WatchForSessionToResume();
        _player.Volume = _volume;

        _player.MediaOpened += (_, _) =>
        {
            // MediaPlayer falls back to its default volume (0.5) whenever a source is (re)opened,
            // so the saved volume shown on the slider must be re-applied every time.
            _player.Volume = Volume;
            _player.IsMuted = IsMuted;
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
        _player.MediaEnded += (_, _) => AdvanceOrStop();
        _player.MediaFailed += (_, e) =>
        {
            Stop();
            ResetTimeline();
            Toast.Error("재생할 수 없습니다",
                $"{CurrentFile?.FileName ?? "파일"}\n{e.ErrorException?.Message ?? "알 수 없는 오류"}");
        };

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
        _player.Open(UriFor(resolved));
    }

    /// <summary>What the player opens: a network song is streamed from the local cache server
    /// (it starts playing while it downloads); everything else is the file itself.</summary>
    private Uri UriFor(string path)
    {
        if (!TuneSync.Core.Models.RemotePath.IsRemote(path)) return new Uri(path);
        return _network.Server.UrlFor(_network.Cache.Open(path));
    }

    /// <summary>Fetches the song that will play next (if it's a network song) so it starts at once.</summary>
    private void PrefetchNext()
    {
        try
        {
            LibraryFileViewModel? next = _upNext.FirstOrDefault();
            if (next is null && !IsShuffle)
            {
                var files = CurrentList();
                var index = CurrentFile is null ? -1 : files.IndexOf(CurrentFile);
                if (index >= 0 && files.Count > 1) next = files[(index + 1) % files.Count];
            }
            if (next is not null && TuneSync.Core.Models.RemotePath.IsRemote(next.Path))
                _network.Cache.Prefetch(next.Path);
        }
        catch
        {
            // best effort
        }
    }

    public void PlayFile(LibraryFileViewModel file) => PlayFile(file, fromHistory: false);

    // --- session: reopen on the last song, paused where it was left ---

    /// <summary>As the library streams in at startup, picks up the song that was loaded when the
    /// app was last closed and loads it paused at the same spot (nothing plays by itself).</summary>
    private void WatchForSessionToResume()
    {
        if (string.IsNullOrEmpty(_settings.LastFilePath)) return;
        var path = _settings.LastFilePath;
        var position = TimeSpan.FromSeconds(Math.Max(0, _settings.LastPositionSeconds));

        System.Collections.Specialized.NotifyCollectionChangedEventHandler? onFiles = null;
        System.ComponentModel.PropertyChangedEventHandler? onScan = null;
        void StopWatching()
        {
            _library.Files.CollectionChanged -= onFiles;
            _library.PropertyChanged -= onScan;
        }
        onFiles = (_, e) =>
        {
            if (CurrentFile is not null) { StopWatching(); return; } // the user already picked something
            if (e.NewItems?.OfType<LibraryFileViewModel>().FirstOrDefault(f =>
                    string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)) is not { } file) return;
            StopWatching();
            // After the current batch of adds, so the queue can select and scroll to it.
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ResumeSession(file, position),
                System.Windows.Threading.DispatcherPriority.Background);
        };
        onScan = (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.IsScanning) && !_library.IsScanning) StopWatching(); // not in the library anymore
        };
        _library.Files.CollectionChanged += onFiles;
        _library.PropertyChanged += onScan;
    }

    private void ResumeSession(LibraryFileViewModel file, TimeSpan position)
    {
        if (CurrentFile is not null) return;
        PlayFile(file);
        // PlayFile starts playing once the file opens; ask it to stay paused at the saved spot.
        _pendingRestore = (position, false);
        if (position > TimeSpan.FromSeconds(1))
        {
            _introUntil = null; // picking up mid-song: straight to the lyrics
            ShowPosition(position);
        }
    }

    /// <summary>Remembers the loaded song and position for next time (called on close).</summary>
    public void SaveSession()
    {
        _settings.LastFilePath = CurrentFile?.Path;
        _settings.LastPositionSeconds = CurrentFile is null ? 0 : (_seekTarget ?? _player.Position).TotalSeconds;
        AppSettingsStore.Save(_settings);
    }

    private async void PlayFile(LibraryFileViewModel file, bool fromHistory)
    {
        // Remember where we came from, so "이전" can retrace the real (shuffled) order.
        if (!fromHistory && CurrentFile is { } previous && previous != file)
        {
            _history.Push(previous);
            if (_history.Count > 500) _history.Clear();
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
            _player.Open(UriFor(path));
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
        var seconds = _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        if (seconds <= 0 && CurrentFile is not null) seconds = CurrentFile.Model.Duration.TotalSeconds;
        if (seconds <= 0) return;

        DurationSeconds = Math.Max(1, seconds);
        DurationText = FormatTime(TimeSpan.FromSeconds(DurationSeconds));
    }

    /// <summary>Decodes the track in the background (about a second for a typical song) so the
    /// seek bar can draw its real waveform. Recently played tracks come from a small cache.</summary>
    private async void LoadWaveform(string path)
    {
        _waveformCts?.Cancel();
        if (_waveformCache.TryGetValue(path, out var cached))
        {
            Waveform = cached;
            return;
        }

        Waveform = null;
        var cts = new CancellationTokenSource();
        _waveformCts = cts;
        WaveformData? result;
        try
        {
            // A network song is analyzed from its cached copy once the download has finished.
            var readable = TuneSync.Core.Models.RemotePath.IsRemote(path)
                ? await _network.Cache.CompletedPathAsync(path)
                : PlayablePath.ResolveForReading(path);
            result = await Task.Run(() => WaveformAnalyzer.Analyze(readable, cts.Token), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return; // another track started; its own analysis takes over
        }
        catch (Exception)
        {
            return; // network song couldn't be fetched: the seek bar just keeps its plain wave
        }

        _waveformCache[path] = result;
        _waveformCacheOrder.Enqueue(path);
        while (_waveformCacheOrder.Count > 30) _waveformCache.Remove(_waveformCacheOrder.Dequeue());

        if (!cts.IsCancellationRequested && string.Equals(CurrentFile?.Path, path, StringComparison.OrdinalIgnoreCase))
            Waveform = result;
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

    /// <summary>The cover of the playing song was changed (태그 관리 / 자동 채움): show the new one.</summary>
    private void CurrentFile_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryFileViewModel.CoverThumbnail) && sender is LibraryFileViewModel file && file == CurrentFile)
            LoadCover(file);
    }

    /// <summary>Shows the thumbnail at once, then swaps in the embedded original (read and
    /// decoded off the UI thread, at most 1000px wide) as soon as it's ready, and recolors the
    /// player from it.</summary>
    private async void LoadCover(LibraryFileViewModel file)
    {
        var version = ++_coverVersion;
        CurrentCover = file.CoverThumbnail;
        CurrentCoverSmall = file.CoverThumbnail;
        if (!file.HasCover)
        {
            BackdropCover = null;
            PlayerPalette.Apply(null);
            return;
        }

        (System.Windows.Media.Imaging.BitmapImage? Full, System.Windows.Media.Imaging.BitmapImage? Small,
         System.Windows.Media.Imaging.BitmapSource? Backdrop, System.Windows.Media.Color? Key) result;
        var thumbnail = file.CoverThumbnail;
        try
        {
            result = await Task.Run(() =>
            {
                var bytes = CoverArtService.GetCover(PlayablePath.ResolveForReading(file.Path));
                var full = ImageHelper.ToBitmapImage(bytes, decodePixelWidth: 1000);
                var small = ImageHelper.ToBitmapImage(bytes, decodePixelWidth: 256);
                System.Windows.Media.Imaging.BitmapSource? source = ImageHelper.ToBitmapImage(bytes, decodePixelWidth: 64) ?? thumbnail;
                return (full, small, ImageHelper.BlurredBackdrop(source), source is null ? (System.Windows.Media.Color?)null : PlayerPalette.ExtractKeyColor(source));
            });
        }
        catch
        {
            // Unreadable right now (file locked/share offline): keep the thumbnail.
            result = (null, null, ImageHelper.BlurredBackdrop(thumbnail), thumbnail is null ? null : PlayerPalette.ExtractKeyColor(thumbnail));
        }
        if (version != _coverVersion) return;

        if (result.Full is not null) CurrentCover = result.Full;
        if (result.Small is not null) CurrentCoverSmall = result.Small;
        BackdropCover = result.Backdrop;
        PlayerPalette.Apply(result.Key);
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

    /// <summary>"이전": like most players, restarts the current track if it's past the first few
    /// seconds; otherwise goes back (through the actual play history when shuffling).</summary>
    [RelayCommand]
    private void PlayPrevious()
    {
        if (CurrentFile is not null && PositionSeconds > 3)
        {
            SeekTo(TimeSpan.Zero);
            return;
        }

        if (IsShuffle)
        {
            var files = CurrentList();
            while (_history.Count > 0)
            {
                var previous = _history.Pop();
                if (!files.Contains(previous)) continue; // filtered out or removed since
                PlayFile(previous, fromHistory: true);
                return;
            }
        }

        PlayRelative(-1);
    }

    /// <summary>"다음" button: always moves on (wrapping around), regardless of repeat mode.</summary>
    // --- "다음에 재생": songs reserved from the queue's right-click menu, played before the
    // list order (or shuffle) continues ---

    private readonly List<LibraryFileViewModel> _upNext = [];

    /// <summary>Plays <paramref name="file"/> right after the current song.</summary>
    public void PlayAfterCurrent(LibraryFileViewModel file)
    {
        _upNext.Remove(file);
        _upNext.Insert(0, file);
        RenumberUpNext();
    }

    /// <summary>Plays <paramref name="file"/> after the songs already reserved.</summary>
    public void PlayLater(LibraryFileViewModel file)
    {
        _upNext.Remove(file);
        _upNext.Add(file);
        RenumberUpNext();
    }

    public void CancelUpNext(LibraryFileViewModel file)
    {
        if (_upNext.Remove(file)) file.UpNextOrder = 0;
        RenumberUpNext();
    }

    public void ClearUpNext()
    {
        foreach (var file in _upNext) file.UpNextOrder = 0;
        _upNext.Clear();
    }

    public bool HasUpNext => _upNext.Count > 0;

    private void RenumberUpNext()
    {
        for (var i = 0; i < _upNext.Count; i++) _upNext[i].UpNextOrder = i + 1;
        OnPropertyChanged(nameof(HasUpNext));
    }

    private LibraryFileViewModel? TakeUpNext()
    {
        var visible = _library.Files.ToHashSet();
        while (_upNext.Count > 0)
        {
            var next = _upNext[0];
            _upNext.RemoveAt(0);
            next.UpNextOrder = 0;
            if (visible.Contains(next)) { RenumberUpNext(); return next; } // skip songs removed from the library since
        }
        RenumberUpNext();
        return null;
    }

    [RelayCommand]
    private void PlayNext()
    {
        if (TakeUpNext() is { } reserved)
        {
            PlayFile(reserved);
            return;
        }
        if (IsShuffle)
        {
            if (NextShuffled(refillWhenEmpty: true) is { } next) PlayFile(next);
            return;
        }
        PlayRelative(+1);
    }

    [RelayCommand]
    private void ToggleShuffle() => IsShuffle = !IsShuffle;

    /// <summary>Cycles 반복 끔 → 전체 반복 → 한 곡 반복.</summary>
    [RelayCommand]
    private void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    partial void OnIsShuffleChanged(bool value)
    {
        _shuffleBag.Clear(); // a fresh shuffle each time it's turned on
        _settings.Shuffle = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        _settings.Repeat = value;
        AppSettingsStore.Save(_settings);
    }

    public string ShuffleToolTip => IsShuffle ? "랜덤 재생: 켬" : "랜덤 재생: 끔";

    public string RepeatToolTip => Repeat switch
    {
        RepeatMode.All => "반복: 전체 반복",
        RepeatMode.One => "반복: 한 곡 반복",
        _ => "반복: 끔",
    };

    public int VolumePercent => (int)Math.Round(Volume * 100);

    /// <summary>The playlist exactly as shown in the 플레이 screen (its sort and grouping).</summary>
    private List<LibraryFileViewModel> CurrentList() => [.. _library.PlaylistView.Cast<LibraryFileViewModel>()];

    /// <summary>Previous/next in list order, wrapping around at either end.</summary>
    private void PlayRelative(int step)
    {
        var files = CurrentList();
        if (files.Count == 0) return;

        var currentIndex = CurrentFile is null ? -1 : files.IndexOf(CurrentFile);
        var nextIndex = ((currentIndex < 0 ? 0 : currentIndex + step) % files.Count + files.Count) % files.Count;
        PlayFile(files[nextIndex]);
    }

    /// <summary>Shuffle draws from a "bag" holding every track once, so nothing repeats until the
    /// whole list has played. Null when the bag is spent and shouldn't be refilled.</summary>
    private LibraryFileViewModel? NextShuffled(bool refillWhenEmpty)
    {
        var files = CurrentList();
        if (files.Count == 0) return null;

        var visible = files.ToHashSet();
        _shuffleBag.RemoveAll(f => !visible.Contains(f));
        if (_shuffleBag.Count == 0)
        {
            if (!refillWhenEmpty) return null;
            _shuffleBag.AddRange(files.Where(f => f != CurrentFile).OrderBy(_ => Random.Shared.Next()));
            if (_shuffleBag.Count == 0) return CurrentFile; // a one-track list
        }

        var next = _shuffleBag[^1];
        _shuffleBag.RemoveAt(_shuffleBag.Count - 1);
        return next;
    }

    /// <summary>End of a track: obeys repeat (one: replay, all: wrap around, off: stop after the
    /// last track) and shuffle.</summary>
    private void AdvanceOrStop()
    {
        if (Repeat == RepeatMode.One && CurrentFile is not null)
        {
            // After MediaEnded the player sits paused at the end; rewind and start again.
            ApplySeek(TimeSpan.Zero);
            _player.Play();
            IsPlaying = true;
            return;
        }

        if (TakeUpNext() is { } reserved)
        {
            PlayFile(reserved);
            return;
        }

        if (IsShuffle)
        {
            if (NextShuffled(refillWhenEmpty: Repeat == RepeatMode.All) is { } shuffled)
                PlayFile(shuffled);
            else
                Stop();
            return;
        }

        var files = CurrentList();
        var index = CurrentFile is null ? -1 : files.IndexOf(CurrentFile);
        if (index >= 0 && index < files.Count - 1)
            PlayFile(files[index + 1]);
        else if (Repeat == RepeatMode.All && files.Count > 0)
            PlayFile(files[0]);
        else
            Stop();
    }

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

    // --- intro: a long instrumental opening shows the song's details before the first line ---

    private TimeSpan? _introUntil;

    /// <summary>Before the first synced line (when it's a few seconds away) the lyrics area shows
    /// the song's title/artist/album instead, then hands over just before the singing starts.</summary>
    [ObservableProperty]
    private bool _isIntroVisible;

    /// <summary>0..1 through the intro, for its small "lyrics start soon" bar.</summary>
    [ObservableProperty]
    private double _introProgress;

    /// <summary>How long the whole intro lasts, so the view can glide the bar between position
    /// updates (they come only every 200 ms) instead of stepping.</summary>
    public TimeSpan IntroLength { get; private set; }

    private void UpdateIntro(TimeSpan pos)
    {
        var show = HasSyncedLyrics && _introUntil is { } until && pos < until;
        if (!show) _introUntil = null; // once over, it stays over: rewinding shows the lyrics
        IntroLength = _introUntil ?? TimeSpan.Zero;
        IsIntroVisible = show;
        IntroProgress = show && _introUntil is { } u ? Math.Clamp(pos / u, 0, 1) : 0;
    }

    /// <summary>Unsynced lyrics split into lines, so they can cascade in one after another.</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _plainLyricsLines = [];

    private void SetCurrentLyricsIndex(int index)
    {
        if (index == CurrentLyricsIndex) return;

        if (CurrentLyricsIndex is >= 0 && CurrentLyricsIndex < LyricsLines.Count)
            LyricsLines[CurrentLyricsIndex].IsCurrent = false;

        CurrentLyricsIndex = index;

        if (index is >= 0 && index < LyricsLines.Count)
            LyricsLines[index].IsCurrent = true;
    }

    /// <summary>The playing file's lyrics were just written: show them without restarting.</summary>
    public void ReloadLyrics()
    {
        if (CurrentFile is not { } file) return;
        LoadLyrics(file);
    }

    private int _lyricsVersion;
    private bool _isLoadingLyrics;

    /// <summary>Reads the song's lyrics off the UI thread: the library lives on a network share,
    /// and a slow read there used to freeze the whole window right as a song started.</summary>
    private async void LoadLyrics(LibraryFileViewModel file)
    {
        var version = ++_lyricsVersion;
        LyricsLines.Clear();
        PlainLyrics = null;
        PlainLyricsLines = [];
        HasSyncedLyrics = false;
        CurrentLyricsIndex = -1;
        _introUntil = null;
        _isLoadingLyrics = true;
        OnPropertyChanged(nameof(HasNoLyrics)); // not "no lyrics" yet - still reading

        string? lyrics;
        try
        {
            lyrics = await Task.Run(() => TagService.ReadLyrics(PlayablePath.ResolveForReading(file.Path)));
        }
        catch
        {
            lyrics = null;
        }
        if (version != _lyricsVersion) return; // another song (or reload) took over meanwhile
        _isLoadingLyrics = false;

        if (!string.IsNullOrWhiteSpace(lyrics))
        {
            var parsed = LrcParser.Parse(lyrics);
            if (parsed.Count > 0)
            {
                foreach (var line in parsed)
                    LyricsLines.Add(new LyricsLineViewModel(line.Time, line.Text));
                HasSyncedLyrics = true;
                // Worth an intro only when there's real time before the first line.
                var sung = parsed.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
                var first = (sung.Count > 0 ? sung[0] : parsed[0]).Time;
                if (first >= TimeSpan.FromSeconds(5)) _introUntil = first - TimeSpan.FromSeconds(0.8);
            }
            else
            {
                PlainLyrics = lyrics;
                PlainLyricsLines = lyrics.Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
            }
        }

        // Where the song is by now (or where a resume/seek is taking it): the intro only plays
        // from the very start, and the current line is picked from this position.
        var position = _pendingRestore?.Position ?? _seekTarget ?? TimeSpan.FromSeconds(PositionSeconds);
        if (position > TimeSpan.FromSeconds(1)) _introUntil = null;
        OnPropertyChanged(nameof(HasPlainLyricsOnly));
        OnPropertyChanged(nameof(HasNoLyrics));
        ShowPosition(position);
        RaiseLyricTexts();
    }

    /// <summary>True when the file has lyrics but they carry no LRC timestamps, so the plain-text
    /// fallback (rather than the synced, auto-scrolling list) is what the view should show.</summary>
    public bool HasPlainLyricsOnly => !HasSyncedLyrics && !string.IsNullOrWhiteSpace(PlainLyrics);

    public bool HasNoLyrics => CurrentFile is not null && !_isLoadingLyrics && !HasSyncedLyrics && string.IsNullOrWhiteSpace(PlainLyrics);

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    public void Dispose()
    {
        _timer.Stop();
        _volumeSaveTimer.Stop();
        _waveformCts?.Cancel();
        _player.Close();
        PlayablePath.DeleteLastCopy();
    }
}
