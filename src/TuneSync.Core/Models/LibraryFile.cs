namespace TuneSync.Core.Models;

/// <summary>One scanned audio file: its tags plus whether it has cover art / lyrics. The gap
/// properties are what the library filter and the auto-fill pipeline both key off.</summary>
public sealed class LibraryFile
{
    public required string Path { get; init; }
    public required TrackTags Tags { get; set; }
    public bool HasCover { get; set; }
    public bool HasLyrics { get; set; }

    /// <summary>True when the lyrics text carries LRC timestamps (so playback can auto-scroll/
    /// highlight), false for plain unsynced lyrics or when there are no lyrics at all.</summary>
    public bool IsLyricsSynced { get; set; }

    public byte[]? CoverBytes { get; set; }
    public TimeSpan Duration { get; set; }

    /// <summary>A small pre-resized JPEG, computed once during the background scan so the UI
    /// thread only ever has to decode a already-tiny image when displaying it (grid view, cover
    /// preview) instead of the full embedded cover.</summary>
    public byte[]? ThumbnailBytes { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public bool IsMissingCover => !HasCover;
    public bool IsMissingLyrics => !HasLyrics;

    public IReadOnlyList<string> MissingTagFields
    {
        get
        {
            var missing = new List<string>(3);
            if (string.IsNullOrWhiteSpace(Tags.Title)) missing.Add("Title");
            if (string.IsNullOrWhiteSpace(Tags.Artist)) missing.Add("Artist");
            if (string.IsNullOrWhiteSpace(Tags.Album)) missing.Add("Album");
            return missing;
        }
    }

    public bool IsMissingTags => MissingTagFields.Count > 0;

    public bool HasAnyGap => IsMissingCover || IsMissingLyrics || IsMissingTags;
}
