using TuneSync.Core.Models;
using TuneSync.Core.Services.Remote;

namespace TuneSync.Core.Services;

/// <summary>What a file held before <see cref="TagEditService.Write"/> changed it; writing it back
/// (<see cref="TagEditService.Undo"/>) restores the file.</summary>
public sealed class TagEditUndo
{
    public required LibraryFile File { get; init; }
    public TrackTags? Tags { get; init; }

    /// <summary>Every value of the fields that can hold several; <see cref="Tags"/> has only the
    /// first of each, which alone would undo an edit of two artists to one.</summary>
    internal TagValueLists? ValueLists { get; init; }

    public bool CoverChanged { get; init; }
    public byte[]? Cover { get; init; }

    /// <summary>Every picture the file held (the cover and any others), put back whole by an undo.</summary>
    internal TagLib.IPicture[]? Pictures { get; init; }
    public bool LyricsChanged { get; init; }
    public string? Lyrics { get; init; }
}

/// <summary>Writes the tag editor's changes - text tags, cover and lyrics together - in a single
/// open/save per file (one upload for a network file), keeps the <see cref="LibraryFile"/> in step,
/// and remembers what was there before so the save can be undone.</summary>
public static class TagEditService
{
    /// <param name="tags">New text tags, or null to leave them.</param>
    /// <param name="cover">New cover; an empty array removes it; null leaves it.</param>
    /// <param name="lyrics">New lyrics; "" removes them; null leaves them.</param>
    public static TagEditUndo Write(LibraryFile file, TrackTags? tags, byte[]? cover, string? lyrics) =>
        WriteCore(file, tags, null, cover, null, lyrics);

    /// <summary>Puts back what <see cref="Write"/> replaced.</summary>
    public static void Undo(TagEditUndo undo) => WriteCore(
        undo.File,
        undo.Tags,
        undo.ValueLists,
        undo.CoverChanged ? undo.Cover ?? [] : null,
        undo.CoverChanged ? undo.Pictures : null,
        undo.LyricsChanged ? undo.Lyrics ?? "" : null);

    /// <param name="valueLists">For an undo: the multi-valued fields to put back whole.</param>
    /// <param name="pictures">For an undo: every picture to put back (in place of <paramref name="cover"/>).</param>
    private static TagEditUndo WriteCore(LibraryFile file, TrackTags? tags, TagValueLists? valueLists, byte[]? cover,
        TagLib.IPicture[]? pictures, string? lyrics)
    {
        TrackTags? beforeTags = tags is null ? null : file.Tags.Clone();
        TagValueLists? beforeLists = null;
        byte[]? beforeCover = null;
        TagLib.IPicture[]? beforePictures = null;
        string? beforeLyrics = null;

        RemoteFiles.Edit(file.Path, f =>
        {
            if (tags is not null)
            {
                beforeLists = TagValueLists.Of(f.Tag);
                TagService.ApplyTo(f, tags);
                valueLists?.RestoreTo(f.Tag);
            }
            if (cover is not null)
            {
                var held = f.Tag.Pictures ?? [];
                beforeCover = held.FirstOrDefault()?.Data?.Data;
                // copies: the file's own objects go when it's closed
                beforePictures = [.. held.Select(p => new TagLib.Picture(p))];
                // A new cover takes the old one's place, the other pictures stay; removing the cover
                // removes them all (one left would show as the cover).
                f.Tag.Pictures = pictures ?? (cover.Length == 0 ? [] : CoverArtService.WithCover(held, cover));
            }
            if (lyrics is not null)
            {
                beforeLyrics = string.IsNullOrEmpty(f.Tag.Lyrics) ? null : f.Tag.Lyrics;
                f.Tag.Lyrics = lyrics.Length == 0 ? null : lyrics;
            }
        });

        // The file is saved: bring the in-memory row up to date.
        if (tags is not null) file.Tags = WithPath(tags, file.Path);
        if (cover is not null)
        {
            file.HasCover = cover.Length > 0;
            file.CoverBytes = null;
            file.ThumbnailBytes = cover.Length > 0 ? CoverArtService.GetThumbnail(cover)?.JpegBytes : null;
        }
        if (lyrics is not null)
        {
            file.HasLyrics = !string.IsNullOrWhiteSpace(lyrics);
            file.IsLyricsSynced = file.HasLyrics && LrcParser.Parse(lyrics).Count > 0;
        }

        return new TagEditUndo
        {
            File = file,
            Tags = beforeTags,
            ValueLists = beforeLists,
            CoverChanged = cover is not null,
            Cover = beforeCover,
            Pictures = beforePictures,
            LyricsChanged = lyrics is not null,
            Lyrics = beforeLyrics,
        };
    }

    private static TrackTags WithPath(TrackTags tags, string path)
    {
        if (tags.Path == path) return tags;
        var copy = new TrackTags { Path = path };
        foreach (var field in Enum.GetValues<TagField>()) TagFields.Set(copy, field, TagFields.Get(tags, field));
        return copy;
    }
}

/// <summary>All the artists, album artists, composers and genres a file holds (<see cref="TrackTags"/>
/// keeps only the first of each).</summary>
internal sealed record TagValueLists(string[] Performers, string[] AlbumArtists, string[] Composers, string[] Genres)
{
    public static TagValueLists Of(TagLib.Tag tag) =>
        new([.. tag.Performers ?? []], [.. tag.AlbumArtists ?? []], [.. tag.Composers ?? []], [.. tag.Genres ?? []]);

    /// <summary>Sets back the lists the file no longer holds exactly - the ones a save replaced.</summary>
    public void RestoreTo(TagLib.Tag tag)
    {
        if (!Same(tag.Performers, Performers)) tag.Performers = Performers;
        if (!Same(tag.AlbumArtists, AlbumArtists)) tag.AlbumArtists = AlbumArtists;
        if (!Same(tag.Composers, Composers)) tag.Composers = Composers;
        if (!Same(tag.Genres, Genres)) tag.Genres = Genres;
    }

    private static bool Same(string[]? now, string[] before) => (now ?? []).SequenceEqual(before);
}
