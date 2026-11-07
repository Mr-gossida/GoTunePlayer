using System.Text;
using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

public static class RenameService
{
    /// <summary>Replaces each Windows-forbidden character individually so a rule change (e.g.
    /// colon -> full-width colon instead of underscore) never has to touch calling code.</summary>
    public static string Sanitize(string value, RenameRuleSet rules)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (ch == ':') sb.Append(rules.ColonReplacement);
            else if (Array.IndexOf(RenameRuleSet.ForbiddenChars, ch) >= 0) sb.Append(rules.OtherForbiddenReplacement);
            else sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    public static string BuildFileName(TrackTags tags, RenameRuleSet rules, bool isAlbumTrack)
    {
        var artist = Sanitize(tags.Artist, rules);
        var title = Sanitize(tags.Title, rules);
        var pattern = isAlbumTrack ? rules.AlbumTrackPattern : rules.SingleTrackPattern;

        var trackNo = tags.Disc > 1
            ? $"{tags.Disc}-{tags.Track:D2}"
            : tags.Track.ToString("D2");

        return pattern
            .Replace("{Artist}", artist)
            .Replace("{Title}", title)
            .Replace("{Track}", trackNo)
            .Replace("{Disc}", tags.Disc.ToString());
    }

    public static RenameRuleSet Load(string jsonPath)
    {
        if (!System.IO.File.Exists(jsonPath)) return new RenameRuleSet();
        var json = System.IO.File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<RenameRuleSet>(json) ?? new RenameRuleSet();
    }

    public static void Save(RenameRuleSet rules, string jsonPath)
    {
        var json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        System.IO.File.WriteAllText(jsonPath, json);
    }
}
