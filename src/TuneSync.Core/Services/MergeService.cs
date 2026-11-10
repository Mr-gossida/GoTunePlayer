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
    public static List<MergeAction> BuildPlan(string sourceFolder, string targetFolder, MergeRuleSet rules)
    {
        var targetIndex = BuildTargetIndex(targetFolder);
        var plan = new List<MergeAction>();

        var sourceOption = rules.IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var sourceFiles = Directory.EnumerateFiles(sourceFolder, "*.*", sourceOption)
            .Where(TagService.IsSupported);

        foreach (var sourcePath in sourceFiles)
        {
            var key = ResolveKey(sourcePath);
            var stem = Path.GetFileNameWithoutExtension(sourcePath);
            var lrcPath = Path.Combine(Path.GetDirectoryName(sourcePath)!, stem + ".lrc");
            var lyricsPath = File.Exists(lrcPath) ? lrcPath : null;
            var sourceSize = new FileInfo(sourcePath).Length;
            var destFileName = Path.GetFileName(sourcePath);

            if (key is null || !targetIndex.TryGetValue(key, out var conflictPath))
            {
                plan.Add(new MergeAction
                {
                    SourcePath = sourcePath,
                    SourceLyricsPath = lyricsPath,
                    DestFileName = destFileName,
                    ActionType = MergeActionType.Move,
                    Reason = key is null ? "태그/파일명에서 곡 정보를 읽지 못함 - 그대로 이동" : "대상에 동일한 곡 없음",
                    SourceSize = sourceSize,
                });
                continue;
            }

            var conflictSize = new FileInfo(conflictPath).Length;
            plan.Add(BuildConflictAction(rules, sourcePath, lyricsPath, destFileName, conflictPath, sourceSize, conflictSize));
        }

        return plan;
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

    /// <summary>Applies a plan built by <see cref="BuildPlan"/>. Every file on the losing side of
    /// a conflict is moved into <paramref name="quarantineFolder"/>, never deleted. Returns a
    /// manifest of every move actually made, so a run can be reviewed or manually reversed.</summary>
    public static List<(string From, string To)> ExecutePlan(
        List<MergeAction> plan, string targetFolder, string quarantineFolder)
    {
        Directory.CreateDirectory(quarantineFolder);
        var manifest = new List<(string From, string To)>();

        foreach (var action in plan)
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
                    MoveTracked(action.ConflictWithPath!, Path.Combine(quarantineFolder, Path.GetFileName(action.ConflictWithPath!)), manifest);
                    if (File.Exists(conflictLrc))
                        MoveTracked(conflictLrc, Path.Combine(quarantineFolder, Path.GetFileName(conflictLrc)), manifest);
                    MoveTracked(action.SourcePath, Path.Combine(targetFolder, action.DestFileName), manifest);
                    MoveSidecar(action, targetFolder, manifest);
                    break;

                case MergeActionType.Quarantine:
                    MoveTracked(action.SourcePath, Path.Combine(quarantineFolder, Path.GetFileName(action.SourcePath)), manifest);
                    if (action.SourceLyricsPath is not null)
                        MoveTracked(action.SourceLyricsPath, Path.Combine(quarantineFolder, Path.GetFileName(action.SourceLyricsPath)), manifest);
                    break;
            }
        }

        return manifest;
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
