using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class LyricsDiffTests
{
    [Fact]
    public void SameLyrics_NothingChanged()
    {
        var r = LyricsDiff.Compare("one\ntwo\nthree", "one\r\ntwo\r\nthree\r\n");
        Assert.Equal(0, r.Removed);
        Assert.Equal(0, r.Added);
        Assert.Equal(3, r.After.Count);
    }

    [Fact]
    public void SyncingPlainLyrics_OnlyTimesAdded_NothingChanged()
    {
        var r = LyricsDiff.Compare("Hello there\nGeneral Kenobi",
            "[ar:Someone]\n[00:12.30]Hello there\n[00:15.00] General  Kenobi");
        Assert.Equal(0, r.Removed);
        Assert.Equal(0, r.Added); // the [ar:] line has no words, so it isn't a change either
    }

    [Fact]
    public void EditedLine_MarkedOnBothSides()
    {
        var r = LyricsDiff.Compare("a\nb\nc", "a\nB!\nc");
        Assert.Equal([false, true, false], r.Before.Select(l => l.Changed));
        Assert.Equal([false, true, false], r.After.Select(l => l.Changed));
    }

    [Fact]
    public void AddedAndRemovedLines()
    {
        var r = LyricsDiff.Compare("a\nb\nc", "a\nc\nd");
        Assert.Equal(1, r.Removed); // b
        Assert.Equal(1, r.Added);   // d
        Assert.True(r.Before[1].Changed);
        Assert.True(r.After[2].Changed);
    }

    [Fact]
    public void BlankLines_NeverCount_AndEdgesTrimmed()
    {
        var r = LyricsDiff.Compare("\n\na\n\nb\n\n", "a\nb");
        Assert.Equal(0, r.Removed);
        Assert.Equal(3, r.Before.Count); // a, blank, b
    }

    [Fact]
    public void FromNothing_AllNew()
    {
        var r = LyricsDiff.Compare(null, "x\ny");
        Assert.Empty(r.Before);
        Assert.Equal(2, r.Added);
    }
}
