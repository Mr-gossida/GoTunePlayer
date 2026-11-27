using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class LrcParserTests
{
    [Fact]
    public void Parse_ReturnsEmpty_ForNullOrPlainText()
    {
        Assert.Empty(LrcParser.Parse(null));
        Assert.Empty(LrcParser.Parse(""));
        Assert.Empty(LrcParser.Parse("그냥 가사입니다\n줄바꿈만 있음"));
    }

    [Fact]
    public void Other_scripts_digits_arent_a_time_and_dont_throw()
    {
        // fullwidth and Arabic-Indic digits: \d took them, int.Parse refused them
        var lines = LrcParser.Parse("[０１:２３.４５]전각 숫자\n[٠١:٢٣.٤٥]아랍 숫자\n[00:05.00]보통 줄");

        Assert.Equal(("보통 줄", TimeSpan.FromSeconds(5)), (Assert.Single(lines).Text, lines[0].Time));
    }

    [Fact]
    public void Half_synced_lyrics_keep_their_untimed_lines_in_order()
    {
        const string lyrics = "[ar:아이유]\n[00:01.00]첫 줄\n시간 없는 줄\n\n[00:05.00]셋째 줄\n끝 줄";

        Assert.Equal(2, LrcParser.UntimedLines(lyrics));
        Assert.Equal(0, LrcParser.UntimedLines("그냥 가사\n둘째 줄")); // plain lyrics aren't half-synced
        Assert.Equal(0, LrcParser.UntimedLines("[ti:밤편지]\n[00:01.00]첫 줄"));
        Assert.Equal(
            [(TimeSpan.FromSeconds(1), "첫 줄"), (null, "시간 없는 줄"), (TimeSpan.FromSeconds(5), "셋째 줄"), ((TimeSpan?)null, "끝 줄")],
            LrcParser.ParseInFileOrder(lyrics));
    }

    [Fact]
    public void Parse_ExtractsTimeAndText()
    {
        var lines = LrcParser.Parse("[00:12.50]첫 줄\n[00:17.00]두 번째 줄");

        Assert.Equal(2, lines.Count);
        Assert.Equal(new TimeSpan(0, 0, 0, 12, 500), lines[0].Time);
        Assert.Equal("첫 줄", lines[0].Text);
        Assert.Equal(new TimeSpan(0, 0, 0, 17, 0), lines[1].Time);
        Assert.Equal("두 번째 줄", lines[1].Text);
    }

    [Fact]
    public void Parse_SortsByTime_RegardlessOfSourceOrder()
    {
        var lines = LrcParser.Parse("[01:00.00]나중 줄\n[00:05.00]먼저 줄");

        Assert.Equal("먼저 줄", lines[0].Text);
        Assert.Equal("나중 줄", lines[1].Text);
    }

    [Fact]
    public void Parse_HandlesMultipleTimestampsOnOneLine()
    {
        var lines = LrcParser.Parse("[00:01.00][00:30.00]반복되는 후렴구");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal("반복되는 후렴구", l.Text));
    }

    [Theory]
    [InlineData("[00:12.5]", 500)]
    [InlineData("[00:12.50]", 500)]
    [InlineData("[00:12.500]", 500)]
    public void Parse_NormalizesFractionDigitsToMilliseconds(string tag, int expectedMs)
    {
        var lines = LrcParser.Parse(tag + "가사");

        Assert.Equal(expectedMs, lines[0].Time.Milliseconds);
    }

    [Fact]
    public void IsSynced_ReflectsWhetherAnyTimestampWasFound()
    {
        Assert.True(LrcParser.IsSynced("[00:01.00]가사"));
        Assert.False(LrcParser.IsSynced("그냥 가사"));
        Assert.False(LrcParser.IsSynced(null));
    }
}
