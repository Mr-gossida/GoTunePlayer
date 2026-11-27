using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.App.ViewModels;

/// <summary>"4:13", or "1:02:03" for something over an hour.</summary>
internal static class Lengths
{
    public static string Text(TimeSpan length) => length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss");

    /// <summary>"길이 일치" within 3 s, else how far off ("길이 +12초"); empty when either is unknown.</summary>
    public static string Match(TimeSpan? ours, TimeSpan? theirs)
    {
        if (ours is not { TotalSeconds: > 1 } a || theirs is not { TotalSeconds: > 1 } b) return "";
        var diff = (b - a).TotalSeconds;
        return Math.Abs(diff) <= 3 ? "길이 일치" : $"길이 {(diff > 0 ? "+" : "−")}{Math.Abs(Math.Round(diff)):0}초";
    }
}

/// <summary>One answer of 태그 찾기, as a card: what it would put in the tags and where it's from.</summary>
public sealed partial class TagCandidateViewModel : ObservableObject
{
    public TagCandidateViewModel(TagCandidate candidate, TimeSpan? songLength, bool isRecommended)
    {
        Candidate = candidate;
        IsRecommended = isRecommended;
        var album = candidate.Album.Length == 0 ? "" : candidate.Year is { } year ? $"{candidate.Album} ({year})" : candidate.Album;
        Subtitle = string.Join(" · ", new[] { candidate.Artist, album.Length > 0 ? album : candidate.Year?.ToString() ?? "" }.Where(s => s.Length > 0));
        var details = new List<string>();
        if (candidate.Track is { } track) details.Add(candidate.TrackCount is { } count && count >= track ? $"트랙 {track}/{count}" : $"트랙 {track}");
        if (candidate.Disc is { } disc) details.Add($"디스크 {disc}");
        if (candidate.Genre.Length > 0) details.Add(candidate.Genre);
        if (candidate.Duration is { } length) details.Add(Lengths.Text(length));
        Details = string.Join(" · ", details);
        LengthNote = Lengths.Match(songLength, candidate.Duration);
    }

    public TagCandidate Candidate { get; }

    /// <summary>The best fit (put in by the search itself).</summary>
    public bool IsRecommended { get; }

    public string Source => Candidate.Source;
    public string Title => Candidate.Title;

    /// <summary>"아이유 · Palette (2017)".</summary>
    public string Subtitle { get; }

    /// <summary>"트랙 3/12 · 디스크 1 · K-Pop · 4:13".</summary>
    public string Details { get; }

    /// <summary>"길이 일치", "길이 +12초", or empty.</summary>
    public string LengthNote { get; }

    public bool LengthMatches => LengthNote == "길이 일치";

    public string ScoreText => $"{Math.Round(Candidate.Score * 100):0}%";

    public string ScoreToolTip => $"이 곡과 맞는 정도 {ScoreText}: 제목·아티스트가 가장 크게, 앨범·길이가 그다음으로 반영됩니다.";

    /// <summary>"바뀌는 칸: 앨범 · 연도 · 트랙", or that it's what the tags already say.</summary>
    [ObservableProperty]
    private string _changes = "";

    /// <summary>It would change something (else <see cref="Changes"/> says it's the tags as they are).</summary>
    [ObservableProperty]
    private bool _hasChanges;

    /// <summary>The one whose values are in the editor now.</summary>
    [ObservableProperty]
    private bool _isApplied;
}

/// <summary>One answer of 가사 찾기, as a card: where it's from, synced or not, its language and
/// length, and its first lines.</summary>
public sealed partial class LyricsCandidateViewModel : ObservableObject
{
    public LyricsCandidateViewModel(LyricsCandidate candidate, TimeSpan? songLength, bool isRecommended)
    {
        Candidate = candidate;
        IsRecommended = isRecommended;
        Heading = candidate.FileName is { } file ? file
            : string.Join(" · ", new[] { candidate.Title, candidate.Artist }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var lines = LyricLines(candidate.Lyrics);
        var details = new List<string>();
        if (candidate.FileName is null && !string.IsNullOrWhiteSpace(candidate.Album)) details.Add(candidate.Album!);
        if (candidate.Duration is { } length) details.Add(Lengths.Text(length));
        details.Add($"{lines.Count}줄");
        Details = string.Join(" · ", details);
        LengthNote = candidate.LengthMatches is null ? Lengths.Match(songLength, candidate.Duration)
            : candidate.LengthMatches == true ? "길이 일치" : Lengths.Match(songLength, candidate.Duration);
        Preview = string.Join("  /  ", lines.Take(2));
        ToolTip = string.Join(Environment.NewLine, lines.Take(12)) + (lines.Count > 12 ? $"{Environment.NewLine}… ({lines.Count}줄)" : "")
                  + $"{Environment.NewLine}{Environment.NewLine}누르면 이 가사로 바뀝니다. 저장하기 전까지 파일은 그대로입니다.";
        Lyrics = candidate.Lyrics.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
    }

    /// <summary>The first dozen lines, and what a click does.</summary>
    public string ToolTip { get; }

    public LyricsCandidate Candidate { get; }

    public bool IsRecommended { get; }

    public string Source => Candidate.Source;

    /// <summary>"밤편지 · 아이유", or the file's name for one beside the song.</summary>
    public string Heading { get; }

    /// <summary>"Palette · 4:13 · 32줄".</summary>
    public string Details { get; }

    public string KindText => Candidate.IsSynced ? "싱크" : "일반";

    public bool IsSynced => Candidate.IsSynced;

    public string Language => Candidate.Language;

    public bool HasLanguage => Language.Length > 0;

    public string LengthNote { get; }

    public bool LengthMatches => LengthNote == "길이 일치";

    public bool HasLengthNote => LengthNote.Length > 0;

    /// <summary>The first two sung lines, timestamps aside.</summary>
    public string Preview { get; }

    /// <summary>As the editor's text box shows lyrics (Windows line breaks).</summary>
    public string Lyrics { get; }

    [ObservableProperty]
    private bool _isApplied;

    /// <summary>The sung lines: blank ones, [ar:]-style tags and timestamps left out.</summary>
    private static List<string> LyricLines(string lyrics)
    {
        var synced = LrcParser.Parse(lyrics);
        if (synced.Count > 0) return [.. synced.Select(l => l.Text.Trim()).Where(t => t.Length > 0)];
        return [.. lyrics.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !(l.StartsWith('[') && l.EndsWith(']')))];
    }
}
