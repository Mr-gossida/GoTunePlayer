using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Builds and applies tag-gap-filling proposals. BuildProposalsAsync runs identically
/// regardless of "완전 자동" vs "검토 후 적용" - the caller (AutoMatchViewModel) is what decides
/// whether to call ApplyProposals immediately on everything or wait for the user to pick rows.
/// Keeping that decision entirely in the caller is what lets both modes share one pipeline.</summary>
public sealed class AutoFillService(IMusicBrainzService musicBrainz, ILyricsService lyrics) : IAutoFillService
{
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
            if (wantTags || wantCover)
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

            if (wantTags && match is not null)
                FillProposedTagFields(proposal, file.Tags, match);

            if (wantCover && !string.IsNullOrWhiteSpace(match?.ReleaseMbid))
            {
                try { proposal.ProposedCoverBytes = await musicBrainz.GetCoverAsync(match.ReleaseMbid, ct); }
                catch (Exception) when (ct.IsCancellationRequested == false) { /* best-effort */ }
            }

            if (wantLyrics)
            {
                try
                {
                    var lyricsResult = await lyrics.FindAsync(match?.Artist ?? artist, match?.Title ?? title, ct);
                    proposal.ProposedLyrics = lyricsResult?.BestLyrics;
                }
                catch (Exception) when (ct.IsCancellationRequested == false) { /* best-effort */ }
            }

            proposal.Status = proposal.HasAnyProposal ? "제안 생성됨"
                : searchError is not null ? $"검색 오류: {searchError}"
                : match is null && !wantLyrics ? "매칭 실패"
                : "찾지 못함";
        }

        return proposals;
    }

    private static void FillProposedTagFields(AutoFillProposal proposal, TrackTags current, MusicBrainzMatch match)
    {
        if (string.IsNullOrWhiteSpace(current.Title) && !string.IsNullOrWhiteSpace(match.Title))
            proposal.ProposedTitle = match.Title;
        if (string.IsNullOrWhiteSpace(current.Artist) && !string.IsNullOrWhiteSpace(match.Artist))
            proposal.ProposedArtist = match.Artist;
        if (string.IsNullOrWhiteSpace(current.Album) && !string.IsNullOrWhiteSpace(match.Album))
            proposal.ProposedAlbum = match.Album;
        if (string.IsNullOrWhiteSpace(current.AlbumArtist) && !string.IsNullOrWhiteSpace(match.AlbumArtist))
            proposal.ProposedAlbumArtist = match.AlbumArtist;
        if (current.Track == 0 && match.Track is > 0)
            proposal.ProposedTrack = match.Track;
        if (current.Disc == 0 && match.Disc is > 0)
            proposal.ProposedDisc = match.Disc;
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
