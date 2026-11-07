using System.Text.Json.Serialization;

namespace TuneSync.Core.Models;

/// <summary>A user-editable naming convention. Persisted as JSON so "규칙 관리" (rule management)
/// can be a settings screen instead of a recompile.</summary>
public sealed class RenameRuleSet
{
    public string Name { get; set; } = "Default";

    /// <summary>Pattern for a standalone single (no album folder). Placeholders: {Artist} {Title}.</summary>
    public string SingleTrackPattern { get; set; } = "[{Artist}] {Title}";

    /// <summary>Pattern for a track that belongs to an album folder.
    /// Placeholders: {Artist} {Title} {Track} {Disc}.</summary>
    public string AlbumTrackPattern { get; set; } = "[{Artist}] {Track} {Title}";

    /// <summary>Replacement used for the colon specifically, since Windows forbids it outright.
    /// Default is the full-width colon so a real colon in a title survives instead of becoming "_".</summary>
    public string ColonReplacement { get; set; } = "："; // '：'

    /// <summary>Replacement used for every other character Windows forbids in file names.</summary>
    public string OtherForbiddenReplacement { get; set; } = "-";

    [JsonIgnore]
    public static readonly char[] ForbiddenChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];
}
