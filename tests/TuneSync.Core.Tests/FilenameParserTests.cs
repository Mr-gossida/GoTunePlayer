using TuneSync.Core.Services;

namespace TuneSync.Core.Tests;

public class FilenameParserTests
{
    [Theory]
    [InlineData("[지드래곤] 거짓말", "지드래곤", "거짓말")]
    [InlineData("[IU] Good Day", "IU", "Good Day")]
    public void Parse_RecognizesBracketPattern(string fileName, string expectedArtist, string expectedTitle)
    {
        var result = FilenameParser.Parse(fileName);

        Assert.NotNull(result);
        Assert.Equal(expectedArtist, result!.Value.Artist);
        Assert.Equal(expectedTitle, result.Value.Title);
    }

    [Theory]
    [InlineData("IU - Good Day", "IU", "Good Day")]
    [InlineData("01 - IU - Good Day", "IU", "Good Day")]
    [InlineData("01. IU - Good Day", "IU", "Good Day")]
    public void Parse_RecognizesHyphenPattern(string fileName, string expectedArtist, string expectedTitle)
    {
        var result = FilenameParser.Parse(fileName);

        Assert.NotNull(result);
        Assert.Equal(expectedArtist, result!.Value.Artist);
        Assert.Equal(expectedTitle, result.Value.Title);
    }

    [Fact]
    public void Parse_ReturnsNull_WhenNoRecognizedPattern()
    {
        Assert.Null(FilenameParser.Parse("track001"));
    }
}
