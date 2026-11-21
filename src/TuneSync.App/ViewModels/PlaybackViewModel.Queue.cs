using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

// Which song plays next: list order, shuffle, repeat, the play history and the songs reserved
// with "play next".
public sealed partial class PlaybackViewModel
{
    /// <summary>The songs played in this shuffle round, by path (so a rescan, which rebuilds the
    /// rows, doesn't end the round).</summary>
    private readonly HashSet<string> _shufflePlayed = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Songs played before the current one, newest last; "이전" walks back through it.</summary>
    private readonly LinkedList<LibraryFileViewModel> _history = new();
    private const int HistoryLimit = 500;

    private void RememberPlayed(LibraryFileViewModel file)
    {
        _history.AddLast(file);
        // Past the limit only the oldest goes (clearing it all lost "이전" in a long shuffle).
        while (_history.Count > HistoryLimit) _history.RemoveFirst();
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
                var previous = _history.Last!.Value;
                _history.RemoveLast();
                if (!files.Contains(previous)) continue; // filtered out or removed since
                PlayFile(previous, fromHistory: true);
                return;
            }
        }

        PlayRelative(-1);
    }

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

    /// <summary>"다음" button: always moves on (wrapping around), regardless of repeat mode.</summary>
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
        _shufflePlayed.Clear(); // a fresh shuffle each time it's turned on
        _settings.Shuffle = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        _settings.Repeat = value;
        AppSettingsStore.Save(_settings);
    }

    public string ShuffleToolTip => IsShuffle ? "무작위 재생: 켬" : "무작위 재생: 끔";

    public string RepeatToolTip => Repeat switch
    {
        RepeatMode.All => "반복: 전체 반복",
        RepeatMode.One => "반복: 한 곡 반복",
        _ => "반복: 끔",
    };

    /// <summary>The playlist in the 플레이 screen's order (its sort and grouping), including songs
    /// of groups folded out of sight.</summary>
    private List<LibraryFileViewModel> CurrentList() => _library.QueueOrder();

    /// <summary>Where the playing song stood when it was taken out of the list: next/previous
    /// carry on from there instead of jumping back to the top.</summary>
    private int _removedCurrentIndex = -1;

    private void OnFilesRemoving(IReadOnlyList<LibraryFileViewModel> files)
    {
        var leaving = files.ToHashSet();
        foreach (var file in files.Where(f => f.IsUpNext)) CancelUpNext(file);
        if (CurrentFile is not { } current || !leaving.Contains(current)) return;
        var list = CurrentList();
        var index = list.IndexOf(current);
        // The song that will take its place: the first one after it that stays.
        _removedCurrentIndex = index < 0 ? -1 : list.Take(index).Count(f => !leaving.Contains(f));
    }

    /// <summary>Previous/next in list order, wrapping around at either end.</summary>
    private void PlayRelative(int step)
    {
        var files = CurrentList();
        if (files.Count == 0) return;

        var currentIndex = IndexOfCurrent(files);
        var anchor = _removedCurrentIndex;
        _removedCurrentIndex = -1;
        if (currentIndex < 0 && anchor >= 0)
        {
            // The playing song was taken out: "next" is the one now in its place.
            var at = step > 0 ? anchor : anchor - 1;
            PlayFile(files[(at % files.Count + files.Count) % files.Count]);
            return;
        }
        var nextIndex = ((currentIndex < 0 ? 0 : currentIndex + step) % files.Count + files.Count) % files.Count;
        PlayFile(files[nextIndex]);
    }

    /// <summary>Shuffle plays every track of the list once, in random order, before any plays
    /// again. Null when the round is over and shouldn't start again (repeat off).</summary>
    private LibraryFileViewModel? NextShuffled(bool refillWhenEmpty)
    {
        var files = CurrentList();
        if (files.Count == 0) return null;

        // The playing track is part of the round, also when it was picked by hand or before
        // shuffle was turned on: a round goes on from whatever plays, not only from "다음".
        var playing = PlayingPath;
        if (playing is not null) _shufflePlayed.Add(playing);
        var left = files.Where(f => !_shufflePlayed.Contains(f.Path)).ToList();
        if (left.Count == 0)
        {
            // Every track has played: the round is over. A new round leaves out the track
            // playing now, so it doesn't come straight back.
            _shufflePlayed.Clear();
            if (!refillWhenEmpty) return null;
            if (playing is not null) _shufflePlayed.Add(playing);
            left = files.Where(f => !_shufflePlayed.Contains(f.Path)).ToList();
            if (left.Count == 0) return files[0]; // a one-track list: that track again
        }
        return left[Random.Shared.Next(left.Count)];
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
        var index = IndexOfCurrent(files);
        if (_library.IsScanning && (index < 0 || index == files.Count - 1))
        {
            // A rescan is still bringing the songs back: this one's row, or the ones after it, may
            // not be there yet. Stop meanwhile and go on once the list is whole again, rather than
            // stop for good or start over from the top.
            Stop();
            _advanceAfterScan = true;
            return;
        }
        if (index >= 0 && index < files.Count - 1)
            PlayFile(files[index + 1]);
        else if (Repeat == RepeatMode.All && files.Count > 0)
            PlayFile(files[0]);
        else
            Stop();
    }

    /// <summary>A track ended while a rescan was rebuilding the list: the next one is picked when
    /// the scan is over. Anything picked, stopped or sought meanwhile calls it off.</summary>
    private bool _advanceAfterScan;

    /// <summary>Where the playing track is in <paramref name="files"/>: its own row, or else the
    /// row of the same file (a row from before a rescan, or the file was renamed while playing).</summary>
    private int IndexOfCurrent(List<LibraryFileViewModel> files)
    {
        if (CurrentFile is not { } current) return -1;
        var index = files.IndexOf(current);
        if (index >= 0) return index;
        var path = PlayingPath;
        return files.FindIndex(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    // --- a rescan builds a new row for every song: what playback holds moves over to them ---

    /// <summary>A scan added a batch of rows. Rows playback still holds from before a rescan - the
    /// playing track, the songs reserved with "다음에 재생", the play history - are swapped for the
    /// new rows of the same files, so the queue goes on from where it was (rather than stopping or
    /// starting over from the top when the track ends) and keeps its marks.</summary>
    private void OnFilesAdded(IReadOnlyList<LibraryFileViewModel> added)
    {
        var byPath = new Dictionary<string, LibraryFileViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in added) byPath.TryAdd(row.Path, row);
        HashSet<LibraryFileViewModel>? present = null;

        // The new row for one held: only when the list doesn't have the held one anymore (a file
        // can be in the list twice - added on its own and inside a folder).
        LibraryFileViewModel? NewRowFor(LibraryFileViewModel held, string path)
        {
            if (!byPath.TryGetValue(path, out var row) || ReferenceEquals(row, held)) return null;
            present ??= new HashSet<LibraryFileViewModel>(_library.Files);
            return present.Contains(held) ? null : row;
        }

        if (CurrentFile is { } current && PlayingPath is { } playing && NewRowFor(current, playing) is { } now)
            RelinkCurrent(now);

        var reserved = false;
        for (var i = 0; i < _upNext.Count; i++)
        {
            if (NewRowFor(_upNext[i], _upNext[i].Path) is not { } row) continue;
            _upNext[i] = row;
            reserved = true;
        }
        if (reserved) RenumberUpNext(); // the new rows show their place in it

        for (var node = _history.First; node is not null; node = node.Next)
            if (NewRowFor(node.Value, node.Value.Path) is { } row) node.Value = row;
    }

    /// <summary>A scan has finished: reserved songs that didn't come back with it are dropped, so
    /// the numbers on the others count only what will play, and a track that ended during the
    /// scan moves on now.</summary>
    private void OnScanFinished()
    {
        if (_upNext.Count > 0)
        {
            var present = _library.Files.ToHashSet();
            if (_upNext.RemoveAll(f => !present.Contains(f)) > 0) RenumberUpNext();
        }
        if (!_advanceAfterScan) return;
        _advanceAfterScan = false;
        AdvanceOrStop();
    }
}
