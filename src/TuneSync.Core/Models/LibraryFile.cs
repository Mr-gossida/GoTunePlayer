namespace TuneSync.Core.Models;

/// <summary>One scanned audio file: its tags plus whether it has cover art / lyrics. The gap
/// properties are what the library filter and the auto-fill pipeline both key off.</summary>
public sealed class LibraryFile
{
    public required string Path { get; init; }
    public required TrackTags Tags { get; set; }
    public bool HasCover { get; set; }
    public bool HasLyrics { get; set; }
    public byte[]? CoverBytes { get; set; }

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
