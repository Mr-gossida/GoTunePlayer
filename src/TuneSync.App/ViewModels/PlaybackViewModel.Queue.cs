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
    private readonly List<LibraryFileViewModel> _shuffleBag = [];
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
        _shuffleBag.RemoveAll(leaving.Contains);
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

        var currentIndex = CurrentFile is null ? -1 : files.IndexOf(CurrentFile);
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
}
