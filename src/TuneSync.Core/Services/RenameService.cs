using System.Text.Json;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Renames audio files (and their .lrc sidecar, if any) from their own tags, using a
/// user-editable <see cref="RenameRuleSet"/>. Building a preview never touches disk; executing it
/// only ever auto-suffixes on a name collision, never silently overwrites another file.</summary>
public static class RenameService
{
    public static string BuildFileName(TrackTags tags, RenameRuleSet rules, bool isAlbumTrack)
    {
        var pattern = isAlbumTrack ? rules.AlbumTrackPattern : rules.SingleTrackPattern;
        return NamePatternFormatter.Format(pattern, tags, rules);
    }

    public static List<RenamePreviewItem> BuildPreview(IEnumerable<string> filePaths, RenameRuleSet rules, bool isAlbumTrack)
    {
        var items = new List<RenamePreviewItem>();

        foreach (var path in filePaths)
        {
            TrackTags tags;
            try
            {
                tags = TagService.Read(path);
            }
            catch
            {
                continue; // unreadable file - nothing we can safely propose for it
            }

            var newStem = BuildFileName(tags, rules, isAlbumTrack);
            if (string.IsNullOrWhiteSpace(newStem))
            {
                items.Add(new RenamePreviewItem { OldPath = path, NewPath = path, Status = "태그 부족 - 변경 없음" });
                continue;
            }

            var newName = newStem + Path.GetExtension(path);
            var newPath = Path.Combine(Path.GetDirectoryName(path)!, newName);
            items.Add(new RenamePreviewItem { OldPath = path, NewPath = newPath });
        }

        return items;
    }

    /// <summary>Reads the tags of each file once (the slow part), so the names can then be worked
    /// out again instantly for every pattern edit (<see cref="Plan"/>). Unreadable files are skipped.</summary>
    public static List<(string Path, TrackTags Tags)> ReadTargets(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var result = new List<(string, TrackTags)>();
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { result.Add((path, TagService.Read(path))); }
            catch (Exception) when (!ct.IsCancellationRequested) { /* unreadable: nothing to propose */ }
        }
        return result;
    }

    private static readonly (string Token, string Label, Func<TrackTags, bool> IsEmpty)[] Tokens =
    [
        ("{Artist}", "아티스트", t => string.IsNullOrWhiteSpace(t.Artist)),
        ("{Title}", "제목", t => string.IsNullOrWhiteSpace(t.Title)),
        ("{Album}", "앨범", t => string.IsNullOrWhiteSpace(t.Album)),
        ("{AlbumArtist}", "앨범 아티스트", t => string.IsNullOrWhiteSpace(t.AlbumArtist)),
        ("{Track}", "트랙", t => t.Track == 0),
        ("{Year}", "연도", t => t.Year == 0),
    ];

    private static bool HasForbiddenChars(string pattern, TrackTags tags) =>
        new[] { ("{Artist}", tags.Artist), ("{Title}", tags.Title), ("{Album}", tags.Album), ("{AlbumArtist}", tags.AlbumArtist) }
            .Any(t => pattern.Contains(t.Item1, StringComparison.Ordinal) && t.Item2.IndexOfAny(RenameRuleSet.ForbiddenChars) >= 0);

    /// <summary>Whether <paramref name="mode"/> names this song with the album pattern.</summary>
    public static bool UsesAlbumPattern(TrackTags tags, RenameMode mode) =>
        mode == RenameMode.Album || mode == RenameMode.Auto && tags.Track > 0 && !string.IsNullOrWhiteSpace(tags.Album);

    /// <summary>The new names, without touching disk beyond checking which names are taken. Each
    /// row says whether it changes, stays, lacks a tag the pattern needs (left unticked), or had
    /// to take " (2)"... because the name exists already - on disk or earlier in this batch.</summary>
    public static List<RenamePreviewItem> Plan(IReadOnlyList<(string Path, TrackTags Tags)> files, RenameRuleSet rules, RenameMode mode)
    {
        var items = new List<RenamePreviewItem>(files.Count);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, tags) in files)
        {
            var pattern = UsesAlbumPattern(tags, mode) ? rules.AlbumTrackPattern : rules.SingleTrackPattern;
            var missing = Tokens.Where(t => pattern.Contains(t.Token, StringComparison.Ordinal) && t.IsEmpty(tags)).Select(t => t.Label).ToList();
            var stem = NamePatternFormatter.Format(pattern, tags, rules);
            var dir = Path.GetDirectoryName(path)!;
            var ext = Path.GetExtension(path);

            if (string.IsNullOrWhiteSpace(stem))
            {
                items.Add(new RenamePreviewItem
                {
                    OldPath = path, NewPath = path, Kind = RenameKind.MissingTags, IsIncluded = false,
                    Note = missing.Count > 0 ? "태그 없음: " + string.Join(", ", missing) : "필요한 태그가 없음",
                });
                claimed.Add(path);
                continue;
            }

            var wanted = Path.Combine(dir, stem + ext);
            if (string.Equals(wanted, path, StringComparison.Ordinal))
            {
                items.Add(new RenamePreviewItem { OldPath = path, NewPath = path, Kind = RenameKind.Same, IsIncluded = false });
                claimed.Add(path);
                continue;
            }

            // Taken: by another file on disk (even one of this batch - it may not have moved away
            // yet when this one is renamed), or by an earlier row of this batch.
            bool Taken(string candidate) =>
                claimed.Contains(candidate)
                || !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate);
            var final = wanted;
            for (var n = 2; Taken(final); n++)
                final = Path.Combine(dir, $"{stem} ({n}){ext}");
            claimed.Add(final);

            var kind = missing.Count > 0 ? RenameKind.MissingTags
                : !string.Equals(final, wanted, StringComparison.Ordinal) ? RenameKind.Collision
                : RenameKind.Change;
            var notes = new List<string>();
            if (kind == RenameKind.MissingTags) notes.Add("태그 없음: " + string.Join(", ", missing));
            if (!string.Equals(final, wanted, StringComparison.Ordinal)) notes.Add("같은 이름이 있어 번호를 붙임");
            if (HasForbiddenChars(pattern, tags)) notes.Add("파일명에 쓸 수 없는 문자를 바꿈");
            items.Add(new RenamePreviewItem
            {
                OldPath = path,
                NewPath = final,
                Kind = kind,
                IsIncluded = kind != RenameKind.MissingTags,
                Note = string.Join(" · ", notes),
            });
        }
        return items;
    }

    /// <summary>Renames every item whose name actually changes. A destination that collides with
    /// an existing file (other than the source itself) gets a " (2)", " (3)", ... suffix instead
    /// of overwriting it. Returns one error message per file that failed.</summary>
    public static List<string> Execute(IEnumerable<RenamePreviewItem> items) => Execute(items, []);

    /// <summary>Like <see cref="Execute(IEnumerable{RenamePreviewItem})"/>, recording every move
    /// (song and .lrc) in <paramref name="moves"/> as it happens - so even a run stopped halfway
    /// can be put back with <see cref="Undo"/>. Unticked items are left alone.</summary>
    public static List<string> Execute(IEnumerable<RenamePreviewItem> items, List<(string From, string To)> moves,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var done = 0;

        foreach (var item in items)
        {
            ct.ThrowIfCancellationRequested();
            if (!item.WillChange || !item.IsIncluded)
            {
                item.Status = "변경 없음";
                continue;
            }

            try
            {
                var destPath = ResolveCollision(item.NewPath, item.OldPath);
                File.Move(item.OldPath, destPath);
                moves.Add((item.OldPath, destPath));

                var oldLrc = Path.ChangeExtension(item.OldPath, ".lrc");
                if (File.Exists(oldLrc))
                {
                    var destLrc = ResolveCollision(Path.ChangeExtension(destPath, ".lrc"), oldLrc);
                    File.Move(oldLrc, destLrc);
                    moves.Add((oldLrc, destLrc));
                }

                item.Status = "변경 완료";
                progress?.Report(++done);
            }
            catch (Exception ex)
            {
                errors.Add($"{item.OldFileName}: {ex.Message}");
                item.Status = $"실패: {ex.Message}";
            }
        }

        return errors;
    }

    /// <summary>Puts recorded moves back, newest first. Returns the files it couldn't restore.</summary>
    public static List<string> Undo(IReadOnlyList<(string From, string To)> moves)
    {
        var errors = new List<string>();
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            var (from, to) = moves[i];
            try
            {
                if (!File.Exists(to)) throw new IOException("파일이 그 사이 옮겨졌거나 지워졌습니다.");
                if (File.Exists(from) && !string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("원래 이름을 다른 파일이 쓰고 있습니다.");
                File.Move(to, from);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(to)}: {ex.Message}");
            }
        }
        return errors;
    }

    private static string ResolveCollision(string destPath, string sourcePath)
    {
        if (!File.Exists(destPath) || string.Equals(destPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            return destPath;

        var dir = Path.GetDirectoryName(destPath)!;
        var stem = Path.GetFileNameWithoutExtension(destPath);
        var ext = Path.GetExtension(destPath);

        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    public static RenameRuleSet Load(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return new RenameRuleSet();
        var json = File.ReadAllText(jsonPath);
        return JsonSerializer.Deserialize<RenameRuleSet>(json) ?? new RenameRuleSet();
    }

    public static void Save(RenameRuleSet rules, string jsonPath)
    {
        var json = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(jsonPath)!);
        File.WriteAllText(jsonPath, json);
    }
}
