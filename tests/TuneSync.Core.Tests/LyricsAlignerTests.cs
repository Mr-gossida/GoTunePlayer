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
}
