namespace TuneSync.Core.Models;

/// <summary>What a planned rename is, for the preview's status column and filters.</summary>
public enum RenameKind
{
    /// <summary>Gets a new name.</summary>
    Change,
    /// <summary>Already named this way.</summary>
    Same,
    /// <summary>A tag the pattern uses is empty - the new name would be incomplete.</summary>
    MissingTags,
    /// <summary>Gets a new name with " (2)"... added, because the name is already taken.</summary>
    Collision,
}

/// <summary>Which pattern a song is named with.</summary>
public enum RenameMode
{
    /// <summary>The album pattern for songs with a track number and an album, else the single one.</summary>
    Auto,
    Single,
    Album,
}

public sealed class RenamePreviewItem
{
    public required string OldPath { get; init; }
    public required string NewPath { get; init; }
    public string OldFileName => Path.GetFileName(OldPath);
    public string NewFileName => Path.GetFileName(NewPath);
    public bool WillChange => !string.Equals(OldPath, NewPath, StringComparison.Ordinal);
    public string Status { get; set; } = "대기 중";

    public RenameKind Kind { get; init; } = RenameKind.Change;

    /// <summary>Why it needs a look (missing tags, a taken name), in Korean.</summary>
    public string Note { get; init; } = "";

    /// <summary>Ticked to be renamed. Rows with missing tags start unticked.</summary>
    public bool IsIncluded { get; set; } = true;
}
