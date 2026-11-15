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
    private bool _lastWasStream;

    /// <summary>Opens a song in the player. A network song gets a brand-new player: Windows'
    /// media player, once it has played one song over the local stream server, read only the
    /// first half-megabyte of the next one and then sat at 0:00 (a fresh one plays it at once).</summary>
    private void OpenSource(Uri uri)
    {
        var stream = uri.Scheme == Uri.UriSchemeHttp;
        if (stream || _lastWasStream)
        {
            var old = _player;
            _player = new MediaPlayer { Volume = Volume, IsMuted = IsMuted };
            Wire(_player);
            try { old.Pause(); old.Close(); } catch { /* already closed */ }
        }
        _lastWasStream = stream;
        _streamWatch = stream ? new StreamWatch(DateTime.UtcNow) : null;
        _player.Open(uri);
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

    /// <summary>Windows' media player now and then stops reading a network song a moment after
    /// it starts and sits at 0:00 (or stops mid-song) although the data is there. If the position
    /// hasn't moved for 2 s while playing and enough of the song has arrived, the song is opened
    /// again in a fresh player at the same spot - up to three times.</summary>
    private void WatchStream(TimeSpan position)
    {
        if (_streamWatch is not { } watch || CurrentFile is not { } file || _isSeeking || _seekTarget is not null) return;
        // Still opening (never got as far as playing) - watched too, but only for a dead download.
        var opening = !IsPlaying && !_player.NaturalDuration.HasTimeSpan;
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
        OpenSource(UriFor(path));
        if (_streamWatch is not null) _streamWatch.Retries = retries;
    }

    /// <summary>What the player opens: a network song is streamed from the local cache server
    /// (it starts playing while it downloads); everything else is the file itself.</summary>
    private Uri UriFor(string path)
    {
        if (!TuneSync.Core.Models.RemotePath.IsRemote(path)) return new Uri(path);
        _network.Cache.Focus(path); // this song gets the connection(s) and the bandwidth
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
