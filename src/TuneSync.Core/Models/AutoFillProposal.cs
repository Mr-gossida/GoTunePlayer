using System.Globalization;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.Core.Models;

/// <summary>A per-file, per-field diff produced by <see cref="Services.IAutoFillService"/>.
/// A null proposed field means "nothing to change" - only non-null fields get written when the
/// proposal is applied, whether that happens immediately (automatic mode) or after the user
/// reviews it (review mode); the pipeline that builds this is identical either way.
/// With the multi-source search, the results it was chosen from are kept too, with where each
/// came from, so the user can see the source and pick another (<see cref="UseTags"/>,
/// <see cref="UseLyrics"/>).</summary>
public sealed class AutoFillProposal
{
    public required LibraryFile File { get; init; }
    public bool IsSelected { get; set; } = true;
    public string Status { get; set; } = "대기 중";

    public string? ProposedTitle { get; set; }
    public string? ProposedArtist { get; set; }
    public string? ProposedAlbum { get; set; }
    public string? ProposedAlbumArtist { get; set; }
    public uint? ProposedYear { get; set; }
    public string? ProposedGenre { get; set; }
    public uint? ProposedTrack { get; set; }
    public uint? ProposedDisc { get; set; }
    public byte[]? ProposedCoverBytes { get; set; }
    public string? ProposedLyrics { get; set; }

    /// <summary>The tag results found, best first (empty when tags weren't searched for, or with the
    /// MusicBrainz-only search).</summary>
    public IReadOnlyList<TagCandidate> TagCandidates { get; set; } = [];

    /// <summary>The tag result the proposed tags are from; null when none fitted well enough to
    /// pick without the user.</summary>
    public TagCandidate? ChosenTags { get; private set; }

    /// <summary>Tags holding another value are replaced too (else only empty ones are filled).</summary>
    public bool ReplaceTags { get; set; }

    /// <summary>The lyrics found, the chosen ones' rivals included.</summary>
    public IReadOnlyList<LyricsCandidate> LyricsCandidates { get; set; } = [];

    /// <summary>The lyrics proposed; null when none is clearly the song's.</summary>
    public LyricsCandidate? ChosenLyrics { get; private set; }

    /// <summary>Where the proposed cover is from ("MusicBrainz", "iTunes", "Deezer").</summary>
    public string? CoverSource { get; set; }

    /// <summary>Sources that didn't answer for this song.</summary>
    public List<string> FailedSources { get; } = [];

    /// <summary>Results were found, but none fitted clearly enough to put in by itself: the tags,
    /// the lyrics or both wait for the user to pick one.</summary>
    public bool NeedsChoice => TagCandidates.Count > 0 && ChosenTags is null || LyricsCandidates.Count > 0 && ChosenLyrics is null;

    public bool HasAnyProposal =>
        HasTagProposal || ProposedCoverBytes is not null || !string.IsNullOrWhiteSpace(ProposedLyrics);

    public bool HasTagProposal =>
        ProposedTitle is not null || ProposedArtist is not null || ProposedAlbum is not null
        || ProposedAlbumArtist is not null || ProposedYear is not null || ProposedGenre is not null
        || ProposedTrack is not null || ProposedDisc is not null;

    /// <summary>Proposes <paramref name="candidate"/>'s tags (what <see cref="TagChanges"/> says it
    /// changes), in place of the last pick's; null proposes no tags.</summary>
    public void UseTags(TagCandidate? candidate)
    {
        ChosenTags = candidate;
        ProposedTitle = ProposedArtist = ProposedAlbum = ProposedAlbumArtist = ProposedGenre = null;
        ProposedYear = ProposedTrack = ProposedDisc = null;
        if (candidate is null) return;
        static uint Number(string value) => uint.Parse(value, CultureInfo.InvariantCulture);
        foreach (var (field, value) in TagChanges.Of(candidate, f => TagFields.Get(File.Tags, f), ReplaceTags))
        {
            switch (field)
            {
                case TagField.Title: ProposedTitle = value; break;
                case TagField.Artist: ProposedArtist = value; break;
                case TagField.Album: ProposedAlbum = value; break;
                case TagField.AlbumArtist: ProposedAlbumArtist = value; break;
                case TagField.Year: ProposedYear = Number(value); break;
                case TagField.Genre: ProposedGenre = value; break;
                case TagField.Track: ProposedTrack = Number(value); break;
                case TagField.Disc: ProposedDisc = Number(value); break;
            }
        }
    }

    /// <summary>Proposes <paramref name="candidate"/>'s lyrics; null proposes none.</summary>
    public void UseLyrics(LyricsCandidate? candidate)
    {
        ChosenLyrics = candidate;
        ProposedLyrics = candidate?.Lyrics;
    }
}
