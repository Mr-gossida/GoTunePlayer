using TuneSync.Core.Models;
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
    public void A_line_whose_beginning_was_heard_is_placed_by_it()
    {
        // "걱정 마 I believe": the lead sings 걱정 마, a backing chorus the rest, which the
        // separated vocals don't carry - only a quarter of the line is heard, its start exactly.
        string[] lines = [.. Lines.Take(3), "걱정 마 I believe and I believe", .. Lines.Skip(4)];
        var heard = Heard(lines, Starts, (i, word) => i == 3 && word is not ("걱정" or "마") ? null : word);

        var result = LyricsAligner.Align(lines, heard, TimeSpan.FromSeconds(60));

        AssertNear(Starts[3], result[3].Time);
        Assert.True(result[3].IsConfident);
    }

    [Fact]
    public async Task Lines_no_pass_heard_are_listened_to_again_with_their_words_as_the_prompt()
    {
        // Every pass misses lines 2 and 3; listened to again from line 1 to line 4, they're heard.
        var missing = Heard(Lines, Starts, (i, word) => i is 2 or 3 ? null : word);
        LyricsSyncEngine.Gap? asked = null;
        var result = await LyricsSyncEngine.SyncAsync(Lines, TimeSpan.FromSeconds(60), null, LyricsSyncEngine.Passes(false),
            _ => Task.FromResult(missing),
            gap =>
            {
                asked = gap;
                return Task.FromResult(Heard(Lines, Starts).Where(w => w.Start.TotalSeconds >= gap.Start && w.Start.TotalSeconds < gap.End).ToList());
            });

        Assert.NotNull(asked);
        Assert.Contains(Lines[2], asked.Value.Prompt);
        Assert.Contains(Lines[3], asked.Value.Prompt);
        AssertNear(Starts[1], TimeSpan.FromSeconds(asked.Value.Start));
        AssertNear(Starts[2], result[2].Time);
        AssertNear(Starts[3], result[3].Time);
        Assert.False(result[2].IsConfident); // given the words, still worth a listen
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

    [Fact]
    public void A_heard_chorus_goes_to_the_repeat_sung_then_not_to_a_later_one()
    {
        // Verse, chorus, verse, then the chorus twice - but recognition heard only the first of
        // those last two. Its words fit either repeat equally well; the times say which.
        string[] lines =
        [
            "처음 부르는 노래의 첫 줄", "두 번째 다른 가사가 있어",
            "사랑해 사랑해요 그대만", "너만을 사랑해요 영원히",
            "세 번째 가사는 또 달라", "네 번째 줄도 새로워",
            "사랑해 사랑해요 그대만", "너만을 사랑해요 영원히",
            "사랑해 사랑해요 그대만", "너만을 사랑해요 영원히",
        ];
        double[] starts = [10, 14, 18, 22, 26, 30, 34, 38, 42, 46];
        var heard = Heard(lines, starts, (i, word) => i >= 8 ? null : word);

        var result = LyricsAligner.Align(lines, heard, TimeSpan.FromSeconds(60));

        AssertNear(34, result[6].Time);
        AssertNear(38, result[7].Time);
        Assert.True(result[6].IsConfident && result[7].IsConfident);
        Assert.False(result[8].IsConfident || result[9].IsConfident);
        Assert.True(result[8].Time > result[7].Time && result[9].Time > result[8].Time);
    }

    [Fact]
    public void A_line_sung_twice_in_a_row_and_heard_once_goes_to_the_copy_the_pace_says()
    {
        // The times at the end of a real song as the alignment left them: the first of two equal
        // lines was heard (at 192.5 s) but given to the second, and the first squeezed in at
        // 187 s, in the middle of the held line before. Lines come every 6-7 s; only the first
        // copy keeps that pace on both sides.
        string[] lines =
        [
            "하나 둘 셋 넷 다섯", "여섯 일곱 여덟 아홉 열", "길게 부르는 줄",
            "두 번 부르는 줄", "두 번 부르는 줄", "그 다음 줄", "끝에서 둘째 줄",
        ];
        double?[] times = [168.6, 176.4, 182.1, 187.1, 192.5, 205.7, 212.4];
        bool[] confident = [true, true, true, false, true, true, true];

        LyricsAligner.ShiftAmongRepeats(lines, times, confident);

        Assert.Equal([true, true, true, true, false, true, true], confident);
        Assert.Equal(192.5, times[3]);
    }

    [Fact]
    public void A_heard_repeat_stays_where_the_pace_does_not_clearly_say_otherwise()
    {
        // Here the heard copy is the second one: lines come every 6.5 s as it is, and giving it
        // to the first would make that uneven, so nothing moves.
        string[] lines = ["첫 줄은 이렇게", "둘째 줄은 저렇게", "같은 줄 또 불러", "같은 줄 또 불러", "마지막 줄", "정말 마지막"];
        double?[] times = [10, 16.5, 21, 29.5, 36, 42.5];
        bool[] confident = [true, true, false, true, true, true];

        LyricsAligner.ShiftAmongRepeats(lines, times, confident);

        Assert.Equal([true, true, false, true, true, true], confident);
    }

    [Fact]
    public void Lines_not_heard_are_spread_over_the_singing_not_over_an_instrumental()
    {
        // Two lines heard, at 10 s and 40 s; the two between them weren't. The singer sings from
        // 10 to 20 s and from 38 s on - 20 to 38 s is an instrumental.
        string[] lines = ["창밖에 비가 내리면", "우산을 들고 나가요", "젖은 거리를 걸으며", "그대 생각을 해요"];
        var heard = Heard(lines, [10, 13, 16, 40], (i, word) => i is 1 or 2 ? null : word);
        var vocals = new float[AudioDecoder.SampleRate * 60];
        foreach (var (from, to) in new[] { (10.0, 20.0), (38.0, 50.0) })
            for (var i = (int)(from * AudioDecoder.SampleRate); i < (int)(to * AudioDecoder.SampleRate); i++)
                vocals[i] = 0.3f * MathF.Sin(i * 0.2f);

        var result = LyricsAligner.Align(lines, heard, TimeSpan.FromSeconds(60), VoiceActivity.Detect(vocals));

        Assert.InRange(result[1].Time.TotalSeconds, 11, 20);
        Assert.InRange(result[2].Time.TotalSeconds, result[1].Time.TotalSeconds + 1, 20);
    }

    [Fact]
    public void Refilling_after_combining_keeps_heard_times_in_order_and_spreads_the_rest()
    {
        static AlignedLine Line(double seconds, bool heard) => new("가사 한 줄입니다", TimeSpan.FromSeconds(seconds), heard);
        // Heard at 10 and 30 s; line 1 was heard (at disputed times, 14 s kept); line 2 nobody
        // heard and its old estimate (35 s) is past the next heard line; line 4 was heard at a
        // time before line 3, out of order.
        var lines = new List<AlignedLine> { Line(10, true), Line(14, false), Line(35, false), Line(30, true), Line(25, true) };

        var result = LyricsAligner.Refill(lines, TimeSpan.FromSeconds(60), null, [true, true, false, true, true]);

        AssertNear(10, result[0].Time);
        AssertNear(14, result[1].Time);
        Assert.InRange(result[2].Time.TotalSeconds, 15, 29);
        AssertNear(30, result[3].Time);
        Assert.True(result[4].Time > result[3].Time);
        Assert.Equal([true, false, false, true, false], result.Select(l => l.IsConfident));
    }

    [Theory]
    [InlineData(LyricsSyncCpuUsage.Low, 16, 4)]
    [InlineData(LyricsSyncCpuUsage.Normal, 16, 8)]
    [InlineData(LyricsSyncCpuUsage.Max, 16, 15)]
    [InlineData(LyricsSyncCpuUsage.Low, 2, 1)]
    [InlineData(LyricsSyncCpuUsage.Normal, 1, 1)]
    [InlineData(LyricsSyncCpuUsage.Max, 1, 1)]
    public void Cpu_usage_levels_give_a_share_of_the_processors(LyricsSyncCpuUsage usage, int processors, int threads) =>
        Assert.Equal(threads, LyricsSyncOptions.ThreadsFor(usage, processors));
}
