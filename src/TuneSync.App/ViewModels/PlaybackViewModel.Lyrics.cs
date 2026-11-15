using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

// The playing song's lyrics: loading them, the line being sung, and the intro shown before the
// first line.
public sealed partial class PlaybackViewModel
{
    /// <summary>The line being sung now (mini players), or the artist when there are no synced lyrics.</summary>
    public string CurrentLyricText => LyricAt(CurrentLyricsIndex) ?? (HasSyncedLyrics ? "♪" : NowPlayingSubtitle);
    public string NextLyricText => HasSyncedLyrics ? LyricAt(CurrentLyricsIndex + 1) ?? "" : "";

    private string? LyricAt(int index) =>
        index >= 0 && index < LyricsLines.Count && !string.IsNullOrWhiteSpace(LyricsLines[index].Text) ? LyricsLines[index].Text : null;

    partial void OnCurrentLyricsIndexChanged(int value) => RaiseLyricTexts();

    private void RaiseLyricTexts()
    {
        OnPropertyChanged(nameof(CurrentLyricText));
        OnPropertyChanged(nameof(NextLyricText));
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

    /// <summary>The playing song's synced lyrics as stored (LRC, tags included), for "싱크 편집".</summary>
    public string? SyncedLyricsText { get; private set; }

    /// <summary>Reads the song's lyrics off the UI thread: the library lives on a network share,
    /// and a slow read there used to freeze the whole window right as a song started.</summary>
    private async void LoadLyrics(LibraryFileViewModel file)
    {
        var version = ++_lyricsVersion;
        LyricsLines.Clear();
        PlainLyrics = null;
        SyncedLyricsText = null;
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
                SyncedLyricsText = lyrics;
                HasSyncedLyrics = true;
                // Worth an intro only when there's real time before the first line.
                var sung = parsed.Where(l => !string.IsNullOrWhiteSpace(l.Text)).ToList();
                var first = (sung.Count > 0 ? sung[0] : parsed[0]).Time;
                // It hands over well before the first line (about a third of the intro, 2-3 s): the
                // cover's glide and the lyrics' entrance take ~1.8 s, so they've settled by the time
                // the singing starts. An intro still lasts at least 3 s.
                if (first >= TimeSpan.FromSeconds(5))
                    _introUntil = first - TimeSpan.FromSeconds(Math.Clamp(first.TotalSeconds * 0.35, 2.0, 3.0));
            }
            else
            {
                PlainLyrics = lyrics;
                PlainLyricsLines = LrcParser.SplitLines(lyrics); // any line-break style (some files use a lone CR)
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
}
