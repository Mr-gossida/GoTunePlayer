using System.Text;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class LyricsLimitsTests
{
    [Fact]
    public void OrdinaryLyrics_AreShownWhole()
    {
        const string synced = "[00:12.50]첫 줄\n[00:17.00]두 번째 줄";
        const string plain = "그냥 가사입니다\n\n줄바꿈만 있음";

        Assert.Same(synced, LyricsLimits.Clip(synced));
        Assert.Equal(LrcParser.Parse(synced), LyricsLimits.Parse(synced));
        Assert.Equal(LrcParser.SplitLines(plain), LyricsLimits.SplitLines(plain));
        Assert.Empty(LyricsLimits.Parse(null));
    }

    [Fact]
    public void Clip_CutsAtTheLastLineBreakWithinTheLimit()
    {
        var line = new string('가', 99);
        var text = string.Concat(Enumerable.Repeat(line + "\n", LyricsLimits.MaxChars / 100 + 50));

        var clipped = LyricsLimits.Clip(text);

        Assert.True(clipped.Length <= LyricsLimits.MaxChars);
        Assert.True(clipped.Length > LyricsLimits.MaxChars - 100);
        Assert.StartsWith(clipped, text);
        Assert.Equal('\n', text[clipped.Length]); // ends where a line ends: no half line
    }

    [Fact]
    public void Clip_OneEnormousLine_IsCutAtTheLimit_WithoutSplittingACharacter()
    {
        Assert.Equal(LyricsLimits.MaxChars, LyricsLimits.Clip(new string('a', LyricsLimits.MaxChars + 10)).Length);

        // A character outside the BMP (two UTF-16 halves) straddling the limit stays whole or goes whole.
        var text = new string('a', LyricsLimits.MaxChars - 1) + "😀" + "bbb";
        var clipped = LyricsLimits.Clip(text);

        Assert.Equal(LyricsLimits.MaxChars - 1, clipped.Length);
        Assert.False(char.IsHighSurrogate(clipped[^1]));
    }

    [Fact]
    public void Parse_RepeatedShortTimestamps_StopAtTheLineLimit()
    {
        // Seven characters per line: well within the character limit, far past the line limit.
        var text = new StringBuilder();
        for (var i = 0; i < 20_000; i++) text.Append("[00:00]\n");

        var lines = LyricsLimits.Parse(text.ToString());

        Assert.Equal(LyricsLimits.MaxLines, lines.Count);
    }

    [Fact]
    public void Parse_KeepsTheEarliestLines_WhenCut()
    {
        var text = new StringBuilder();
        for (var i = LyricsLimits.MaxLines + 500; i > 0; i--)
            text.Append($"[{i / 60 % 100:00}:{i % 60:00}]줄 {i}\n");

        var lines = LyricsLimits.Parse(text.ToString());

        Assert.Equal(LyricsLimits.MaxLines, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(LyricsLimits.MaxLines), lines[^1].Time);
    }

    [Fact]
    public void Parse_LooksOnlyAtTheStartOfAHugeTag()
    {
        // A synced line past the character limit isn't read at all.
        var text = new string('x', LyricsLimits.MaxChars + 100) + "\n[00:05.00]너무 뒤에 있는 줄";

        Assert.Empty(LyricsLimits.Parse(text));
    }

    [Fact]
    public void SplitLines_StopsAtTheLineLimit_AndKeepsBlankLines()
    {
        var text = string.Concat(Enumerable.Repeat("가사\n\n", LyricsLimits.MaxLines));

        var lines = LyricsLimits.SplitLines(text);

        Assert.Equal(LyricsLimits.MaxLines, lines.Count);
        Assert.Equal("가사", lines[0]);
        Assert.Equal("", lines[1]);
    }

    [Fact]
    public void SplitLines_HugeRunOfLineBreaks_IsBounded()
    {
        var lines = LyricsLimits.SplitLines(new string('\n', 5_000_000));

        Assert.Equal(LyricsLimits.MaxLines, lines.Count);
    }
}
