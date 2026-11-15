using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.Core.Tests;

public class LrcWriterTests
{
    private const string Synced = "[ar:IU]\r\n[ti:좋은 날]\r\n[offset:+120]\r\n[00:12.50]첫 줄\r\n[00:20.00]\r\n[00:21.30]둘째 줄";

    [Fact]
    public void HeaderLines_KeepsTagsOnly()
    {
        Assert.Equal(["[ar:IU]", "[ti:좋은 날]", "[offset:+120]"], LrcWriter.HeaderLines(Synced));
        Assert.Empty(LrcWriter.HeaderLines("그냥 가사\n둘째 줄"));
    }

    [Fact]
    public void EditedLyrics_RoundTrip_KeepsHeaderAndGaps()
    {
        var parsed = LrcParser.Parse(Synced);
        var written = LrcWriter.Write(LrcWriter.HeaderLines(Synced), parsed.Select(l => (l.Time, l.Text)));
        Assert.Equal("[ar:IU]\n[ti:좋은 날]\n[offset:+120]\n[00:12.50]첫 줄\n[00:20.00]\n[00:21.30]둘째 줄", written);
    }

    [Fact]
    public void Write_WithoutHeader_IsPlainWrite()
    {
        var lines = new[] { (TimeSpan.FromSeconds(1), "a") };
        Assert.Equal(LrcWriter.Write(lines), LrcWriter.Write([], lines));
    }
}
