using System.Text;
using System.Text.RegularExpressions;
using TuneSync.Core.Models;

namespace TuneSync.Core.Services;

/// <summary>Builds a dry-run plan for folding one folder's tracks into another, flags duplicates
/// by normalized artist+title, and only ever *moves* the losing side into a quarantine folder —
/// never deletes — so a bad call is one drag-and-drop away from undone.</summary>
public static partial class MergeService
{
    [GeneratedRegex(@"^\[(?<artist>.+?)\]\s*(?<title>.+)$")]
    private static partial Regex BracketPattern();

    public static (string Artist, string Title)? ParseBracketName(string fileNameWithoutExt)
    {
        var m = BracketPattern().Match(fileNameWithoutExt);
        if (!m.Success) return null;
        return (m.Groups["artist"].Value.Trim(), m.Groups["title"].Value.Trim());
    }

    public static string NormalizeKey(string artist, string title)
    {
        var combined = (artist + "|" + title).Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        var sb = new StringBuilder(combined.Length);
        foreach (var ch in combined)
            if (!char.IsWhiteSpace(ch) && "-_,.()[]!?&:;~'\"’‘“”·".IndexOf(ch) < 0)
                sb.Append(ch);
        return sb.ToString();
    }

    private static (string Artist, string Title)? KeyOf(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var parsed = ParseBracketName(stem);
        if (parsed is not null) return parsed;
        if (!TagService.IsSupported(path)) return null;
        try
        {
            var tags = TagService.Read(path);
            return string.IsNullOrWhiteSpace(tags.Artist) || string.IsNullOrWhiteSpace(tags.Title)
                ? null
                : (tags.Artist, tags.Title);
        }
        catch
        {
            return null;
        }
    }

    public static List<MergePlanItem> BuildPlan(string sourceFolder, string destFolder, RenameRuleSet rules)
    {
        var destIndex = new Dictionary<string, string>(); // normkey -> dest file name (audio file, not lyrics)
        foreach (var destPath in Directory.EnumerateFiles(destFolder))
        {
            if (!TagService.IsSupported(destPath)) continue;
            var key = KeyOf(destPath);
            if (key is null) continue;
            var norm = NormalizeKey(key.Value.Artist, key.Value.Title);
            destIndex.TryAdd(norm, Path.GetFileName(destPath));
        }

        var plan = new List<MergePlanItem>();
        var audioBySourceStem = Directory.EnumerateFiles(sourceFolder)
            .Where(TagService.IsSupported)
            .ToList();

        foreach (var srcPath in audioBySourceStem)
        {
            var key = KeyOf(srcPath);
            if (key is null) continue;
            var (artist, title) = key.Value;

            var tags = SafeRead(srcPath) ?? new TrackTags { Path = srcPath, Artist = artist, Title = title };
            var destFileName = RenameService.BuildFileName(tags, rules, isAlbumTrack: false) + Path.GetExtension(srcPath);

            var stem = Path.GetFileNameWithoutExtension(srcPath);
            var lrcPath = Path.Combine(sourceFolder, stem + ".lrc");
            var lrcExists = System.IO.File.Exists(lrcPath);

            var norm = NormalizeKey(artist, title);
            if (destIndex.TryGetValue(norm, out var conflictName))
            {
                var conflictPath = Path.Combine(destFolder, conflictName);
                var srcSize = new FileInfo(srcPath).Length;
                var destSize = new FileInfo(conflictPath).Length;
                var action = srcSize > destSize ? MergeAction.ReplaceExisting : MergeAction.KeepExisting;
                plan.Add(new MergePlanItem
                {
                    SourcePath = srcPath,
                    SourceLrcPath = lrcExists ? lrcPath : null,
                    DestFileName = destFileName,
                    ConflictWithPath = conflictPath,
                    Action = action,
                    SourceSize = srcSize,
                    ConflictSize = destSize,
                });
            }
            else
            {
                plan.Add(new MergePlanItem
                {
                    SourcePath = srcPath,
                    SourceLrcPath = lrcExists ? lrcPath : null,
                    DestFileName = destFileName,
                    Action = MergeAction.Copy,
                    SourceSize = new FileInfo(srcPath).Length,
                });
            }
        }

        return plan;
    }

    private static TrackTags? SafeRead(string path)
    {
        try { return TagService.Read(path); } catch { return null; }
    }

    /// <summary>Applies a plan built by <see cref="BuildPlan"/>. Every file that loses a duplicate
    /// comparison is moved into <paramref name="quarantineFolder"/>, not deleted.</summary>
    public static void Execute(IEnumerable<MergePlanItem> plan, string destFolder, string quarantineFolder)
    {
        Directory.CreateDirectory(quarantineFolder);

        foreach (var item in plan)
        {
            switch (item.Action)
            {
                case MergeAction.Copy:
                    MoveInto(item.SourcePath, Path.Combine(destFolder, item.DestFileName));
                    if (item.SourceLrcPath is not null)
                        MoveInto(item.SourceLrcPath, Path.Combine(destFolder, Path.GetFileNameWithoutExtension(item.DestFileName) + ".lrc"));
                    break;

                case MergeAction.ReplaceExisting:
                    var conflictLrc = Path.ChangeExtension(item.ConflictWithPath!, ".lrc");
                    MoveInto(item.ConflictWithPath!, Path.Combine(quarantineFolder, Path.GetFileName(item.ConflictWithPath!)));
                    if (System.IO.File.Exists(conflictLrc))
                        MoveInto(conflictLrc, Path.Combine(quarantineFolder, Path.GetFileName(conflictLrc)));
                    MoveInto(item.SourcePath, Path.Combine(destFolder, item.DestFileName));
                    if (item.SourceLrcPath is not null)
                        MoveInto(item.SourceLrcPath, Path.Combine(destFolder, Path.GetFileNameWithoutExtension(item.DestFileName) + ".lrc"));
                    break;

                case MergeAction.KeepExisting:
                    MoveInto(item.SourcePath, Path.Combine(quarantineFolder, Path.GetFileName(item.SourcePath)));
                    if (item.SourceLrcPath is not null)
                        MoveInto(item.SourceLrcPath, Path.Combine(quarantineFolder, Path.GetFileName(item.SourceLrcPath)));
                    break;
            }
        }
    }

    private static void MoveInto(string sourcePath, string destPath)
    {
        if (!System.IO.File.Exists(sourcePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        System.IO.File.Move(sourcePath, destPath, overwrite: false);
    }
}
