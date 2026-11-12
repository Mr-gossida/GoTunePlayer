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
        var targetIndex = BuildTargetIndex(targetFolder);
        var plan = new List<MergeAction>();
        // File names the target root will hold: what's there now plus what this plan moves in, so
        // two songs that happen to share a file name never collide (everything lands in the root).
        var claimed = new HashSet<string>(
            Directory.Exists(targetFolder) ? Directory.EnumerateFiles(targetFolder).Select(Path.GetFileName)! : [],
            StringComparer.OrdinalIgnoreCase);

        var sourceOption = rules.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var sourceFiles = Directory.EnumerateFiles(sourceFolder, "*.*", sourceOption)
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
        }

        return plan;
    }

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
        var destName = destFileName ?? (type == MergeActionType.RenameAndMove
            ? MakeUniqueName(Path.GetFileName(action.SourcePath), action.ConflictWithPath)
            : Path.GetFileName(action.SourcePath));
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
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
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
        {
            switch (action.ActionType)
            {
                case MergeActionType.Move:
                case MergeActionType.RenameAndMove:
                    MoveTracked(action.SourcePath, Path.Combine(targetFolder, action.DestFileName), manifest);
                    MoveSidecar(action, targetFolder, manifest);
                    break;

                case MergeActionType.Replace:
                    var conflictLrc = Path.ChangeExtension(action.ConflictWithPath!, ".lrc");
                    MoveTracked(action.ConflictWithPath!, Quarantined(quarantineFolder, action.ConflictWithPath!), manifest);
                    if (File.Exists(conflictLrc))
                        MoveTracked(conflictLrc, Quarantined(quarantineFolder, conflictLrc), manifest);
                    MoveTracked(action.SourcePath, Path.Combine(targetFolder, action.DestFileName), manifest);
                    MoveSidecar(action, targetFolder, manifest);
                    break;

                case MergeActionType.Quarantine:
                    MoveTracked(action.SourcePath, Quarantined(quarantineFolder, action.SourcePath), manifest);
                    if (action.SourceLyricsPath is not null)
                        MoveTracked(action.SourceLyricsPath, Quarantined(quarantineFolder, action.SourceLyricsPath), manifest);
                    break;
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

    private static Dictionary<string, string> BuildTargetIndex(string targetFolder)
    {
        var index = new Dictionary<string, string>();
        if (!Directory.Exists(targetFolder)) return index;

        foreach (var path in Directory.EnumerateFiles(targetFolder).Where(TagService.IsSupported))
        {
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
