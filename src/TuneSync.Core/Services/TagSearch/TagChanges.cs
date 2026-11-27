using TuneSync.Core.Models;

namespace TuneSync.Core.Services.TagSearch;

/// <summary>What a tag result would change in a song's tags - the same rule for one song's cards
/// and for many songs at once.</summary>
public static class TagChanges
{
    /// <summary>The fields <paramref name="candidate"/> has a value for that the song should take,
    /// each as the field stores it: empty fields always; with <paramref name="replace"/>, fields
    /// holding another value too (capitals or spacing alone don't make another value: "ゆびきり
    /// -日本語version-" is the store's "ゆびきり-日本語version-"). A genre only fills an empty
    /// field: the stores' are broad ("K-Pop", "Asian Music"), and one the user set (often finer)
    /// shouldn't be swapped on every search.</summary>
    /// <param name="current">The song's value of a field now.</param>
    public static List<(TagField Field, string Value)> Of(TagCandidate candidate, Func<TagField, string> current, bool replace)
    {
        var list = new List<(TagField, string)>();
        void Check(TagField field, string? value, bool mayReplace = true)
        {
            if (string.IsNullOrWhiteSpace(value) || !TagFields.TryNormalize(field, value, out var normalized, out _) || normalized.Length == 0) return;
            var now = current(field).Trim();
            if (now.Length == 0 || replace && mayReplace && !string.Equals(Squeeze(now), Squeeze(normalized), StringComparison.OrdinalIgnoreCase))
                list.Add((field, normalized));
        }
        static string Squeeze(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));
        Check(TagField.Title, candidate.Title);
        Check(TagField.Artist, candidate.Artist);
        Check(TagField.Album, candidate.Album);
        Check(TagField.AlbumArtist, candidate.AlbumArtist);
        Check(TagField.Year, candidate.Year?.ToString());
        Check(TagField.Genre, candidate.Genre, mayReplace: false);
        Check(TagField.Track, candidate.Track?.ToString());
        Check(TagField.Disc, candidate.Disc?.ToString());
        return list;
    }

    /// <summary>"앨범 · 연도 · 트랙".</summary>
    public static string Names(IEnumerable<(TagField Field, string Value)> changes) =>
        string.Join(" · ", changes.Select(c => TagFields.Label(c.Field)));
}
