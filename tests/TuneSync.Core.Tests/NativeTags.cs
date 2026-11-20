namespace TuneSync.Core.Tests;

/// <summary>Sets and reads tag fields the way each format stores them - several artists or genres
/// in one field, a full date - which the app's own TrackTags (first value, year) can't show.</summary>
internal static class NativeTags
{
    /// <summary>The MP4 "©day" atom, where m4a files keep the date.</summary>
    private static readonly TagLib.ByteVector AppleDate = new(0xA9, (byte)'d', (byte)'a', (byte)'y');

    public static void Write(string path, string[] artists, string[] genres, string date)
    {
        using var file = TagLib.File.Create(path);
        TagLib.Tag tag = Path.GetExtension(path) switch
        {
            ".flac" => file.GetTag(TagLib.TagTypes.Xiph, true),
            ".mp3" => file.GetTag(TagLib.TagTypes.Id3v2, true),
            ".m4a" => file.GetTag(TagLib.TagTypes.Apple, true),
            _ => throw new ArgumentException("Unsupported test file", nameof(path)),
        };
        tag.Performers = artists;
        tag.Genres = genres;
        switch (tag)
        {
            case TagLib.Ogg.XiphComment xiph:
                xiph.SetField("DATE", date);
                break;
            case TagLib.Id3v2.Tag id3:
                id3.Version = 4; // several values per frame and a full TDRC date are ID3v2.4
                id3.SetTextFrame("TDRC", date);
                break;
            case TagLib.Mpeg4.AppleTag apple:
                apple.SetText(AppleDate, date);
                break;
        }
        file.Save();
    }

    /// <summary>The fields from the format's own tag (the Vorbis comment, ID3v2 or MP4 atoms).</summary>
    public static (string? Title, string[] Artists, string[] Genres, string? Date) Read(string path)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.GetTag(TagLib.TagTypes.Xiph) ?? file.GetTag(TagLib.TagTypes.Id3v2) ?? file.GetTag(TagLib.TagTypes.Apple);
        var date = tag switch
        {
            TagLib.Ogg.XiphComment xiph => xiph.GetFirstField("DATE"),
            TagLib.Id3v2.Tag id3 => id3.GetTextAsString("TDRC"),
            TagLib.Mpeg4.AppleTag apple => apple.GetText(AppleDate).FirstOrDefault(),
            _ => throw new ArgumentException("Unsupported test file", nameof(path)),
        };
        return (tag.Title, tag.Performers, tag.Genres, date);
    }
}
