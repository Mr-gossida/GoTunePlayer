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
        if (hyphen.Success)
            return (hyphen.Groups["artist"].Value.Trim(), hyphen.Groups["title"].Value.Trim());

        return null;
    }
}
