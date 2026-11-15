using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

/// <summary>The times <see cref="LyricsAligner"/> gives to lines it could not hear.</summary>
public class LyricsAlignerFillTests
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

    /// <summary>What a recognizer would report for the lines it heard: each word, 0.3 s per character.</summary>
    private static List<TimedText> Heard(Func<int, bool> heardLine)
    {
        var heard = new List<TimedText>();
        for (var i = 0; i < Lines.Length; i++)
        {
            if (!heardLine(i)) continue;
            var t = Starts[i];
            foreach (var word in Lines[i].Split(' '))
            {
                var length = word.Length * 0.3;
                heard.Add(new TimedText(TimeSpan.FromSeconds(t), TimeSpan.FromSeconds(t + length), word));
                t += length + 0.1;
            }
        }
        return heard;
    }

    private static List<AlignedLine> Align(Func<int, bool> heardLine, double duration) =>
        LyricsAligner.Align(Lines, Heard(heardLine), TimeSpan.FromSeconds(duration));

    private static void AssertMovesForward(IReadOnlyList<AlignedLine> result, int from = 1)
    {
        for (var i = Math.Max(1, from); i < result.Count; i++)
            Assert.True(result[i].Time > result[i - 1].Time, $"line {i} ({result[i].Time}) is not after line {i - 1} ({result[i - 1].Time})");
    }

    // --- after the last line that was heard ---

    [Fact]
    public void Lines_after_the_last_heard_one_follow_it_in_order_as_estimates()
    {
        var result = Align(line => line <= 2, duration: 60);

        for (var i = 0; i <= 2; i++) Assert.True(result[i].IsConfident);
        for (var i = 3; i < Lines.Length; i++) Assert.False(result[i].IsConfident);
        Assert.InRange(result[2].Time.TotalSeconds, Starts[2] - 0.35, Starts[2] + 0.35);
        AssertMovesForward(result, from: 3);
        Assert.True(result[^1].Time < TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Lines_after_the_last_heard_one_take_the_room_the_song_has_left()
    {
        // The same lyrics with little left of the song, and with plenty: the unheard last lines
        // are sung closer together in the first, and still end before the song does.
        var tight = Align(line => line <= 2, duration: 40);
        var roomy = Align(line => line <= 2, duration: 60);

        Assert.True(tight[^1].Time < TimeSpan.FromSeconds(40));
        Assert.True(tight[^1].Time < roomy[^1].Time);
        AssertMovesForward(tight, from: 3);
    }

    [Fact]
    public void A_very_long_song_does_not_stretch_the_last_lines_without_limit()
    {
        // Ten minutes left after the last heard line: the rest is still sung at a plausible pace
        // (no more than twice the usual), not spread over all of it.
        var result = Align(line => line <= 2, duration: 600);

        Assert.True(result[^1].Time < TimeSpan.FromSeconds(Starts[2] + 60));
        AssertMovesForward(result, from: 3);
    }

    [Fact]
    public void Lines_past_the_end_of_a_short_song_still_move_forward()
    {
        // The song ends right after the last heard line: the estimates are squeezed in at its
        // end, but each still comes after the one before.
        var result = Align(line => line <= 2, duration: 24);

        AssertMovesForward(result, from: 3);
    }

    [Fact]
    public void With_the_length_unknown_the_last_lines_go_on_at_the_sung_pace()
    {
        var result = Align(line => line <= 2, duration: 0);

        AssertMovesForward(result, from: 3);
        // Roughly a line's singing time plus a breath apart (a few seconds), not bunched up.
        for (var i = 3; i < Lines.Length; i++)
            Assert.InRange((result[i].Time - result[i - 1].Time).TotalSeconds, 1.5, 12);
    }

    [Fact]
    public void Lines_before_the_first_heard_one_are_placed_ahead_of_it()
    {
        var result = Align(line => line >= 2, duration: 60);

        Assert.False(result[0].IsConfident);
        Assert.False(result[1].IsConfident);
        Assert.True(result[2].IsConfident);
        Assert.True(result[0].Time >= TimeSpan.Zero);
        AssertMovesForward(result);
    }

    // --- nothing heard at all ---

    [Fact]
    public void With_nothing_heard_the_lines_start_after_a_short_intro_and_end_before_the_song_does()
    {
        var result = LyricsAligner.Align(Lines, [], TimeSpan.FromSeconds(200));

        Assert.All(result, r => Assert.False(r.IsConfident));
        Assert.InRange(result[0].Time.TotalSeconds, 1, 10);
        AssertMovesForward(result);
        Assert.True(result[^1].Time < TimeSpan.FromSeconds(200));
    }

    [Fact]
    public void With_nothing_heard_a_longer_song_gets_more_time_per_line()
    {
        var shortSong = LyricsAligner.Align(Lines, [], TimeSpan.FromSeconds(30));
        var longSong = LyricsAligner.Align(Lines, [], TimeSpan.FromSeconds(200));

        var shortSpan = shortSong[^1].Time - shortSong[0].Time;
        var longSpan = longSong[^1].Time - longSong[0].Time;
        Assert.True(longSpan > shortSpan);
        AssertMovesForward(shortSong);
    }

    [Fact]
    public void With_nothing_heard_and_the_length_unknown_the_lines_start_at_zero_at_the_usual_pace()
    {
        var result = LyricsAligner.Align(Lines, [], TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, result[0].Time);
        AssertMovesForward(result);
    }

    [Fact]
    public void Words_that_match_none_of_the_lyrics_count_as_nothing_heard()
    {
        var heard = new List<TimedText> { new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6), "thank you") };

        var result = LyricsAligner.Align(Lines, heard, TimeSpan.FromSeconds(200));

        Assert.All(result, r => Assert.False(r.IsConfident));
        AssertMovesForward(result);
    }

    [Fact]
    public void No_lines_give_no_times()
    {
        Assert.Empty(LyricsAligner.Align([], [], TimeSpan.FromSeconds(60)));
    }
}
