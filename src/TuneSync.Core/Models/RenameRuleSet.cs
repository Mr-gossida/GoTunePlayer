using System.Text.Json.Serialization;

namespace TuneSync.Core.Models;

/// <summary>A user-editable naming convention. Persisted as JSON so "이름 변경 규칙" can be a
/// settings screen instead of a recompile. Shared by the standalone rename feature and the
/// merge feature's "이름 변경 후 둘 다 보존" conflict action.</summary>
public sealed class RenameRuleSet
{
    public string Name { get; set; } = "기본 규칙";

    /// <summary>Pattern for a standalone single (no album folder). Placeholders: {Artist}
    /// {Title} {Album} {AlbumArtist} {Track} {Disc} {Year}.</summary>
    public string SingleTrackPattern { get; set; } = "{Artist} - {Title}";

    /// <summary>Pattern for a track that belongs to an album. Same placeholders as above.</summary>
    public string AlbumTrackPattern { get; set; } = "{Track} {Title}";

    /// <summary>Replacement used for the colon specifically, since Windows forbids it outright.
    /// Default is the full-width colon so a real colon in a title survives instead of becoming "-".</summary>
    public string ColonReplacement { get; set; } = "：";

    /// <summary>Replacement used for every other character Windows forbids in file names.</summary>
    public string OtherForbiddenReplacement { get; set; } = "-";

    [JsonIgnore]
    public static readonly char[] ForbiddenChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
}
