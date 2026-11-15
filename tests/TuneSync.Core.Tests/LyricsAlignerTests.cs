using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

public class LyricsAlignerTests
{
    private static readonly string[] Lines =
    [
        "당신은 날 설레게 만들어",
        "조용한 내 마음 자꾸만 춤추게 해",
        "얼마나 얼마나 날 떨리게 하는지",
        "당신이 이 밤을 항상 잠 못 들게 해",
        "오늘 밤 잠이 오질 않네요",
        "보고 싶은 그대여",
    ];

    private static readonly double[] Starts = [12.0, 16.5, 21.0, 25.4, 31.0, 35.2];

    /// <summary>What a recognizer would report: each word of each line, 0.3 s per character.</summary>
    private static List<TimedText> Heard(IReadOnlyList<string> lines, IReadOnlyList<double> starts, Func<int, string, string?>? change = null)
    {
        var heard = new List<TimedText>();
        for (var i = 0; i < lines.Count; i++)
        {
            var t = starts[i];
            foreach (var word in lines[i].Split(' '))
            {
                var text = change is null ? word : change(i, word);
                var length = word.Length * 0.3;
                if (text is not null) heard.Add(new TimedText(TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t + length), text));
                t += length + 0.1;
            }
        }
        return heard;
    }

    private static void AssertNear(double expected, TimeSpan actual, double tolerance = 0.35) =>
        Assert.InRange(actual.TotalSeconds, expected - tolerance, expected + tolerance);

    [Fact]
    public void Times_each_line_where_its_words_were_heard()
    {
        var result = LyricsAligner.Align(Lines, Heard(Lines, Starts), TimeSpan.FromSeconds(60));

        Assert.Equal(Lines.Length, result.Count);
        for (var i = 0; i < Lines.Length; i++)
        {
            AssertNear(Starts[i], result[i].Time);
            Assert.True(result[i].IsConfident);
        }
    }

    [Fact]
    public void Tolerates_misheard_and_missed_words()
    {
        // Typical recognition slips: a different final consonant, a dropped word, a wrong word.
        var heard = Heard(Lines, Starts, (_, word) => word switch
        {
            "설레게" => "설래게",
            "자꾸만" => null,
            "떨리게" => "떠리게",
            "항상" => "향상",
            "그대여" => "그대요",
            _ => word,
        });

        var result = LyricsAligner.Align(Lines, heard, TimeSpan.FromSeconds(60));

        for (var i = 0; i < Lines.Length; i++) AssertNear(Starts[i], result[i].Time);
    }

    [Fact]
    public void Estimates_a_line_that_was_not_heard_between_its_neighbors()
    {
        var heard = Heard(Lines, Starts, (line, word) => line == 2 ? null : word);

        var result = LyricsAligner.Align(Lines, heard, TimeSpan.FromSeconds(60));

        Assert.False(result[2].IsConfident);
        Assert.InRange(result[2].Time.TotalSeconds, Starts[1] + 1, Starts[3] - 1);
        Assert.True(result[1].IsConfident && result[3].IsConfident);
    }

    [Fact]
    public void Ignores_words_imagined_during_the_intro()
    {
        var heard = Heard(Lines, Starts);
        heard.Insert(0, new TimedText(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), "감사합니다"));
        heard.Insert(1, new TimedText(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), "당신"));

        var result = LyricsAligner.Align(Lines, heard, TimeSpan.FromSeconds(60));

        AssertNear(Starts[0], result[0].Time);
    }

    [Fact]
    public void A_repeated_chorus_keeps_each_pass_in_order()
    {
        string[] lines = ["오늘 밤 잠이 오질 않네요", "보고 싶은 그대여", "오늘 밤 잠이 오질 않네요", "보고 싶은 그대여"];
        double[] starts = [10, 14, 40, 44];

        var result = LyricsAligner.Align(lines, Heard(lines, starts), TimeSpan.FromSeconds(60));

        for (var i = 0; i < lines.Length; i++) AssertNear(starts[i], result[i].Time);
    }

    [Fact]
    public void With_nothing_heard_spreads_the_lines_over_the_song()
    {
        var result = LyricsAligner.Align(Lines, [], TimeSpan.FromSeconds(200));

        Assert.All(result, r => Assert.False(r.IsConfident));
        for (var i = 1; i < result.Count; i++) Assert.True(result[i].Time > result[i - 1].Time);
        Assert.True(result[^1].Time < TimeSpan.FromSeconds(200));
    }

    [Fact]
    public void Writes_lrc_that_the_player_reads_back()
    {
        var lrc = LrcWriter.Write([(TimeSpan.FromSeconds(12.346), "당신은 날 설레게 만들어"), (TimeSpan.FromSeconds(75.5), "보고 싶은 그대여")]);

        Assert.Equal("[00:12.35]당신은 날 설레게 만들어\n[01:15.50]보고 싶은 그대여", lrc);
        var parsed = LrcParser.Parse(lrc);
        Assert.Equal(2, parsed.Count);
        Assert.Equal("보고 싶은 그대여", parsed[1].Text);
    }

    [Fact]
    public void Plain_lines_drop_blank_lines_and_old_timestamps()
    {
        Assert.Equal(["첫 줄", "둘째 줄"], LrcWriter.PlainLines("첫 줄\r\n\r\n[00:12.00]둘째 줄\n   \n"));
    }

    [Fact]
    public void Lyrics_with_any_line_break_style_split_into_lines()
    {
        // Some taggers separate lines with a lone CR (old Mac style) - those used to run together.
        const string lyrics = "사랑이란 대체 뭘까\r더 멀어지기 위해\r\n내게 왔던가\u2028적당한 거리라도";
        Assert.Equal(["사랑이란 대체 뭘까", "더 멀어지기 위해", "내게 왔던가", "적당한 거리라도"], LrcParser.SplitLines(lyrics));
        Assert.Equal(4, LrcWriter.PlainLines(lyrics).Count);
        Assert.Equal(2, LrcParser.Parse("[00:01.00]첫 줄\r[00:02.00]둘째 줄").Count);
    }

    [Theory]
    [InlineData("당신은 날 설레게 만들어 baby", "ko")]
    [InlineData("I could take it to the top", "en")]
    [InlineData("君の名は", "ja")]
    public void Picks_the_recognition_language_from_the_lyrics(string text, string expected) =>
        Assert.Equal(expected, LyricsSyncEngine.LanguageOf([text]));

    [Fact]
    public void Combining_passes_takes_the_time_most_of_them_agree_on()
    {
        static AlignedLine Line(double seconds, bool heard = true) => new("가사", TimeSpan.FromSeconds(seconds), heard);
        var combined = LyricsSyncEngine.Combine(
        [
            //  agreed by two     one stray pass   only one heard it   all differ    none heard it
            [Line(10.0),        Line(40.0),      Line(50, false),    Line(70.0),   Line(90, false)],
            [Line(10.4),        Line(20.2),      Line(52.0),         Line(75.0),   Line(95, false)],
            [Line(30.0),        Line(20.0),      Line(58, false),    Line(80.0),   Line(99, false)],
        ]);

        Assert.Equal([10.2, 20.1, 52.0, 70.0, 90.0], combined.Select(l => Math.Round(l.Time.TotalSeconds, 2)));
        // Passes that heard a line at different times, and lines nobody heard, are worth a check.
        Assert.Equal([true, true, true, false, false], combined.Select(l => l.IsConfident));
    }

    [Fact]
    public void Singing_is_found_in_the_vocals_and_cut_into_pieces_at_its_pauses()
    {
        // A tone from 1 s to 3 s and from 3.1 s to 4 s (a breath, not a pause), then from 20 s to 21 s.
        var vocals = new float[AudioDecoder.SampleRate * 25];
        foreach (var (from, to) in new[] { (1.0, 3.0), (3.1, 4.0), (20.0, 21.0) })
            for (var i = (int)(from * AudioDecoder.SampleRate); i < (int)(to * AudioDecoder.SampleRate); i++)
                vocals[i] = 0.3f * MathF.Sin(i * 0.2f);

        var voice = VoiceActivity.Detect(vocals);

        Assert.Equal(2, voice.Regions.Count);
        Assert.InRange(voice.Regions[0].Start, 0.95, 1.05);
        Assert.InRange(voice.Regions[0].End, 3.95, 4.05);
        var windows = voice.Windows();
        Assert.Equal(2, windows.Count); // the long pause is left out
        Assert.InRange(windows[0].Start, 0.65, 0.75);
        Assert.InRange(windows[0].End, 4.25, 4.35);
        Assert.InRange(windows[1].Start, 19.65, 19.75);
    }

    [Fact]
    public void Hears_korean_by_sound_not_spelling()
    {
        // Sung and heard: ㅔ/ㅐ alike, tense consonants, a final consonant moved to the next syllable.
        string[] lines = ["네게 줄게 했어요", "이제는 안녕"];
        var heard = new List<TimedText>
        {
            new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "내개"),
            new(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(7), "쭐께"),
            new(TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(8.5), "해써요"),
            new(TimeSpan.FromSeconds(12), TimeSpan.FromSeconds(13), "이제는"),
            new(TimeSpan.FromSeconds(13), TimeSpan.FromSeconds(14), "안녕"),
        };

        var result = LyricsAligner.Align(lines, heard, TimeSpan.FromSeconds(60));

        Assert.True(result[0].IsConfident);
        AssertNear(5, result[0].Time);
        AssertNear(12, result[1].Time);
    }
}
