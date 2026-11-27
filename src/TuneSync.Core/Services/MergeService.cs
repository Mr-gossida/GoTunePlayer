using System.Text;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Builds a dry-run plan for folding one folder's tracks into another, flags duplicates
/// by normalized artist+title, and only ever *moves* the losing side into a quarantine folder -
/// never deletes - so a bad call is one drag-and-drop away from undone.</summary>
public static class MergeService
{
    /// <summary>Pure analysis - reads tags and directory listings, touches no files. Call
    /// <see cref="ExecutePlan"/> with the result to actually move anything.</summary>
    public static List<MergeAction> BuildPlan(string sourceFolder, string targetFolder, MergeRuleSet rules, CancellationToken ct = default)
    {
        var targetIndex = BuildTargetIndex(targetFolder, ct);
        var plan = new List<MergeAction>();
        // File names the target root will hold: what's there now plus what this plan moves in, so
        // two songs that happen to share a file name never collide (everything lands in the root).
        var claimed = new HashSet<string>(
            Directory.Exists(targetFolder) ? Directory.EnumerateFiles(targetFolder, "*", Listing(false)).Select(p => Path.GetFileName(p)) : [],
            StringComparer.OrdinalIgnoreCase);
        // The songs this plan brings into the target from the source, by song: a second copy of one
        // inside the source is weighed against the first by the same rule - both used to go in.
        var incoming = new Dictionary<string, int>();

        var sourceFiles = MediaLibraryScanner.WalkFiles(sourceFolder, rules.IncludeSubfolders, FileAttributes.Hidden | FileAttributes.System)
            .Select(f => f.FullName)
            .Where(TagService.IsSupported);

        foreach (var sourcePath in sourceFiles)
        {
            ct.ThrowIfCancellationRequested();
            var key = ResolveKey(sourcePath);
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            var lrcPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, stem + ".lrc");
            var lyricsPath = File.Exists(lrcPath) ? lrcPath : null;
            var sourceSize = new FileInfo(sourcePath).Length;
            var destFileName = Path.GetFileName(sourcePath);

            if (key is not null && incoming.TryGetValue(key, out var firstIndex))
            {
                plan.Add(AgainstEarlier(plan, firstIndex, rules, sourcePath, lyricsPath, sourceSize, claimed, out var newWinner));
                if (newWinner) incoming[key] = plan.Count - 1;
                continue;
            }

            if (key is null || !targetIndex.TryGetValue(key, out var conflictPath))
            {
                var taken = claimed.Contains(destFileName);
                var dest = taken ? UniqueAmong(destFileName, claimed) : destFileName;
                claimed.Add(dest);
                plan.Add(new MergeAction
                {
                    SourcePath = sourcePath,
                    SourceLyricsPath = lyricsPath,
                    DestFileName = dest,
                    ActionType = taken ? MergeActionType.RenameAndMove : MergeActionType.Move,
                    Reason = taken ? "다른 곡이 같은 파일 이름을 쓰고 있어 번호를 붙여 이동"
                        : key is null ? "태그/파일명에서 곡 정보를 읽지 못함 - 그대로 이동" : "받을 폴더에 같은 곡 없음",
                    SourceSize = sourceSize,
                });
                if (key is not null) incoming[key] = plan.Count - 1;
                continue;
            }

            var conflictSize = new FileInfo(conflictPath).Length;
            var action = BuildConflictAction(rules, sourcePath, lyricsPath, destFileName, conflictPath, sourceSize, conflictSize);
            if (action.ActionType == MergeActionType.Replace)
                claimed.Remove(Path.GetFileName(conflictPath)); // moves out to quarantine
            if (action.ActionType != MergeActionType.Quarantine && claimed.Contains(action.DestFileName))
                action = Override(action, action.ActionType, UniqueAmong(action.DestFileName, claimed));
            if (action.ActionType != MergeActionType.Quarantine) claimed.Add(action.DestFileName);
            plan.Add(action);
            if (action.ActionType is MergeActionType.Replace or MergeActionType.RenameAndMove) incoming[key] = plan.Count - 1;
        }

        return plan;
    }

    /// <summary>A song the source holds twice (or more): the later copy against the one already
    /// headed for the target, by the same rule. Keep-both moves it in under a free name; when the
    /// rule prefers the later copy, it takes the earlier one's place in the plan (same name, same
    /// replaced file) and the earlier one goes to quarantine; otherwise the later one does. These
    /// rows have no file in the target to compare with, so their choice isn't changed by hand.</summary>
    private static MergeAction AgainstEarlier(List<MergeAction> plan, int firstIndex, MergeRuleSet rules, string sourcePath,
        string? lyricsPath, long sourceSize, HashSet<string> claimed, out bool newWinner)
    {
        var first = plan[firstIndex];
        var verdict = BuildConflictAction(rules, sourcePath, lyricsPath, Path.GetFileName(sourcePath), first.SourcePath, sourceSize, first.SourceSize);
        newWinner = false;
        MergeAction Quarantined(string path, string? lyrics, long size, string why) => new()
        {
            SourcePath = path,
            SourceLyricsPath = lyrics,
            DestFileName = Path.GetFileName(path),
            ActionType = MergeActionType.Quarantine,
            Reason = why,
            SourceSize = size,
        };

        switch (verdict.ActionType)
        {
            case MergeActionType.RenameAndMove:
                var name = UniqueAmong(Path.GetFileName(sourcePath), claimed);
                claimed.Add(name);
                return new MergeAction
                {
                    SourcePath = sourcePath,
                    SourceLyricsPath = lyricsPath,
                    DestFileName = name,
                    ActionType = MergeActionType.RenameAndMove,
                    Reason = "옮길 폴더에 같은 곡이 또 있음 · 규칙: 둘 다 보존 (이름 변경)",
                    SourceSize = sourceSize,
                };
            case MergeActionType.Replace:
                newWinner = true;
                plan[firstIndex] = Quarantined(first.SourcePath, first.SourceLyricsPath, first.SourceSize,
                    $"옮길 폴더 안의 같은 곡 {Path.GetFileName(sourcePath)}을(를) 대신 옮김 · {verdict.Reason}");
                // It goes in under its own name (its own extension), in place of the first one's.
                claimed.Remove(first.DestFileName);
                var own = Path.GetFileName(sourcePath);
                var dest = claimed.Contains(own) ? UniqueAmong(own, claimed) : own;
                claimed.Add(dest);
                return new MergeAction
                {
                    SourcePath = sourcePath,
                    SourceLyricsPath = lyricsPath,
                    DestFileName = dest,
                    // (still replacing - or keeping beside - the target's copy the first one met)
                    ActionType = first.ConflictWithPath is not null ? first.ActionType
                        : dest == own ? MergeActionType.Move : MergeActionType.RenameAndMove,
                    Reason = first.Reason + " · 옮길 폴더 안의 같은 곡 중 이쪽을 옮김",
                    ConflictWithPath = first.ConflictWithPath,
                    SourceSize = sourceSize,
                    ConflictSize = first.ConflictSize,
                };
            default:
                return Quarantined(sourcePath, lyricsPath, sourceSize,
                    $"옮길 폴더 안의 같은 곡 {Path.GetFileName(first.SourcePath)}을(를) 옮김 · {verdict.Reason}");
        }
    }

    /// <summary>A listing that leaves out hidden and system items ($RECYCLE.BIN, System Volume
    /// Information - a drive root's own) and skips folders it may not read, instead of failing.</summary>
    private static EnumerationOptions Listing(bool recurse) => new()
    {
        RecurseSubdirectories = recurse,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private static string UniqueAmong(string fileName, HashSet<string> claimed)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!claimed.Contains(candidate)) return candidate;
        }
    }

    private static MergeAction BuildConflictAction(
        MergeRuleSet rules, string sourcePath, string? lyricsPath, string destFileName,
        string conflictPath, long sourceSize, long conflictSize)
    {
        switch (rules.ConflictResolution)
        {
            case MergeConflictResolution.KeepSourceAlways:
                return Conflict(MergeActionType.Replace, "규칙: 항상 소스 우선");

            case MergeConflictResolution.KeepTargetAlways:
                return Conflict(MergeActionType.Quarantine, "규칙: 항상 대상 우선");

            case MergeConflictResolution.RenameWithSuffix:
                return new MergeAction
                {
                    SourcePath = sourcePath,
                    SourceLyricsPath = lyricsPath,
                    DestFileName = MakeUniqueName(destFileName, conflictPath),
                    ActionType = MergeActionType.RenameAndMove,
                    Reason = "규칙: 동명 파일 모두 보존 (이름 변경)",
                    ConflictWithPath = conflictPath,
                    SourceSize = sourceSize,
                    ConflictSize = conflictSize,
                };

            case MergeConflictResolution.KeepNewerFile:
                var sourceIsNewer = File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(conflictPath);
                return sourceIsNewer
                    ? Conflict(MergeActionType.Replace, "규칙: 더 최신 파일 유지 (소스가 더 최신)")
                    : Conflict(MergeActionType.Quarantine, "규칙: 더 최신 파일 유지 (대상이 더 최신)");

            case MergeConflictResolution.KeepBetterQuality:
                var better = CompareQuality(sourcePath, conflictPath);
                if (better == 0) better = sourceSize.CompareTo(conflictSize);
                return better > 0
                    ? Conflict(MergeActionType.Replace, "규칙: 음질 좋은 쪽 유지 (소스가 더 좋음)")
                    : Conflict(MergeActionType.Quarantine, "규칙: 음질 좋은 쪽 유지 (대상이 같거나 더 좋음)");

            case MergeConflictResolution.KeepLargerFile:
            default:
                return sourceSize > conflictSize
                    ? Conflict(MergeActionType.Replace, "규칙: 더 큰 파일 유지 (소스가 더 큼)")
                    : Conflict(MergeActionType.Quarantine, "규칙: 더 큰 파일 유지 (대상이 더 크거나 같음)");
        }

        MergeAction Conflict(MergeActionType type, string reason) => new()
        {
            SourcePath = sourcePath,
            SourceLyricsPath = lyricsPath,
            DestFileName = destFileName,
            ActionType = type,
            Reason = reason,
            ConflictWithPath = conflictPath,
            SourceSize = sourceSize,
            ConflictSize = conflictSize,
        };
    }

    /// <summary>Ranks two files by sound quality: &gt;0 when <paramref name="a"/> is better, &lt;0
    /// when <paramref name="b"/> is, 0 when they are alike or can't be read.</summary>
    public static int CompareQuality(string a, string b)
    {
        static AudioQuality? Read(string path)
        {
            try { return TagService.ReadAudioQuality(path); } catch { return null; }
        }
        var qa = Read(a);
        var qb = Read(b);
        if (qa is null || qb is null) return 0;
        if (qa.IsLossless != qb.IsLossless) return qa.IsLossless ? 1 : -1;
        if (qa.IsLossless)
        {
            var c = ((long)qa.BitsPerSample * qa.SampleRate).CompareTo((long)qb.BitsPerSample * qb.SampleRate);
            if (c != 0) return c;
        }
        return qa.BitrateKbps.CompareTo(qb.BitrateKbps);
    }

    /// <summary>A conflict row with the user's own choice instead of the rule's: keep the source
    /// (<see cref="MergeActionType.Replace"/>), keep the target (<see cref="MergeActionType.Quarantine"/>)
    /// or keep both (<see cref="MergeActionType.RenameAndMove"/>).</summary>
    public static MergeAction Override(MergeAction action, MergeActionType type, string? destFileName = null)
    {
        if (action.ConflictWithPath is null || action.ActionType == type && destFileName is null) return action;
        var own = Path.GetFileName(action.SourcePath);
        // Keeping the incoming file under its own name: unless another file of the target (not the
        // one it replaces) has that name - then it takes a free one rather than failing to move in.
        var taken = File.Exists(Path.Combine(Path.GetDirectoryName(action.ConflictWithPath)!, own))
                    && !string.Equals(Path.GetFileName(action.ConflictWithPath), own, StringComparison.OrdinalIgnoreCase);
        var destName = destFileName ?? (type == MergeActionType.RenameAndMove || type == MergeActionType.Replace && taken
            ? MakeUniqueName(own, action.ConflictWithPath)
            : own);
        if (destFileName is not null && action.ActionType == type)
            return new MergeAction
            {
                SourcePath = action.SourcePath,
                SourceLyricsPath = action.SourceLyricsPath,
                DestFileName = destName,
                ActionType = type,
                Reason = action.Reason + " · 같은 파일 이름이 있어 번호를 붙임",
                ConflictWithPath = action.ConflictWithPath,
                SourceSize = action.SourceSize,
                ConflictSize = action.ConflictSize,
            };
        return new MergeAction
        {
            SourcePath = action.SourcePath,
            SourceLyricsPath = action.SourceLyricsPath,
            DestFileName = destName,
            ActionType = type,
            Reason = type switch
            {
                MergeActionType.Replace => "직접 선택: 소스 유지 (대상은 격리)",
                MergeActionType.Quarantine => "직접 선택: 대상 유지 (소스는 격리)",
                _ => "직접 선택: 둘 다 보존 (이름 변경)",
            },
            ConflictWithPath = action.ConflictWithPath,
            SourceSize = action.SourceSize,
            ConflictSize = action.ConflictSize,
        };
    }

    /// <summary>Applies a plan built by <see cref="BuildPlan"/>. Every file on the losing side of
    /// a conflict is moved into <paramref name="quarantineFolder"/>, never deleted. Returns a
    /// manifest of every move actually made, so a run can be reviewed or manually reversed.</summary>
    public static List<(string From, string To)> ExecutePlan(
        List<MergeAction> plan, string targetFolder, string quarantineFolder)
    {
        var manifest = new List<(string From, string To)>();
        var errors = ExecutePlan(plan, targetFolder, quarantineFolder, manifest);
        if (errors.Count > 0) throw new IOException(errors[0]);
        return manifest;
    }

    /// <summary>Like the other overload, but records each move in <paramref name="manifest"/> as
    /// it happens, so a run that fails or is cancelled halfway can still be undone
    /// (<see cref="Undo"/>). A row that fails (a locked file, a name already taken) is skipped
    /// and reported; the rest carry on. Reports how many plan rows are done.</summary>
    public static List<string> ExecutePlan(List<MergeAction> plan, string targetFolder, string quarantineFolder,
        List<(string From, string To)> manifest, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var done = 0;
        foreach (var action in plan)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (action.ActionType != MergeActionType.Move && action.ActionType != MergeActionType.RenameAndMove)
                    Directory.CreateDirectory(quarantineFolder);
                Apply(action, targetFolder, quarantineFolder, manifest);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{Path.GetFileName(action.SourcePath)}: {ex.Message}");
            }
            progress?.Report(++done);
        }
        return errors;
    }

    /// <summary>Puts every move of a manifest back, newest first. Returns what couldn't be restored.</summary>
    public static List<string> Undo(IReadOnlyList<(string From, string To)> manifest)
    {
        var errors = new List<string>();
        for (var i = manifest.Count - 1; i >= 0; i--)
        {
            var (from, to) = manifest[i];
            try
            {
                if (!File.Exists(to)) throw new IOException("파일이 그 사이 옮겨졌거나 지워졌습니다.");
                if (File.Exists(from)) throw new IOException("원래 자리에 다른 파일이 있습니다.");
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                File.Move(to, from);
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(to)}: {ex.Message}");
            }
        }
        return errors;
    }

    /// <summary>Removes folders under <paramref name="root"/> (and the root itself unless
    /// <paramref name="keepRoot"/>) that hold no files at all, deepest first. Returns how many.</summary>
    public static int RemoveEmptyFolders(string root, bool keepRoot = false)
    {
        if (!Directory.Exists(root)) return 0;
        var removed = 0;
        // (Hidden and system folders - a drive root's own - are left alone, so are linked folders
        // (not walked through, nor removed), and an unreadable one no longer throws out of here
        // over the result of the merge.)
        var listing = Listing(true);
        listing.AttributesToSkip |= FileAttributes.ReparsePoint;
        foreach (var dir in Directory.EnumerateDirectories(root, "*", listing)
                     .OrderByDescending(d => d.Length).ToList())
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(dir).Any()) { Directory.Delete(dir); removed++; }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        if (!keepRoot)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(root).Any()) { Directory.Delete(root); removed++; }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    private static void Apply(MergeAction action, string targetFolder, string quarantineFolder, List<(string From, string To)> manifest)
    {
        // The song itself must still be there: a file moved or deleted since the plan was made
        // used to be skipped without a word and the row reported as done.
        if (!File.Exists(action.SourcePath)) throw new IOException("옮길 파일이 없습니다 (계획을 만든 뒤 옮겨졌거나 지워짐).");
        var dest = Path.Combine(targetFolder, action.DestFileName);
        switch (action.ActionType)
        {
            case MergeActionType.Move:
            case MergeActionType.RenameAndMove:
                MoveTracked(action.SourcePath, dest, manifest);
                MoveSidecar(action, targetFolder, manifest);
                break;

            case MergeActionType.Replace:
                // The name may be taken by a file other than the one replaced (a choice changed by
                // hand): found out now, before anything moves.
                if (File.Exists(dest) && !string.Equals(Path.GetFullPath(dest), Path.GetFullPath(action.ConflictWithPath!), StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"받을 폴더에 같은 이름의 다른 파일({action.DestFileName})이 있습니다.");
                var start = manifest.Count;
                var conflictLrc = Path.ChangeExtension(action.ConflictWithPath!, ".lrc");
                MoveTracked(action.ConflictWithPath!, Quarantined(quarantineFolder, action.ConflictWithPath!), manifest);
                if (File.Exists(conflictLrc))
                    MoveTracked(conflictLrc, Quarantined(quarantineFolder, conflictLrc), manifest);
                try
                {
                    MoveTracked(action.SourcePath, dest, manifest);
                }
                catch
                {
                    // The new file couldn't come in (locked, the disk full): the one it was to replace
                    // goes back, so the target never ends up without the song.
                    PutBack(manifest, start);
                    throw;
                }
                MoveSidecar(action, targetFolder, manifest);
                break;

            case MergeActionType.Quarantine:
                MoveTracked(action.SourcePath, Quarantined(quarantineFolder, action.SourcePath), manifest);
                if (action.SourceLyricsPath is not null)
                    MoveTracked(action.SourceLyricsPath, Quarantined(quarantineFolder, action.SourceLyricsPath), manifest);
                break;
        }
    }

    /// <summary>Undoes the moves recorded from <paramref name="start"/> on, newest first.</summary>
    private static void PutBack(List<(string From, string To)> manifest, int start)
    {
        for (var i = manifest.Count - 1; i >= start; i--)
        {
            var (from, to) = manifest[i];
            try
            {
                File.Move(to, from);
                manifest.RemoveAt(i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // left in the manifest: 되돌리기 can still try it
            }
        }
    }

    /// <summary>Where a losing file goes in quarantine - numbered if an earlier run left one of
    /// the same name there.</summary>
    private static string Quarantined(string quarantineFolder, string path)
    {
        var dest = Path.Combine(quarantineFolder, Path.GetFileName(path));
        if (!File.Exists(dest)) return dest;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var candidate = Path.Combine(quarantineFolder, $"{stem} ({n}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static void MoveSidecar(MergeAction action, string targetFolder, List<(string, string)> manifest)
    {
        if (action.SourceLyricsPath is null) return;
        var destLrc = Path.Combine(targetFolder, Path.GetFileNameWithoutExtension(action.DestFileName) + ".lrc");
        MoveTracked(action.SourceLyricsPath, destLrc, manifest);
    }

    private static void MoveTracked(string sourcePath, string destPath, List<(string From, string To)> manifest)
    {
        if (!File.Exists(sourcePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        File.Move(sourcePath, destPath, overwrite: false);
        manifest.Add((sourcePath, destPath));
    }

    private static string MakeUniqueName(string fileName, string existingPath)
    {
        var dir = Path.GetDirectoryName(existingPath)!;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            if (!File.Exists(Path.Combine(dir, candidate))) return candidate;
        }
    }

    private static Dictionary<string, string> BuildTargetIndex(string targetFolder, CancellationToken ct)
    {
        var index = new Dictionary<string, string>();
        if (!Directory.Exists(targetFolder)) return index;

        foreach (var path in Directory.EnumerateFiles(targetFolder, "*", Listing(false)).Where(TagService.IsSupported))
        {
            // Reading every song's tags takes a while on a big folder: a changed choice in the
            // window cancels it rather than letting it read to the end.
            ct.ThrowIfCancellationRequested();
            var key = ResolveKey(path);
            if (key is not null) index.TryAdd(key, path);
        }
        return index;
    }

    private static string? ResolveKey(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);

        try
        {
            var tags = TagService.Read(path);
            if (!string.IsNullOrWhiteSpace(tags.Artist) && !string.IsNullOrWhiteSpace(tags.Title))
                return NormalizeKey(tags.Artist, tags.Title);
        }
        catch
        {
            // unreadable tags - fall through to filename parsing
        }

        var parsed = FilenameParser.Parse(stem);
        return parsed is null ? null : NormalizeKey(parsed.Value.Artist, parsed.Value.Title);
    }

    private static string NormalizeKey(string artist, string title)
    {
        var combined = (artist + "|" + title).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(combined.Length);
        const string ignoredPunctuation = "-_,.()[]!?&:;~'\"’‘“”·";
        foreach (var ch in combined)
            if (!char.IsWhiteSpace(ch) && ignoredPunctuation.IndexOf(ch) < 0)
                sb.Append(ch);
        return sb.ToString();
    }
}
