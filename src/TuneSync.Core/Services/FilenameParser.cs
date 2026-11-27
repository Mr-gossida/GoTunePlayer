using System.Text.RegularExpressions;

namespace TuneSync.Core.Services;

/// <summary>Best-effort artist/title extraction from a filename, used as a fallback search key
/// when a file's own tags are empty. Recognizes the two conventions TuneSync's own rename feature
/// and most ripped libraries already use: "[Artist] Title" and "(track#) Artist - Title".</summary>
public static partial class FilenameParser
{
    [GeneratedRegex(@"^\[(?<artist>.+?)\]\s*(?<title>.+)$")]
    private static partial Regex BracketPattern();

    [GeneratedRegex(@"^(?:\d+[\s.\-]+)?(?<artist>.+?)\s*-\s*(?<title>.+)$")]
    private static partial Regex HyphenPattern();

    public static (string Artist, string Title)? Parse(string fileNameWithoutExtension)
    {
        var bracket = BracketPattern().Match(fileNameWithoutExtension);
        if (bracket.Success)
            return (bracket.Groups["artist"].Value.Trim(), bracket.Groups["title"].Value.Trim());

        var hyphen = HyphenPattern().Match(fileNameWithoutExtension);
        // "01 - Intro" is a track number and a title, not an artist "01": every untagged
        // "01 - Intro" of different albums was taken for the same song (합치기) and searched for
        // by artist "01" (통합 검색).
        if (hyphen.Success && !hyphen.Groups["artist"].Value.Trim().All(c => char.IsAsciiDigit(c) || c is '.' or ' '))
            return (hyphen.Groups["artist"].Value.Trim(), hyphen.Groups["title"].Value.Trim());

        return null;
    }
}
