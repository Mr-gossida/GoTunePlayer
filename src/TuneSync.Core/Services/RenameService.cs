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

    /// <summary>Renames every item whose name actually changes. A destination that collides with
    /// an existing file (other than the source itself) gets a " (2)", " (3)", ... suffix instead
    /// of overwriting it. Returns one error message per file that failed.</summary>
    public static List<string> Execute(IEnumerable<RenamePreviewItem> items)
    {
        var errors = new List<string>();

        foreach (var item in items)
        {
            if (!item.WillChange)
            {
                item.Status = "변경 없음";
                continue;
            }

            try
            {
                var destPath = ResolveCollision(item.NewPath, item.OldPath);
                File.Move(item.OldPath, destPath);

                var oldLrc = Path.ChangeExtension(item.OldPath, ".lrc");
                if (File.Exists(oldLrc))
                {
                    var destLrc = Path.ChangeExtension(destPath, ".lrc");
                    File.Move(oldLrc, ResolveCollision(destLrc, oldLrc));
                }

                item.Status = "변경 완료";
            }
            catch (Exception ex)
            {
                errors.Add($"{item.OldFileName}: {ex.Message}");
                item.Status = $"실패: {ex.Message}";
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
