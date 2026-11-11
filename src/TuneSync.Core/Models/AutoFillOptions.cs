namespace TuneSync.Core.Models;

/// <summary>Which categories an auto-fill search covers.</summary>
/// <param name="Tags">Fill empty tag fields (title, artist, album, album artist, track, disc).
/// Existing tag values are never overwritten.</param>
/// <param name="Cover">Look for album art.</param>
/// <param name="Lyrics">Look for lyrics.</param>
/// <param name="ReplaceExisting">For cover and lyrics: also search when the file already has one
/// (the user explicitly asked for it), so the result can replace it after review. When false,
/// only missing covers/lyrics are searched.</param>
public sealed record AutoFillOptions(bool Tags = true, bool Cover = true, bool Lyrics = true, bool ReplaceExisting = false);
