using System.Text.Json;
using TuneSync.Core.Models;
using TuneSync.Core.Services.CoverSearch;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.Core.Services;

/// <summary>Builds and applies tag-gap-filling proposals. BuildProposalsAsync runs identically
/// regardless of "완전 자동" vs "검토 후 적용" - the caller (AutoMatchViewModel) is what decides
/// whether to call ApplyProposals immediately on everything or wait for the user to pick rows.
/// Keeping that decision entirely in the caller is what lets both modes share one pipeline.
/// With <paramref name="tagSearch"/>, tags come from every tag source the user has on and lyrics
/// from LRCLIB and the files beside the song (as for one song), and every result is kept on the
/// proposal with where it's from, for the user to pick another.</summary>
public sealed class AutoFillService(IMusicBrainzService musicBrainz, ILyricsService lyrics, ICoverSearchService? covers = null,
    TagSearchService? tagSearch = null) : IAutoFillService
{
    /// <summary>A cover put in without the user choosing it must match at least this well.</summary>
    public const double AutoCoverMinScore = 0.7;

    /// <summary>A tag result put in without the user choosing it must fit at least this well: title
    /// and artist both match. An exact title by an artist written otherwise ("IU" for "아이유")
    /// scores about 0.6 and waits for the user to pick it.</summary>
    public const double AutoTagMinScore = 0.7;

    /// <summary>The most results kept for a song to choose from.</summary>
    private const int MaxKept = 6;

    public async Task<List<AutoFillProposal>> BuildProposalsAsync(
        IEnumerable<LibraryFile> files,
        IProgress<string>? progress = null,
        CancellationToken ct = default,
        AutoFillOptions? options = null)
    {
        var opt = options ?? new AutoFillOptions();
        var proposals = new List<AutoFillProposal>();

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();

            var proposal = new AutoFillProposal { File = file };
            proposals.Add(proposal);

            var wantTags = opt.Tags;
            var wantCover = opt.Cover && (file.IsMissingCover || opt.ReplaceExisting);
            var wantLyrics = opt.Lyrics && (file.IsMissingLyrics || opt.ReplaceExisting);
            if (!wantTags && !wantCover && !wantLyrics)
            {
                proposal.Status = "채울 항목 없음";
                continue;
            }

            var (artist, title) = ResolveSearchKey(file);
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
            {
                proposal.Status = "검색 불가 (아티스트/제목 정보 없음)";
                continue;
            }

            progress?.Report($"{file.FileName} 검색 중...");

            // MusicBrainz is only needed for tags and cover art; a lyrics-only search goes straight
            // to the lyrics service with the artist/title already known (faster, and it still works
            // when MusicBrainz has no match).
            MusicBrainzMatch? match = null;
            string? searchError = null;
            var manySources = wantTags && tagSearch is not null;
            // With the cover search, a cover-only search doesn't need the MusicBrainz match: the
            // cover search asks MusicBrainz (and the other sources) itself.
            if ((wantTags && !manySources) || (wantCover && covers is null))
            {
                try
                {
                    match = await musicBrainz.FindAsync(artist, title, ct);
                }
                catch (Exception ex) when (ct.IsCancellationRequested == false)
                {
                    searchError = ex.Message;
                }
            }

            if (manySources)
            {
                try
                {
                    var album = string.IsNullOrWhiteSpace(file.Tags.Album) ? null : file.Tags.Album;
                    var found = await tagSearch!.SearchAsync(new TagQuery(artist, title, album, file.Duration), opt.DisabledTagSources, null, ct);
                    proposal.ReplaceTags = opt.ReplaceTags;
                    proposal.TagCandidates = [.. found.Candidates.Take(MaxKept)];
                    proposal.FailedSources.AddRange(found.FailedSources);
                    if (found.Candidates.FirstOrDefault() is { Score: >= AutoTagMinScore } best) proposal.UseTags(best);
                }
                catch (Exception ex) when (ct.IsCancellationRequested == false)
                {
                    searchError = ex.Message;
                }
            }
            else if (wantTags && match is not null)
            {
                FillProposedTagFields(proposal, file.Tags, match, opt.ReplaceTags);
            }

            // the album the song is on, to tell versions apart (live, remix, a foreign release)
            var knownAlbum = !string.IsNullOrWhiteSpace(file.Tags.Album) ? file.Tags.Album
                : proposal.ChosenTags is { Album.Length: > 0 } chosen ? chosen.Album
                : match?.Album;

            if (wantCover && covers is not null)
            {
                try
                {
                    var found = await covers.SearchAsync(new CoverQuery(artist, title, knownAlbum, file.Duration), null, ct);
                    // The best few that match well enough; the first whose image downloads wins.
                    foreach (var candidate in found.Candidates.Where(c => c.Score >= AutoCoverMinScore).Take(3))
                    {
                        proposal.ProposedCoverBytes = await covers.GetFullAsync(candidate, ct);
                        if (proposal.ProposedCoverBytes is null) continue;
                        proposal.CoverSource = candidate.Source;
                        break;
                    }
                }
                catch (Exception ex) when (ct.IsCancellationRequested == false) { searchError ??= ex.Message; }
            }
            else if (wantCover && !string.IsNullOrWhiteSpace(match?.ReleaseMbid))
            {
                try
                {
                    proposal.ProposedCoverBytes = await musicBrainz.GetCoverAsync(match.ReleaseMbid, ct);
                    if (proposal.ProposedCoverBytes is not null) proposal.CoverSource = "MusicBrainz";
                }
                catch (Exception) when (ct.IsCancellationRequested == false) { /* best-effort */ }
            }

            if (wantLyrics) await FindLyricsAsync(proposal, artist, title, knownAlbum, opt.DisabledLyricsSources, ct);

            proposal.Status = proposal.HasAnyProposal ? "제안 생성됨"
                : proposal.NeedsChoice ? "확실한 결과 없음"
                : proposal.ChosenTags is not null ? "지금 태그와 같음"
                : searchError is not null ? $"검색 오류: {searchError}"
                : match is null && !wantLyrics && !(wantCover && covers is not null) && !manySources ? "매칭 실패"
                : "찾지 못함";
        }

        return proposals;
    }

    /// <summary>The lyrics beside the song and LRCLIB's (those the user has on), all kept; the ones
    /// clearly the song's proposed (<see cref="LyricsSearchResult.Pick"/>).</summary>
    private async Task FindLyricsAsync(AutoFillProposal proposal, string artist, string title, string? album,
        IReadOnlyCollection<string> disabled, CancellationToken ct)
    {
        var found = new List<LyricsCandidate>();
        if (!disabled.Contains(LocalLyricsSource.SourceName))
            found.AddRange(LocalLyricsSource.Find(proposal.File.Path, title, artist));
        var certain = true;
        if (!disabled.Contains(LrcLibLyricsService.SourceName))
        {
            try
            {
                var online = await lyrics.SearchAsync(new LyricsQuery(artist, title, album, proposal.File.Duration), ct);
                found.AddRange(online.Candidates);
                certain = online.Certain;
            }
            catch (Exception) when (ct.IsCancellationRequested == false)
            {
                proposal.FailedSources.Add(LrcLibLyricsService.SourceName); // the files beside the song still count
            }
        }
        proposal.LyricsCandidates = [.. found.Take(MaxKept)];
        proposal.UseLyrics(LyricsSearchResult.Pick(proposal.LyricsCandidates, certain));
    }

    /// <summary>Empty fields get the match's values; with <paramref name="replace"/>, fields whose
    /// value differs from the match's do too.</summary>
    private static void FillProposedTagFields(AutoFillProposal proposal, TrackTags current, MusicBrainzMatch match, bool replace)
    {
        bool Text(string now, string? found) =>
            !string.IsNullOrWhiteSpace(found) && (string.IsNullOrWhiteSpace(now)
                || replace && !string.Equals(now.Trim(), found.Trim(), StringComparison.OrdinalIgnoreCase)); // not for capitalization alone
        bool Number(uint now, uint? found) => found is > 0 && (now == 0 || replace && now != found);

        if (Text(current.Title, match.Title)) proposal.ProposedTitle = match.Title;
        if (Text(current.Artist, match.Artist)) proposal.ProposedArtist = match.Artist;
        if (Text(current.Album, match.Album)) proposal.ProposedAlbum = match.Album;
        if (Text(current.AlbumArtist, match.AlbumArtist)) proposal.ProposedAlbumArtist = match.AlbumArtist;
        if (Number(current.Track, match.Track)) proposal.ProposedTrack = match.Track;
        if (Number(current.Disc, match.Disc)) proposal.ProposedDisc = match.Disc;
    }

    /// <summary>What the cover search looks for: the tags, or the file name when they're empty.</summary>
    public static CoverQuery CoverQueryFor(LibraryFile file)
    {
        var (artist, title) = ResolveSearchKey(file);
        return new CoverQuery(artist, title, string.IsNullOrWhiteSpace(file.Tags.Album) ? null : file.Tags.Album, file.Duration);
    }

    private static (string Artist, string Title) ResolveSearchKey(LibraryFile file)
    {
        if (!string.IsNullOrWhiteSpace(file.Tags.Artist) && !string.IsNullOrWhiteSpace(file.Tags.Title))
            return (file.Tags.Artist, file.Tags.Title);

        var parsed = FilenameParser.Parse(Path.GetFileNameWithoutExtension(file.Path));
        var artist = !string.IsNullOrWhiteSpace(file.Tags.Artist) ? file.Tags.Artist : parsed?.Artist ?? "";
        var title = !string.IsNullOrWhiteSpace(file.Tags.Title) ? file.Tags.Title : parsed?.Title ?? "";
        return (artist, title);
    }

    public List<string> ApplyProposals(IEnumerable<AutoFillProposal> proposals, string? undoLogDirectory = null)
    {
        var errors = new List<string>();
        var undoEntries = new List<AutoFillUndoEntry>();

        foreach (var proposal in proposals.Where(p => p.HasAnyProposal))
        {
            try
            {
                var tags = proposal.File.Tags;
                var before = tags.Clone();

                if (proposal.ProposedTitle is not null) tags.Title = proposal.ProposedTitle;
                if (proposal.ProposedArtist is not null) tags.Artist = proposal.ProposedArtist;
                if (proposal.ProposedAlbum is not null) tags.Album = proposal.ProposedAlbum;
                if (proposal.ProposedAlbumArtist is not null) tags.AlbumArtist = proposal.ProposedAlbumArtist;
                if (proposal.ProposedYear is not null) tags.Year = proposal.ProposedYear.Value;
                if (proposal.ProposedGenre is not null) tags.Genre = proposal.ProposedGenre;
                if (proposal.ProposedTrack is not null) tags.Track = proposal.ProposedTrack.Value;
                if (proposal.ProposedDisc is not null) tags.Disc = proposal.ProposedDisc.Value;

                TagService.Write(tags);

                if (proposal.ProposedCoverBytes is { Length: > 0 })
                {
                    CoverArtService.SetCover(proposal.File.Path, proposal.ProposedCoverBytes);
                    proposal.File.CoverBytes = proposal.ProposedCoverBytes;
                    proposal.File.ThumbnailBytes = CoverArtService.GetThumbnail(proposal.ProposedCoverBytes)?.JpegBytes;
                    proposal.File.HasCover = true;
                }

                if (!string.IsNullOrWhiteSpace(proposal.ProposedLyrics))
                {
                    TagService.WriteLyrics(proposal.File.Path, proposal.ProposedLyrics);
                    proposal.File.HasLyrics = true;
                    proposal.File.IsLyricsSynced = LrcParser.IsSynced(proposal.ProposedLyrics);
                }

                undoEntries.Add(new AutoFillUndoEntry { Path = proposal.File.Path, Before = before });
                proposal.Status = "적용 완료";
            }
            catch (Exception ex)
            {
                errors.Add($"{proposal.File.FileName}: {ex.Message}");
                proposal.Status = $"적용 실패: {ex.Message}";
            }
        }

        if (undoLogDirectory is not null && undoEntries.Count > 0)
            TryWriteUndoLog(undoLogDirectory, undoEntries);

        return errors;
    }

    private static void TryWriteUndoLog(string directory, List<AutoFillUndoEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"autofill-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch
        {
            // best-effort undo log; failure here shouldn't undo the apply that already succeeded
        }
    }
}
