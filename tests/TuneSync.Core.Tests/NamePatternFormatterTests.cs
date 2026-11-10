using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class NamePatternFormatterTests
{
    private static readonly RenameRuleSet DefaultRules = new();

    [Fact]
    public void Format_SubstitutesBasicPlaceholders()
    {
        var tags = new TrackTags { Path = "x.mp3", Artist = "IU", Title = "Good Day", Track = 3 };

        var result = NamePatternFormatter.Format("{Artist} - {Title}", tags, DefaultRules);

        Assert.Equal("IU - Good Day", result);
    }

    [Fact]
    public void Format_PadsTrackNumberToTwoDigits()
    {
        var tags = new TrackTags { Path = "x.mp3", Title = "Title", Track = 3 };

        Assert.Equal("03 Title", NamePatternFormatter.Format("{Track} {Title}", tags, DefaultRules));
    }

    [Fact]
    public void Format_PrefixesTrackWithDisc_WhenMultiDisc()
    {
        var tags = new TrackTags { Path = "x.mp3", Title = "Title", Track = 3, Disc = 2 };

        Assert.Equal("2-03 Title", NamePatternFormatter.Format("{Track} {Title}", tags, DefaultRules));
    }

    [Fact]
    public void Sanitize_ReplacesColonWithConfiguredReplacement()
    {
        var result = NamePatternFormatter.Sanitize("Part 1: The Beginning", DefaultRules);

        Assert.Equal("Part 1： The Beginning", result);
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a\"b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    public void Sanitize_ReplacesEveryForbiddenCharacter(string input)
    {
        var result = NamePatternFormatter.Sanitize(input, DefaultRules);

        Assert.All(RenameRuleSet.ForbiddenChars, ch => Assert.DoesNotContain(ch, result));
    }

    [Fact]
    public void Format_LeavesEmptyYearBlank_NotZero()
    {
        var tags = new TrackTags { Path = "x.mp3", Title = "Title" };

        Assert.Equal("Title", NamePatternFormatter.Format("{Title}{Year}", tags, DefaultRules));
    }
}
