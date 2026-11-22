using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Looks up lyrics on LRCLIB only (open-licensed, no paywalled/ToS-restricted
/// providers). Every candidate - from the exact lookup and from search - is scored on title,
/// artist, length, album, language and whether it has synced lyrics; versions of a different
/// length (live, remix, a foreign-language release) are turned down rather than guessed.
/// Language: a song's lyrics are wanted in the song's own language, not a Japanese release's or
/// romanized ones uploaded under the same title and length. Hangul or kana in the tags says which
/// that is; otherwise the candidates of the recording vote, the app's language counting double.</summary>
public sealed class LrcLibLyricsService : ILyricsService, IDisposable
{
    /// <summary>Longer or shorter than this, a candidate is another recording of the song.</summary>
    private const double MaxLengthDifference = 8;

    private static readonly string[] VersionWords =
        ["live", "remix", "inst", "instrumental", "acoustic", "ver", "version", "japanese", "english", "chinese", "mr", "karaoke", "sped", "slowed"];

    private readonly HttpClient _http;
    private readonly Script _preferred;

    public LrcLibLyricsService() : this(new HttpClientHandler(), CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
    {
    }

    /// <summary>For tests: a handler standing in for the LRCLIB server.</summary>
    /// <param name="preferredLanguage">The app's language ("ko", "ja", "zh", anything else = Latin
    /// script), which wins ties between languages when the tags don't say.</param>
    public LrcLibLyricsService(HttpMessageHandler handler, string preferredLanguage = "ko")
    {
        _http = new HttpClient(handler) { BaseAddress = new Uri("https://lrclib.net/api/") };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GoTunePlayer/1.0");
        _preferred = preferredLanguage switch { "ko" => Script.Korean, "ja" => Script.Japanese, "zh" => Script.Chinese, _ => Script.Latin };
    }

    public async Task<LyricsResult?> FindAsync(LyricsQuery query, CancellationToken ct = default)
    {
        var candidates = new List<Candidate>();
        var tagged = TagLanguage(query);
        bool AnyFits() => candidates.Any(c => Match(query, c.Track, c.StrictArtist) is not null);
        void Add(List<LrcLibTrack> found, bool strictArtist) => candidates.AddRange(found.Select(t => new Candidate(t, strictArtist)));

        // 1. The exact lookup, with album and length when known (LRCLIB then picks the recording
        //    within ~2 s of that length). A synced, well-fitting hit in the right language (the
        //    tags', or the app's when they don't say) ends the search right here.
        if (await TryGetAsync(query, query.Title, ct) is { } exact)
        {
            candidates.Add(new(exact, false));
            if (Score(query, exact) is { } s && s >= GoodEnough && HasSynced(exact) && LanguageOf(exact) == (tagged ?? _preferred))
                return ToResult(exact);
        }

        // 2. Without a "(feat. …)" / "(with …)" clause - a common reason the exact lookup misses.
        var bare = StripFeature(query.Title);
        if (bare != query.Title && await TryGetAsync(query, bare, ct) is { } bareHit) candidates.Add(new(bareHit, false));

        // 3. Search: by field first, then free text; every result is a candidate.
        Add(await SearchAsync($"search?track_name={Esc(bare)}&artist_name={Esc(query.Artist)}", ct), false);
        if (!AnyFits()) Add(await SearchAsync($"search?q={Esc(query.Artist + " " + bare)}", ct), false);

        // 4. By title alone: the artist is often written otherwise there ("아이유(IU)" / "아이유",
        //    "TWS (투어스)" / "TWS"), and then LRCLIB's artist filter misses. Every song of that
        //    title comes back, so here the artist must match - the length alone isn't enough.
        if (!AnyFits()) Add(await SearchAsync($"search?track_name={Esc(bare)}", ct), true);

        // 5. By artist and album: the song may be listed under its other-language title ("막내"
        //    is "The loved one" there, "잠들고 싶어 (zZ)" is "zZ"), which no title search finds.
        if (!AnyFits() && !string.IsNullOrWhiteSpace(query.Album))
            Add(await SearchAsync($"search?q={Esc(query.Artist + " " + query.Album)}", ct), true);

        // 6. The tags are in Hangul (or kana) but every fitting candidate is in another script: the
        //    song may be listed under its title in that script, which ours doesn't search for.
        //    "Some" by "소유, 정기고" only finds a romanized upload titled "썸 (Some)"; the Korean
        //    lyrics are all under "썸". The other-language candidate's title tells us that name.
        HashSet<string>? aliases = null;
        if (tagged is { } script and not Script.Latin
            && !candidates.Any(c => Match(query, c.Track, c.StrictArtist) is not null && LanguageOf(c.Track) == script))
        {
            var ours = TitleVariants(query.Title);
            aliases = [.. candidates
                .Where(c => Match(query, c.Track, c.StrictArtist) is { ByAlbum: false })
                .Select(c => MainTitle(c.Track.TrackName))
                .Where(v => v.Length > 0 && !ours.Contains(v) && InScript(v, script))];
            foreach (var alias in aliases.Take(2))
            {
                Add(await SearchAsync($"search?q={Esc(query.Artist + " " + alias)}", ct), false);
                Add(await SearchAsync($"search?track_name={Esc(alias)}", ct), true);
            }
        }

        var fitting = candidates
            .Select(c => (c.Track, Fit: Match(query, c.Track, c.StrictArtist, aliases), Language: LanguageOf(c.Track)))
            .Where(c => c.Fit is not null)
            .Select(c => (c.Track, Fit: c.Fit!.Value, c.Language))
            .ToList();

        // A song recognized by artist, album and length alone is only taken when nothing matches
        // by title, and when just one song of the album fits that length (two tracks of nearly
        // the same length leave it open which one this is).
        if (fitting.Any(c => !c.Fit.ByAlbum)) fitting.RemoveAll(c => c.Fit.ByAlbum);
        else if (fitting.Select(c => Normalize(StripBrackets(c.Track.TrackName))).Distinct().Count() > 1) return null;
        if (fitting.Count == 0) return null;

        // 7. The language: the tags', or the one most of the fitting candidates are in (the app's
        //    language counting double) - so one Japanese or romanized upload of a Korean song,
        //    under the same title and length, loses to the Korean ones.
        var wanted = tagged ?? fitting
            .GroupBy(c => c.Language)
            .OrderByDescending(g => g.Count() * (g.Key == _preferred ? 2 : 1))
            .ThenByDescending(g => g.Key == _preferred)
            .First().Key;
        var best = fitting
            .Select(c => (c.Track, Score: c.Fit.Score + LanguageScore(wanted, c.Language, tagged is not null)))
            .Where(c => c.Score is not null)
            .OrderByDescending(c => c.Score)
            .FirstOrDefault();
        return best.Track is null ? null : ToResult(best.Track);
    }

    /// <summary>A search result, and whether its artist must match ours (the result of a search
    /// that didn't filter by artist).</summary>
    private sealed record Candidate(LrcLibTrack Track, bool StrictArtist);

    /// <summary>How well a candidate fits; <see cref="ByAlbum"/> when its title is another one
    /// altogether and only artist, album and length say it's this song.</summary>
    internal readonly record struct Fit(double Score, bool ByAlbum);

    /// <summary>The script a song's lyrics are written in, which is as much of its language as
    /// matters here (romanized Korean counts as Latin).</summary>
    internal enum Script { Latin, Korean, Japanese, Chinese }

    /// <summary>The right language counts for a lot; another one counts against (it is still taken
    /// when nothing else fits) - except that lyrics in another Asian script than the one the tags
    /// are written in are not this song's at all (Japanese lyrics for a song tagged in Hangul).</summary>
    private static double? LanguageScore(Script wanted, Script language, bool fromTags) =>
        language == wanted ? 3
        : fromTags && wanted != Script.Latin && language != Script.Latin ? null
        : -4;

    /// <summary>What the tags themselves say: kana means Japanese, Hangul Korean ("Japanese Ver."
    /// in the title, Japanese); Latin-only tags ("REALLY REALLY" by "WINNER") say nothing.</summary>
    internal static Script? TagLanguage(LyricsQuery query)
    {
        if (Words(query.Title).Contains("japanese")) return Script.Japanese;
        var tags = $"{query.Title} {query.Artist} {query.Album}";
        if (tags.Any(IsKana)) return Script.Japanese;
        if (tags.Any(IsHangul)) return Script.Korean;
        return null;
    }

    /// <summary>The lyrics' script: kana makes it Japanese (Japanese lyrics are full of kanji
    /// too, but never without kana), Hangul Korean, Chinese characters alone Chinese, and the
    /// rest - English, or Korean written out in Latin letters - Latin.</summary>
    internal static Script LanguageOf(LrcLibTrack track)
    {
        var text = track.SyncedLyrics ?? track.PlainLyrics ?? "";
        int hangul = 0, kana = 0, han = 0;
        foreach (var c in text)
        {
            if (IsHangul(c)) hangul++;
            else if (IsKana(c)) kana++;
            else if (c is >= '一' and <= '鿿') han++;
        }
        if (kana >= 2 && kana >= hangul) return Script.Japanese;
        if (hangul >= 2) return Script.Korean;
        if (han >= 2) return Script.Chinese;
        return Script.Latin;
    }

    private static bool IsHangul(char c) => c is >= '가' and <= '힣' or >= 'ㄱ' and <= 'ㆎ';

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ';

    /// <summary>A title without its "(…)" parts, normalized: "썸 (Some)" and "썸 (Feat. 릴보이)"
    /// are both "썸". Unlike <see cref="TitleVariants"/> it keeps one-syllable titles.</summary>
    private static string MainTitle(string title) => Normalize(StripBrackets(title));

    /// <summary>Whether a (normalized) title is written in the script of the tags: Hangul for
    /// Korean; kana or kanji for Japanese.</summary>
    private static bool InScript(string title, Script script) => script switch
    {
        Script.Korean => title.Any(IsHangul),
        Script.Japanese => title.Any(c => IsKana(c) || c is >= '一' and <= '鿿'),
        _ => false,
    };

    /// <summary>A candidate scoring at least this (title and length both right) can be taken
    /// without searching further.</summary>
    private const double GoodEnough = 8;

    /// <summary>How well a candidate fits, or null when it shouldn't be used at all.</summary>
    internal static double? Score(LyricsQuery query, LrcLibTrack candidate) => Match(query, candidate, false)?.Score;

    /// <summary>How well a candidate fits, or null when it shouldn't be used at all.</summary>
    /// <param name="strictArtist">The artist must match: a matching length doesn't make up for
    /// another artist name (for results of a search by title alone, which has every song of that
    /// title in it).</param>
    /// <param name="aliases">Other names of our song, learned from a candidate whose title holds
    /// both ("썸 (Some)" makes "썸" a name of "Some").</param>
    internal static Fit? Match(LyricsQuery query, LrcLibTrack candidate, bool strictArtist, IReadOnlySet<string>? aliases = null)
    {
        if (!HasSynced(candidate) && string.IsNullOrWhiteSpace(candidate.PlainLyrics)) return null; // nothing to use
        double score = 0;

        // Length: the strongest sign it's the same recording.
        double? diff = null;
        if (query.Seconds is { } seconds && candidate.Duration > 0)
        {
            diff = Math.Abs(candidate.Duration - seconds);
            if (diff > MaxLengthDifference) return null;
            score += diff <= 2 ? 4 : diff <= 3 ? 3 : 1;
        }
        var lengthMatches = diff <= 3;

        // Artist: exact, or one name inside the other ("IU" / "IU (아이유)"), or a name both tags
        // hold ("권오선 & MASON HOME" / "MASON HOME"), or its romanization ("경서" / "KyoungSeo").
        // A differently written artist ("(G)I-DLE" / "(여자)아이들") is still fine when title and
        // length match - unless it must match.
        var artist = ArtistMatch(query.Artist, candidate.ArtistName);
        if (artist == 0 && (strictArtist || !lengthMatches)) return null;
        score += artist;

        // Title: the same - exactly, or once "(feat. …)"-style extras are set aside; or, when
        // artist and length match too, one of the names the song goes by ("Ego (미워 (Ego))" is
        // also "미워", "우주를 건너 Across the universe" also "우주를 건너").
        var byAlbum = false;
        if (Normalize(candidate.TrackName) == Normalize(query.Title)) score += 4;
        else if (Normalize(StripBrackets(candidate.TrackName)) == Normalize(StripBrackets(query.Title))) score += 3;
        else if (artist > 0 && lengthMatches && TitleVariants(candidate.TrackName).Overlaps(TitleVariants(query.Title))) score += 2;
        else if (aliases is { Count: > 0 } && artist > 0 && lengthMatches && aliases.Contains(MainTitle(candidate.TrackName))) score += 2;
        // Another title altogether: the song under its name in the other language ("막내" is
        // listed as "The loved one") - taken only when one title is Hangul and the other Latin
        // and artist, album and length (within 2 s) all match. Two different titles in the same
        // script are two songs, however alike their lengths ("아이처럼" / "그건 말야"). The lyrics
        // must also sing our title: "The loved one" sings "막내", but "Antifreeze" - another song
        // of the album "선물", just as long as "왜? 날", which LRCLIB doesn't have - never says "왜 날".
        else if (artist > 0 && diff <= 2 && SameAlbum(query.Album, candidate.AlbumName)
                 && IsTranslation(query.Title, candidate.TrackName)
                 && !VersionWords.Any(w => Words(candidate.TrackName).Contains(w))
                 && SingsTitle(candidate, query.Title)) byAlbum = true;
        else return null;

        // A live/remix/… version we weren't asked for.
        if (VersionWords.Any(w => Words(candidate.TrackName).Contains(w) && !Words(query.Title).Contains(w))) score -= 3;

        if (SameAlbum(query.Album, candidate.AlbumName)) score += 1;
        if (HasSynced(candidate)) score += 2;
        return new Fit(score, byAlbum);
    }

    /// <summary>One title in Hangul and the other in Latin letters only - as a song's Korean
    /// title and its English one are.</summary>
    private static bool IsTranslation(string ours, string theirs)
    {
        static bool IsLatinOnly(string s) =>
            !s.Any(IsHangul) && !s.Any(IsKana) && s.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
        return ours.Any(IsHangul) && IsLatinOnly(theirs) || theirs.Any(IsHangul) && IsLatinOnly(ours);
    }

    /// <summary>Whether the lyrics hold the title, spacing and punctuation aside ("왜? 날" is
    /// found in "왜 날 ..."). Timestamps are dropped first so their digits don't split a line.</summary>
    private static bool SingsTitle(LrcLibTrack track, string title)
    {
        var wanted = Normalize(StripBrackets(title));
        if (wanted.Length == 0) return false;
        var lyrics = track.PlainLyrics is { Length: > 0 } plain ? plain
            : LrcTimestamp.Replace(track.SyncedLyrics ?? "", " ");
        return Normalize(lyrics).Contains(wanted);
    }

    private static readonly System.Text.RegularExpressions.Regex LrcTimestamp = new(@"\[[^\]]*\]");

    private static bool SameAlbum(string? ours, string? theirs) =>
        Normalize(ours ?? "") is { Length: > 0 } album && Normalize(theirs ?? "") == album;

    /// <summary>3 for the same artist, 2 when one name is inside the other or the tags share one
    /// of their names, 1 when one is the other's romanization ("경서" / "KyoungSeo", "폴킴" /
    /// "Paul Kim"), 0 otherwise.</summary>
    internal static int ArtistMatch(string ours, string theirs)
    {
        var a = Normalize(ours);
        var b = Normalize(theirs);
        if (a.Length == 0 || b.Length == 0) return 0;
        if (a == b) return 3;
        if (a.Contains(b) || b.Contains(a)) return 2;
        var ourNames = ArtistNames(ours);
        var theirNames = ArtistNames(theirs);
        if (ourNames.Overlaps(theirNames)) return 2;
        return Romanizes(ourNames, theirNames) || Romanizes(theirNames, ourNames) ? 1 : 0;
    }

    /// <summary>Whether a Hangul name on one side sounds like a Latin one on the other: the same
    /// consonants in the same order, vowels aside (their spellings vary too much).</summary>
    private static bool Romanizes(HashSet<string> hangulNames, HashSet<string> latinNames)
    {
        var skeletons = hangulNames.Where(n => n.All(IsHangul)).Select(Consonants).Where(s => s.Count >= 2).ToList();
        return skeletons.Count > 0 && latinNames
            .Where(n => n.All(c => c is >= 'a' and <= 'z'))
            .Any(n => skeletons.Any(s => s.SequenceEqual(Consonants(n))));
    }

    /// <summary>The consonant sounds of a normalized name, Hangul or Latin, in broad classes
    /// (ㄱ/ㅋ and g/k/c alike, ㄹ and r/l, a final ㅇ and "ng"): 경서 and kyoungseo both give
    /// k-ng-s. A Hangul final and the next initial are two sounds, like a doubled Latin letter
    /// (민니 and minnie: m-n-n).</summary>
    private static List<string> Consonants(string name)
    {
        var sounds = new List<string>();
        if (name.All(IsHangul))
        {
            // per syllable: the initial (ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ; ㅇ is silent there)
            // and the final, in Unicode order
            const string initials = "kknttlmppss-jjjktph";
            string[] finals = ["", "k", "k", "k", "n", "n", "n", "t", "l", "l", "l", "l", "l", "l", "l", "l", "m", "p", "p", "t", "t", "ng", "t", "t", "k", "t", "p", "t"];
            foreach (var c in name)
            {
                if (c is < '가' or > '힣') continue;
                var index = c - '가';
                if (initials[index / (21 * 28)] is var initial and not '-') sounds.Add(initial.ToString());
                if (finals[index % 28] is { Length: > 0 } final) sounds.Add(final);
            }
            return sounds;
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var next = i + 1 < name.Length ? name[i + 1] : '\0';
            if (c == 'n' && next == 'g' && (i + 2 >= name.Length || "aeiouwy".IndexOf(name[i + 2]) < 0)) { sounds.Add("ng"); i++; continue; }
            if (c is 'c' or 's' or 'p' && next == 'h') { sounds.Add(c switch { 'c' => "j", 's' => "s", _ => "p" }); i++; continue; }
            switch (c)
            {
                case 'g' or 'k' or 'c' or 'q': sounds.Add("k"); break;
                case 'd' or 't': sounds.Add("t"); break;
                case 'b' or 'p' or 'f' or 'v': sounds.Add("p"); break;
                case 'r' or 'l': sounds.Add("l"); break;
                case 'j' or 'z': sounds.Add("j"); break;
                case 's' or 'x': sounds.Add("s"); break;
                case 'm' or 'n' or 'h': sounds.Add(c.ToString()); break;
            }
        }
        return sounds;
    }

    private static readonly string[] ArtistSeparators =
        ["(", ")", "[", "]", ",", "&", "/", ";", "·", " x ", " X ", " feat. ", " Feat. ", " ft. ", " with "];

    /// <summary>The names an artist tag holds: "권오선 & MASON HOME" → 권오선, MASON HOME;
    /// "잔나비 JANNABI" → 잔나비, JANNABI. One-letter names say too little to count.</summary>
    private static HashSet<string> ArtistNames(string artist)
    {
        var names = new HashSet<string>();
        foreach (var part in artist.Split(ArtistSeparators, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in ScriptParts(part).Append(part))
                if (Normalize(name) is { Length: >= 2 } n) names.Add(n);
        return names;
    }

    /// <summary>The names a title stands for: the whole title, the title without its "(…)"
    /// parts, each of those parts ("Ego (미워 (Ego))" → 미워, ego, …), and the Hangul and the
    /// Latin half of a bilingual title ("우주를 건너 Across the universe"). Credits ("feat. …")
    /// and version notes ("Inst.", "Live") aren't names of the song.</summary>
    internal static HashSet<string> TitleVariants(string title)
    {
        var stripped = StripBrackets(title);
        var raw = new List<string> { title, stripped };
        raw.AddRange(ScriptParts(stripped));
        var inner = new StringBuilder();
        var depth = 0;
        foreach (var c in title)
        {
            var opens = c is '(' or '[' or '（';
            if (opens || c is ')' or ']' or '）')
            {
                if (depth > 0) raw.Add(inner.ToString());
                inner.Clear();
                depth = opens ? depth + 1 : Math.Max(0, depth - 1);
            }
            else if (depth > 0) inner.Append(c);
        }

        var variants = new HashSet<string>();
        foreach (var r in raw)
        {
            var words = Words(r);
            if (words.Count == 0 || words.All(VersionWords.Contains)) continue;
            if (r.TrimStart().Split(' ')[0].ToLowerInvariant().TrimEnd('.', ':') is "feat" or "ft" or "with" or "prod") continue;
            if (Normalize(r) is { Length: >= 2 } n) variants.Add(n);
        }
        return variants;
    }

    /// <summary>The Hangul words and the Latin words of a text that has both, each as one part
    /// ("우주를 건너 Across the universe" → "우주를 건너", "Across the universe"); nothing for a
    /// text in one script, or one mixing them inside a word ("OOH-AHH하게").</summary>
    private static List<string> ScriptParts(string text)
    {
        var hangul = new List<string>();
        var latin = new List<string>();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var hasHangul = word.Any(IsHangul);
            var hasLatin = word.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z');
            if (hasHangul && hasLatin) return [];
            if (hasHangul) hangul.Add(word);
            else if (hasLatin) latin.Add(word);
        }
        return hangul.Count > 0 && latin.Count > 0 ? [string.Join(' ', hangul), string.Join(' ', latin)] : [];
    }

    private async Task<LrcLibTrack?> TryGetAsync(LyricsQuery query, string title, CancellationToken ct)
    {
        var url = new StringBuilder($"get?artist_name={Esc(query.Artist)}&track_name={Esc(title)}");
        if (!string.IsNullOrWhiteSpace(query.Album)) url.Append("&album_name=").Append(Esc(query.Album));
        if (query.Seconds is { } seconds) url.Append("&duration=").Append(Math.Round(seconds).ToString(CultureInfo.InvariantCulture));
        try
        {
            var resp = await GetAsync(url.ToString(), ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<LrcLibTrack>(cancellationToken: ct);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<List<LrcLibTrack>> SearchAsync(string url, CancellationToken ct)
    {
        try
        {
            var resp = await GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];
            return await resp.Content.ReadFromJsonAsync<List<LrcLibTrack>>(cancellationToken: ct) ?? [];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return [];
        }
    }

    /// <summary>A GET that waits out one "too many requests" answer (LRCLIB turns away bursts
    /// of lookups, e.g. during auto-fill) instead of taking it for "no lyrics".</summary>
    private async Task<HttpResponseMessage> GetAsync(string url, CancellationToken ct)
    {
        var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode != System.Net.HttpStatusCode.TooManyRequests) return resp;
        var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
        resp.Dispose();
        await Task.Delay(wait < TimeSpan.FromSeconds(5) ? wait : TimeSpan.FromSeconds(5), ct);
        return await _http.GetAsync(url, ct);
    }

    private static bool HasSynced(LrcLibTrack track) => !string.IsNullOrWhiteSpace(track.SyncedLyrics);

    private static string Esc(string value) => Uri.EscapeDataString(value);

    private static string StripFeature(string title)
    {
        var idx = title.IndexOf("(feat.", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = title.IndexOf("(with ", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) idx = title.IndexOf("(prod.", StringComparison.OrdinalIgnoreCase);
        return idx <= 0 ? title : title[..idx].Trim();
    }

    /// <summary>Drops every "(…)" and "[…]" part: "Good Day (Japanese Ver.)" → "Good Day".</summary>
    private static string StripBrackets(string title)
    {
        var sb = new StringBuilder();
        var depth = 0;
        foreach (var c in title)
        {
            if (c is '(' or '[' or '（') depth++;
            else if (c is ')' or ']' or '）') depth = Math.Max(0, depth - 1);
            else if (depth == 0) sb.Append(c);
        }
        return sb.ToString();
    }

    private static HashSet<string> Words(string s) =>
        [.. s.ToLowerInvariant().Split([' ', '(', ')', '[', ']', '.', '-', '/', '_'], StringSplitOptions.RemoveEmptyEntries)];

    private static string Normalize(string s) =>
        new([.. s.ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    private static LyricsResult ToResult(LrcLibTrack dto) => new()
    {
        TrackName = dto.TrackName,
        ArtistName = dto.ArtistName,
        AlbumName = dto.AlbumName,
        Instrumental = dto.Instrumental,
        PlainLyrics = dto.PlainLyrics,
        SyncedLyrics = dto.SyncedLyrics,
    };

    public void Dispose() => _http.Dispose();

    internal sealed class LrcLibTrack
    {
        [JsonPropertyName("trackName")] public string TrackName { get; set; } = "";
        [JsonPropertyName("artistName")] public string ArtistName { get; set; } = "";
        [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
        [JsonPropertyName("duration")] public double Duration { get; set; }
        [JsonPropertyName("instrumental")] public bool Instrumental { get; set; }
        [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
        [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
    }
}
