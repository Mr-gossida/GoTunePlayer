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
