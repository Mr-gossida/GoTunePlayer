namespace TuneSync.Core.Models;

/// <summary>A per-file, per-field diff produced by <see cref="Services.IAutoFillService"/>.
/// A null proposed field means "nothing to change" - only non-null fields get written when the
/// proposal is applied, whether that happens immediately (automatic mode) or after the user
/// reviews it (review mode); the pipeline that builds this is identical either way.</summary>
public sealed class AutoFillProposal
{
    public required LibraryFile File { get; init; }
    public bool IsSelected { get; set; } = true;
    public string Status { get; set; } = "대기 중";

    public string? ProposedTitle { get; set; }
    public string? ProposedArtist { get; set; }
    public string? ProposedAlbum { get; set; }
    public string? ProposedAlbumArtist { get; set; }
    public uint? ProposedTrack { get; set; }
    public uint? ProposedDisc { get; set; }
    public byte[]? ProposedCoverBytes { get; set; }
    public string? ProposedLyrics { get; set; }

    public bool HasAnyProposal =>
        ProposedTitle is not null || ProposedArtist is not null || ProposedAlbum is not null
        || ProposedAlbumArtist is not null || ProposedTrack is not null || ProposedDisc is not null
        || ProposedCoverBytes is not null || !string.IsNullOrWhiteSpace(ProposedLyrics);
}
