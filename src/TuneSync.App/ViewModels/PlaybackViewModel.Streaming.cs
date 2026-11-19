using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

// Network songs: opening them through the local stream server, getting a stuck one going again,
// and fetching the next one ahead of time.
public sealed partial class PlaybackViewModel
{
    /// <summary>What the current player was opened from (to hand it to the other engine if this one fails).</summary>
    private Audio.PlaySource? _openSource;

    /// <summary>Songs the app's own player couldn't decode: Windows' player gets them straight away.</summary>
    private readonly HashSet<string> _windowsOnly = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the playing song goes around the equalizer (it needed Windows' own player).</summary>
    public bool IsToneBypassed => CurrentFile is not null && _player is Audio.WindowsEngine;

    /// <summary>The sound going out right now, for the equalizer's live displays: null while
    /// nothing plays or the song goes around the equalizer (Windows' player).</summary>
    public Core.Audio.OutputScope? OutputScope => _player is Audio.ToneEngine engine && IsPlaying ? engine.Scope : null;

    /// <summary>How the sound is going out right now ("독점 · 24비트 96kHz · 스테레오"), or why
    /// it can't go out as set; null while nothing plays through the app's own player.</summary>
    public string? OutputDescription => _player is Audio.ToneEngine engine ? engine.OutputDescription : null;

    /// <summary>The play screen's output badges ("독점", "32bit · 352.8kHz", "업샘플링 ×8"...);
    /// refreshed whenever <see cref="OutputDescription"/> is.</summary>
    public IReadOnlyList<OutputBadge> OutputBadges =>
        _player is Audio.ToneEngine engine && CurrentFile is not null ? OutputBadge.For(engine.OutputInfo, engine.OutputDescription) : [];

    public bool HasOutputBadges => OutputBadges.Count > 0;

    /// <summary>The song goes out bit-perfect: the equalizer and the volume are left out.</summary>
    public bool IsBitPerfect => _player is Audio.ToneEngine engine && engine.IsBitPerfect;

    /// <summary>The output settings changed (설정 › 음향 › 출력): the playing song moves to the
    /// new device or format right away, from where it was.</summary>
    public void ApplyOutput()
    {
        if (_player is not Audio.ToneEngine engine) return;
        _dopRefused.Clear(); // a new setting: DoP may work now
        if (engine.IsDsd && CurrentFile is { } dsd)
        {
            // a DSD song is read as DoP or as PCM depending on the output: open it again
            _pendingRestore = (CurrentPosition, IsPlaying);
            OpenSource(SourceFor(dsd.Path));
            OnPropertyChanged(nameof(OutputDescription));
            return;
        }
        var at = CurrentPosition;
        engine.Output = _settings.Output;
        if (CurrentFile is not null) _player.Position = at; // what the old device still held is heard again from here
        OnPropertyChanged(nameof(OutputDescription));
    }

    /// <summary>How hard the limiter is holding the sound down right now, in dB (0 = not at all).</summary>
    public double TakeLimiterReductionDb() => _player is Audio.ToneEngine engine && IsPlaying ? engine.TakeLimiterReductionDb() : 0;

    /// <summary>Opens a song in a brand-new player: the app's own (with the equalizer), or
    /// Windows' media player for the songs only that one can play. (Each song getting its own
    /// player also matters for Windows' player: once it has played one song over the local
    /// stream server, it read only the first half-megabyte of the next one and then sat at 0:00.)</summary>
    private void OpenSource(Audio.PlaySource source, bool windows = false)
    {
        var old = _player;
        try { old.Pause(); old.Close(); } catch { /* already closed */ }
        _openSource = source;
        _streamWatch = source.IsStream ? new StreamWatch(DateTime.UtcNow) : null;
        windows |= CurrentFile is { } file && _windowsOnly.Contains(file.Path);
        var output = _settings.Output;
        if (CurrentFile is { } current && _dopRefused.Contains(current.Path))
        {
            // DoP didn't work out for this song as things are: DSD as PCM
            output = output.Clone();
            output.DsdOutput = TuneSync.Core.Models.DsdOutput.Pcm;
        }
        Audio.IPlayerEngine player = windows ? new Audio.WindowsEngine(source) : new Audio.ToneEngine(source, _equalizer.Config, output);
        player.Volume = Volume;
        player.IsMuted = IsMuted;
        if (player is Audio.ToneEngine tone)
        {
            tone.OutputChanged += () =>
            {
                OnPropertyChanged(nameof(OutputDescription));
                OnPropertyChanged(nameof(IsBitPerfect));
            };
            tone.RerouteNeeded += () =>
            {
                if (!ReferenceEquals(_player, tone) || CurrentFile is not { } file || !_dopRefused.Add(file.Path)) return;
                _pendingRestore = (CurrentPosition, true);
                OpenSource(SourceFor(file.Path));
            };
        }
        _player = player;
        Wire(player);
        player.Open();
        OnPropertyChanged(nameof(IsToneBypassed));
    }

    // ---- getting a stuck network song going again ----

    /// <summary>A network song being watched: when its position last moved, and how many times
    /// it has been reopened.</summary>
    private sealed class StreamWatch(DateTime since)
    {
        public DateTime LastProgress = since;
        public TimeSpan LastPosition = TimeSpan.MinValue;
        /// <summary>The last place it was heard playing past the start - kept when a failing
        /// player falls back to 0:00, so the song can be reopened where it was.</summary>
        public TimeSpan LastGood = TimeSpan.Zero;
        public int Retries;
    }

    /// <summary>Where to reopen a network song whose player failed: its own position, unless it
    /// fell back to 0:00 - then the last place it was heard playing.</summary>
    private TimeSpan ResumeAfterFailure(StreamWatch watch)
    {
        var position = _player.Position;
        return position < TimeSpan.FromSeconds(0.5) && watch.LastGood > position ? watch.LastGood : position;
    }

    private StreamWatch? _streamWatch;

    /// <summary>DSD songs DoP couldn't go out for (as the output is set now): played as PCM.</summary>
    private readonly HashSet<string> _dopRefused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Windows' media player now and then stops reading a network song a moment after
    /// it starts and sits at 0:00 (or stops mid-song) although the data is there. If the position
    /// hasn't moved for 2 s while playing and enough of the song has arrived, the song is opened
    /// again in a fresh player at the same spot - up to three times.</summary>
    private void WatchStream(TimeSpan position)
    {
        if (_streamWatch is not { } watch || CurrentFile is not { } file || _isSeeking || _seekTarget is not null) return;
        // Still opening (never got as far as playing) - watched too, but only for a dead download.
        var opening = !IsPlaying && _player.NaturalDuration is null;
        if (!IsPlaying && !opening) return;
        var now = DateTime.UtcNow;
        if (position != watch.LastPosition)
        {
            watch.LastPosition = position;
            if (position > TimeSpan.FromSeconds(0.5)) watch.LastGood = position;
            watch.LastProgress = now;
            return;
        }
        if (now - watch.LastProgress < TimeSpan.FromSeconds(2) || watch.Retries >= 3) return;

        if (_network.Cache.Peek(file.Path) is not { } item) return;
        // Its download died (stopped by a song picked meanwhile): the player waits for bytes
        // that will never come. Opening it again starts the download anew.
        var dead = !item.IsComplete && !item.IsDownloading;
        // Otherwise only when the music is there to play (a slow network is a different matter).
        if (!dead && (opening || item.Length <= 0)) return;
        var duration = DurationSeconds > 1 ? DurationSeconds : file.Duration.TotalSeconds;
        var neededByte = duration > 0 ? (long)(item.Length * Math.Min(1, (position.TotalSeconds + 5) / duration)) : 0;
        if (!dead && !item.IsComplete && item.Downloaded < Math.Max(neededByte, 1_500_000)) return;

        ReopenStream(watch, file.Path, position);
    }

    /// <summary>Opens the watched song again in a fresh player, to carry on from
    /// <paramref name="position"/>. Opening starts a new watch; the count of attempts is carried
    /// over to it, so the retries stay limited.</summary>
    private void ReopenStream(StreamWatch watch, string path, TimeSpan position)
    {
        var retries = watch.Retries + 1;
        _pendingRestore = (position, true);
        OpenSource(SourceFor(path));
        if (_streamWatch is not null) _streamWatch.Retries = retries;
    }

    /// <summary>What the player opens: a network song is streamed from the local cache server
    /// (it starts playing while it downloads); everything else is the file itself.</summary>
    private Audio.PlaySource SourceFor(string path)
    {
        if (!TuneSync.Core.Models.RemotePath.IsRemote(path)) return new Audio.PlaySource(path);
        _network.Cache.Focus(path); // this song gets the connection(s) and the bandwidth
        return new Audio.PlaySource(_network.Cache.Open(path), _network.Server);
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
            if (next is null || !TuneSync.Core.Models.RemotePath.IsRemote(next.Path)) return;
            // Only once this song is fully here: fetching both at once halves the speed of the
            // one being listened to (and takes a second connection).
            var current = CurrentFile;
            if (current is not null && TuneSync.Core.Models.RemotePath.IsRemote(current.Path))
            {
                var nextPath = next.Path;
                _network.Cache.Open(current.Path).Completion.ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                        {
                            if (CurrentFile == current) _network.Cache.Prefetch(nextPath);
                        });
                }, TaskScheduler.Default);
            }
            else
            {
                _network.Cache.Prefetch(next.Path);
            }
        }
        catch
        {
            // best effort
        }
    }
}
