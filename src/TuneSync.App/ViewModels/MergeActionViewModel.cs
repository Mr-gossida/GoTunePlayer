using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>One planned merge row. A conflict row's choice (keep the incoming file, keep the one
/// already there, keep both) can be changed here, replacing the rule's pick.</summary>
public sealed partial class MergeActionViewModel : ObservableObject
{
    private readonly string _sourceRoot;
    private readonly Action _onChanged;

    public MergeActionViewModel(MergeAction action, string sourceRoot, Action onChanged)
    {
        _action = action;
        _sourceRoot = sourceRoot;
        _onChanged = onChanged;
        RuleChoice = action.ActionType;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionType), nameof(DestFileName), nameof(Reason), nameof(ActionLabel), nameof(DestLabel),
        nameof(IsOverridden), nameof(SourceWins), nameof(TargetWins))]
    private MergeAction _action;

    /// <summary>What the rule picked, to show whether the user changed it.</summary>
    public MergeActionType RuleChoice { get; }

    public string SourceFileName => Path.GetFileName(Action.SourcePath);
    public string SourceLocation
    {
        get
        {
            var dir = Path.GetDirectoryName(Action.SourcePath) ?? "";
            var rel = Path.GetRelativePath(_sourceRoot, dir);
            return rel == "." ? "" : rel;
        }
    }
    public bool HasSourceLocation => SourceLocation.Length > 0;
    public string DestFileName => Action.DestFileName;
    public string Reason => Action.Reason;
    public bool IsConflict => Action.ConflictWithPath is not null;
    public string ConflictFileName => Action.ConflictWithPath is { } p ? Path.GetFileName(p) : "";
    public bool HasLyricsSidecar => Action.SourceLyricsPath is not null;
    public bool IsOverridden => Action.ActionType != RuleChoice;

    /// <summary>Settable from the row's choice buttons (conflict rows only).</summary>
    public MergeActionType ActionType
    {
        get => Action.ActionType;
        set
        {
            if (!IsConflict || value == Action.ActionType) return;
            Action = MergeService.Override(Action, value);
            _onChanged();
        }
    }

    public string ActionLabel => Action.ActionType switch
    {
        MergeActionType.Move => "새로 이동",
        MergeActionType.Replace => "교체",
        MergeActionType.Quarantine => "건너뜀",
        MergeActionType.RenameAndMove => "이름 바꿔 이동",
        _ => Action.ActionType.ToString(),
    };

    /// <summary>Where the incoming file ends up.</summary>
    public string DestLabel => Action.ActionType switch
    {
        MergeActionType.Quarantine => "격리 폴더로 (받을 폴더 파일 유지)",
        MergeActionType.Replace => $"{Action.DestFileName}  ·  기존 파일은 격리",
        _ => Action.DestFileName,
    };

    public bool SourceWins => Action.ActionType is MergeActionType.Replace or MergeActionType.RenameAndMove or MergeActionType.Move;
    public bool TargetWins => Action.ActionType is MergeActionType.Quarantine or MergeActionType.RenameAndMove;

    // ---- side-by-side comparison, loaded when the row is opened ----

    [ObservableProperty]
    private MergeFileFacts? _sourceFacts;

    [ObservableProperty]
    private MergeFileFacts? _targetFacts;

    private bool _factsRequested;

    public async Task LoadFactsAsync()
    {
        if (_factsRequested) return;
        _factsRequested = true;
        var source = Action.SourcePath;
        var target = Action.ConflictWithPath;
        var (s, t) = await Task.Run(() =>
        {
            var a = MergeFileFacts.Read(source);
            var b = target is null ? null : MergeFileFacts.Read(target);
            if (a is not null && b is not null) MergeFileFacts.MarkBetter(a, b);
            return (a, b);
        });
        SourceFacts = s;
        TargetFacts = t;
    }
}

/// <summary>What one side of a conflict is like: quality, size, length, cover, lyrics.</summary>
public sealed class MergeFileFacts
{
    public required string FileName { get; init; }
    public required string Folder { get; init; }
    public string Quality { get; init; } = "-";
    public string Size { get; init; } = "-";
    public string Duration { get; init; } = "-";
    public string Modified { get; init; } = "-";
    public bool HasCover { get; init; }
    public bool HasLyrics { get; init; }
    public ImageSource? Cover { get; init; }
    public string Tags { get; init; } = "";

    public bool BetterQuality { get; set; }
    public bool Larger { get; set; }
    public bool Newer { get; set; }

    internal AudioQuality? RawQuality { get; init; }
    internal long Bytes { get; init; }
    internal DateTime LastWrite { get; init; }

    public static MergeFileFacts? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var (tags, cover, lyrics, duration) = TagService.ReadForLibrary(path);
            AudioQuality? q = null;
            try { q = TagService.ReadAudioQuality(path); } catch { /* unknown quality */ }
            var hasLrc = File.Exists(Path.ChangeExtension(path, ".lrc"));
            return new MergeFileFacts
            {
                FileName = info.Name,
                Folder = info.DirectoryName ?? "",
                Quality = q is null ? "-" : q.IsLossless
                    ? $"{q.Format} · {(q.BitsPerSample > 0 ? $"{q.BitsPerSample}bit / " : "")}{q.SampleRate / 1000.0:0.#}kHz"
                    : $"{q.Format} · {q.BitrateKbps}kbps",
                Size = FormatSize(info.Length),
                Duration = duration > TimeSpan.Zero ? duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss") : "-",
                Modified = info.LastWriteTime.ToString("yyyy-MM-dd"),
                HasCover = cover is { Length: > 0 },
                HasLyrics = lyrics is not null || hasLrc,
                Cover = cover is { Length: > 0 } ? Thumb(cover) : null,
                Tags = string.Join(" · ", new[] { tags.Artist, tags.Title, tags.Album }.Where(s => !string.IsNullOrWhiteSpace(s))),
                RawQuality = q,
                Bytes = info.Length,
                LastWrite = info.LastWriteTimeUtc,
            };
        }
        catch
        {
            return null;
        }
    }

    public static void MarkBetter(MergeFileFacts a, MergeFileFacts b)
    {
        var c = MergeService.CompareQuality(a.Folder + "\\" + a.FileName, b.Folder + "\\" + b.FileName);
        a.BetterQuality = c > 0;
        b.BetterQuality = c < 0;
        a.Larger = a.Bytes > b.Bytes;
        b.Larger = b.Bytes > a.Bytes;
        a.Newer = a.LastWrite > b.LastWrite;
        b.Newer = b.LastWrite > a.LastWrite;
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    private static ImageSource? Thumb(byte[] bytes)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 160;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}
