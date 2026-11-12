using System.Globalization;
using System.Text.RegularExpressions;

namespace TuneSync.Core.Models;

/// <summary>The editable text tags, in the order the tag editor shows them.</summary>
public enum TagField
{
    Title,
    Artist,
    Album,
    AlbumArtist,
    Year,
    Genre,
    Track,
    Disc,
    Composer,
}

/// <summary>Reads, validates and writes one <see cref="TagField"/> of a <see cref="TrackTags"/> as
/// text, so the editor can treat every field (text or number) the same way.</summary>
public static partial class TagFields
{
    public static string Label(TagField field) => field switch
    {
        TagField.Title => "제목",
        TagField.Artist => "아티스트",
        TagField.Album => "앨범",
        TagField.AlbumArtist => "앨범 아티스트",
        TagField.Year => "연도",
        TagField.Genre => "장르",
        TagField.Track => "트랙",
        TagField.Disc => "디스크",
        TagField.Composer => "작곡가",
        _ => field.ToString(),
    };

    public static bool IsNumeric(TagField field) => field is TagField.Year or TagField.Track or TagField.Disc;

    /// <summary>The field as text; an unset number (0) is empty.</summary>
    public static string Get(TrackTags tags, TagField field) => field switch
    {
        TagField.Title => tags.Title,
        TagField.Artist => tags.Artist,
        TagField.Album => tags.Album,
        TagField.AlbumArtist => tags.AlbumArtist,
        TagField.Genre => tags.Genre,
        TagField.Composer => tags.Composer,
        TagField.Year => Number(tags.Year),
        TagField.Track => Number(tags.Track),
        TagField.Disc => Number(tags.Disc),
        _ => "",
    };

    private static string Number(uint value) => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "";

    /// <summary>Checks typed text and returns it the way it will be stored: trimmed, and for
    /// numbers the plain number ("03" → "3"; a track typed as "3/12" keeps the 3). Empty text
    /// clears the field. On failure <paramref name="error"/> says what's wrong, in Korean.</summary>
    public static bool TryNormalize(TagField field, string? text, out string normalized, out string? error)
    {
        var trimmed = (text ?? "").Trim();
        error = null;
        normalized = trimmed;
        if (!IsNumeric(field) || trimmed.Length == 0) return true;

        var match = (field is TagField.Track or TagField.Disc ? PartOfTotal() : Digits()).Match(trimmed);
        if (!match.Success || !uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            error = field switch
            {
                TagField.Year => "연도는 숫자 4자리로 입력하세요 (예: 2017)",
                _ => "숫자로 입력하세요 (예: 3 또는 3/12)",
            };
            return false;
        }
        if (field == TagField.Year && number is > 0 and (< 1000 or > 9999))
        {
            error = "연도는 숫자 4자리로 입력하세요 (예: 2017)";
            return false;
        }
        if (field != TagField.Year && number > 999)
        {
            error = "999 이하의 숫자로 입력하세요";
            return false;
        }
        normalized = number > 0 ? number.ToString(CultureInfo.InvariantCulture) : "";
        return true;
    }

    /// <summary>Sets the field from already-normalized text (see <see cref="TryNormalize"/>).</summary>
    public static void Set(TrackTags tags, TagField field, string value)
    {
        uint Parse() => uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        switch (field)
        {
            case TagField.Title: tags.Title = value; break;
            case TagField.Artist: tags.Artist = value; break;
            case TagField.Album: tags.Album = value; break;
            case TagField.AlbumArtist: tags.AlbumArtist = value; break;
            case TagField.Genre: tags.Genre = value; break;
            case TagField.Composer: tags.Composer = value; break;
            case TagField.Year: tags.Year = Parse(); break;
            case TagField.Track: tags.Track = Parse(); break;
            case TagField.Disc: tags.Disc = Parse(); break;
        }
    }

    /// <summary>A copy of <paramref name="original"/> with <paramref name="edits"/> applied.</summary>
    public static TrackTags Apply(TrackTags original, IReadOnlyDictionary<TagField, string> edits)
    {
        var result = original.Clone();
        foreach (var (field, value) in edits) Set(result, field, value);
        return result;
    }

    [GeneratedRegex(@"^(\d{1,4})$")]
    private static partial Regex Digits();

    [GeneratedRegex(@"^(\d{1,4})\s*(?:/\s*\d{0,4})?$")]
    private static partial Regex PartOfTotal();
}
