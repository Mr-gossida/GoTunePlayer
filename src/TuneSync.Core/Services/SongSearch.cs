namespace TuneSync.Core.Services;

/// <summary>The texts of a song that the play list's search looks through.</summary>
public readonly record struct SongSearchText(string Title, string Artist, string Album, string FileName);

/// <summary>The play list's search: which songs a query finds, best first. It never filters the
/// list - the player steps through the matches in this order.</summary>
public static class SongSearch
{
    /// <summary><see cref="Rank"/>'s answer for a song the query doesn't find.</summary>
    public const int NoMatch = -1;

    /// <summary>Every song the search finds, best first: titles that are the query or start with
    /// it, then titles containing it, then artists, then albums and file names, then songs where
    /// all the words appear somewhere (e.g. "akmu love"). Within a rank, list order.</summary>
    public static List<T> Find<T>(IEnumerable<T> songs, string query, Func<T, SongSearchText> textOf)
    {
        query = query.Trim();
        if (query.Length == 0) return [];
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return [.. songs
            .Select((song, index) => (Song: song, Index: index, Rank: Rank(textOf(song), query, words)))
            .Where(m => m.Rank != NoMatch)
            .OrderBy(m => m.Rank).ThenBy(m => m.Index)
            .Select(m => m.Song)];
    }

    /// <summary>How well a song answers the query (lower is better), or <see cref="NoMatch"/>.
    /// <paramref name="words"/> are the query's words, used only when the query as a whole is
    /// found nowhere.</summary>
    public static int Rank(SongSearchText song, string query, IReadOnlyList<string> words)
    {
        const StringComparison ignore = StringComparison.CurrentCultureIgnoreCase;
        if (song.Title.Equals(query, ignore)) return 0;
        if (song.Title.StartsWith(query, ignore)) return 1;
        if (song.Title.Contains(query, ignore)) return 2;
        if (song.Artist.Contains(query, ignore)) return 3;
        if (song.Album.Contains(query, ignore)) return 4;
        if (song.FileName.Contains(query, ignore)) return 5;
        if (words.Count > 1)
        {
            var all = $"{song.Title} {song.Artist} {song.Album} {song.FileName}";
            if (words.All(w => all.Contains(w, ignore))) return 6;
        }
        return NoMatch;
    }
}
