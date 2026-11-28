using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>What the one-song editor's 파일 이름 card shows.</summary>
public enum FileRenameState
{
    /// <summary>No card (several songs, or not worked out yet).</summary>
    Hidden,
    /// <summary>A song on a WebDAV/FTP/SFTP source: its name can't be changed here.</summary>
    Remote,
    /// <summary>Already named by the rule.</summary>
    Same,
    /// <summary>The rule gives it another name.</summary>
    Change,
    /// <summary>The tags the rule uses are all empty: no name to give.</summary>
    NoName,
}

/// <summary>파일 이름 바꾸기 in the one-song editor: the name 이름 변경's rule (its saved patterns
/// and mode, its formatter) gives the song, worked out live from the tags as edited - unsaved
/// edits included, since the point is usually "these tags, now this name". When those edits
/// change the name, the button saves them first, so the file never ends up named after tags it
/// doesn't hold. Takes the .lrc along, never overwrites (" (2)"), and can be undone.</summary>
public sealed partial class TagEditorViewModel
{
    private readonly AppSettings _settings;

    /// <summary>Set by the one-song editor: only it shows the card, so only it works the name out.</summary>
    public bool OffersRename { get; set; }

    /// <summary>The song's file name as it is now.</summary>
    [ObservableProperty]
    private string _fileName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename), nameof(RenameButtonToolTip))]
    private FileRenameState _renameState;

    /// <summary>The name the rule gives (with the extension).</summary>
    [ObservableProperty]
    private string _renameNewName = "";

    /// <summary>What to know about that name: a tag it lacks, a number added, characters replaced.</summary>
    [ObservableProperty]
    private string _renameNote = "";

    /// <summary>Which rule it is, in words: "앨범 곡 규칙 · [트랙] [제목]".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameButtonToolTip))]
    private string _renameRuleText = "";

    /// <summary>The new name comes from unsaved tag edits: renaming saves them first.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenameButtonToolTip))]
    private bool _renameNeedsSave;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRename))]
    private bool _isRenaming;

    /// <summary>The song was just renamed here: its old name is one click away.</summary>
    [ObservableProperty]
    private bool _canUndoRename;

    /// <summary>What 이름 바꾸기 will do - or, greyed out, why it can't.</summary>
    public string RenameButtonToolTip => RenameState switch
    {
        FileRenameState.Change => "파일 이름을 이름 변경 규칙에 맞게 바꿉니다. 같은 이름의 가사 파일(.lrc)도 함께 바뀝니다."
            + (RenameNeedsSave ? "\n저장하지 않은 태그 변경을 먼저 저장합니다." : "")
            + $"\n{RenameRuleText}",
        FileRenameState.Same => $"이미 이름 변경 규칙에 맞는 이름입니다.\n{RenameRuleText}",
        FileRenameState.NoName => $"규칙에 쓰는 태그가 비어 있어 새 이름을 만들 수 없습니다.\n{RenameRuleText}",
        FileRenameState.Remote => "네트워크 위치(WebDAV·FTP·SFTP)의 곡은 파일 이름을 바꿀 수 없습니다.",
        _ => "",
    };

    public bool CanRename => RenameState == FileRenameState.Change && !IsRenaming;

    private int _fileNameVersion;
    private RenamePreviewItem? _renamePlan;
    private (LibraryFileViewModel File, List<(string From, string To)> Moves)? _renameUndo;

    private static readonly (string Token, string Label)[] RuleTokens =
    [
        ("{AlbumArtist}", "앨범 아티스트"), ("{Artist}", "아티스트"), ("{Title}", "제목"), ("{Album}", "앨범"),
        ("{Track}", "트랙"), ("{Disc}", "디스크"), ("{Year}", "연도"),
    ];

    private static string DescribeRule(string pattern, bool album)
    {
        var text = pattern;
        foreach (var (token, label) in RuleTokens) text = text.Replace(token, $"[{label}]");
        return $"{(album ? "앨범 곡 규칙" : "한 곡 규칙")} · {text}";
    }

    private static TrackTags EffectiveTags(LibraryFileViewModel file) =>
        file.PendingTags.Count > 0 ? TagFields.Apply(file.Model.Tags, file.PendingTags) : file.Model.Tags;

    /// <summary>Works the card out again: after a selection change, an edit to the song, a save, a rename.</summary>
    private async void RefreshFileName()
    {
        if (!OffersRename) return;
        var version = ++_fileNameVersion;
        _renamePlan = null;
        if (_selection.Count != 1)
        {
            FileName = "";
            RenameState = FileRenameState.Hidden;
            CanUndoRename = false;
            return;
        }

        var file = _selection[0];
        FileName = file.FileName;
        CanUndoRename = _renameUndo is { } undo && undo.File == file;
        if (file.IsRemote)
        {
            RenameState = FileRenameState.Remote;
            RenameNote = "네트워크 위치(WebDAV·FTP·SFTP)의 곡은 파일 이름을 바꿀 수 없습니다.";
            return;
        }

        var tags = EffectiveTags(file);
        var rules = RenameService.RulesFrom(_settings);
        var mode = _settings.RenameMode;
        var album = RenameService.UsesAlbumPattern(tags, mode);
        RenameRuleText = DescribeRule(album ? rules.AlbumTrackPattern : rules.SingleTrackPattern, album);
        // Only edits that change the name need saving first (a new cover alone doesn't).
        var saved = file.Model.Tags;
        var savedPattern = RenameService.UsesAlbumPattern(saved, mode) ? rules.AlbumTrackPattern : rules.SingleTrackPattern;
        var needsSave = file.PendingTags.Count > 0
            && NamePatternFormatter.Format(album ? rules.AlbumTrackPattern : rules.SingleTrackPattern, tags, rules)
               != NamePatternFormatter.Format(savedPattern, saved, rules);

        RenamePreviewItem item;
        try
        {
            // (asks the disk which names are taken: off the UI thread, a NAS may be slow to answer)
            var path = file.Path;
            item = await Task.Run(() => RenameService.PlanOne(path, tags, rules, mode));
        }
        catch (Exception)
        {
            if (version == _fileNameVersion) RenameState = FileRenameState.Hidden;
            return;
        }
        if (version != _fileNameVersion) return;

        _renamePlan = item;
        RenameNeedsSave = needsSave;
        RenameNewName = item.NewFileName;
        RenameNote = item.Note;
        RenameState = item.Kind switch
        {
            RenameKind.Same => FileRenameState.Same,
            RenameKind.MissingTags when !item.WillChange => FileRenameState.NoName,
            _ => FileRenameState.Change,
        };
    }

    [RelayCommand]
    private async Task RenameFileAsync()
    {
        if (!CanRename || IsSaving || _selection.Count != 1 || _renamePlan is not { } plan) return;
        var file = _selection[0];
        if (file.IsRemote) return;
        var saveFirst = RenameNeedsSave;
        if (saveFirst && AllFields.FirstOrDefault(f => f.HasError) is { } invalid)
        {
            Tab = EditorTab.Tags;
            Toast.Warning("입력값을 확인하세요", $"{invalid.Label}: {invalid.Error}");
            return;
        }
        // An incomplete name is worth a second look; any other is what the card shows.
        if (plan.Kind == RenameKind.MissingTags
            && !await Dialogs.ConfirmAsync("파일 이름 바꾸기",
                $"{plan.Note}\n\n그래도 이 이름으로 바꿀까요?\n{plan.NewFileName}", "이름 바꾸기"))
            return;

        IsRenaming = true;
        var rules = RenameService.RulesFrom(_settings);
        var mode = _settings.RenameMode;
        // The playing song is let go once for both the save and the rename, then reopened under
        // its new name (or the old one if the rename failed).
        var resume = await _playback.ReleaseIfPlayingAsync([file.Path]);
        string? newPath = null;
        try
        {
            if (saveFirst)
            {
                await CommitAsync([file], releasePlayback: false);
                if (file.PendingTags.Count > 0) return; // not saved: CommitAsync said why
            }

            // From the file as it is now: its saved tags, and the names taken on disk by now.
            var path = file.Path;
            var tags = file.Model.Tags;
            var item = await Task.Run(() => RenameService.PlanOne(path, tags, rules, mode));
            if (!item.WillChange || string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(item.NewPath))) return;

            var moves = new List<(string From, string To)>();
            var errors = await Task.Run(() => RenameService.Execute([item], moves));
            if (moves.Count > 0)
            {
                _library.ApplyRenames(moves);
                _renameUndo = (file, moves);
                newPath = moves[0].To;
            }
            if (errors.Count > 0)
            {
                Toast.Warning("파일 이름을 바꾸지 못했습니다", errors[0]);
                return;
            }
            var lyricsToo = moves.Count > 1 ? $"{Environment.NewLine}가사 파일(.lrc)도 함께 바꿨습니다." : "";
            Toast.Success("파일 이름을 바꿨습니다", Path.GetFileName(newPath) + lyricsToo);
        }
        catch (Exception ex)
        {
            Toast.Warning("파일 이름을 바꾸지 못했습니다", ex.Message);
        }
        finally
        {
            resume?.Invoke(newPath);
            IsRenaming = false;
            _ = LoadDetailsAsync(_selectionVersion);
            RefreshFileName();
        }
    }

    [RelayCommand]
    private async Task UndoRenameAsync()
    {
        if (_renameUndo is not { } undo || IsRenaming || IsSaving) return;
        IsRenaming = true;
        var resume = await _playback.ReleaseIfPlayingAsync(undo.Moves.Select(m => m.To));
        string? back = null;
        try
        {
            var (errors, done) = await Task.Run(() =>
            {
                var errors = RenameService.Undo(undo.Moves);
                // Which went back: all of them, or (some failed) those no longer under the new name.
                var reversed = undo.Moves.Select(m => (From: m.To, To: m.From))
                    .Where(m => errors.Count == 0 || !File.Exists(m.From) && File.Exists(m.To)).ToList();
                return (errors, reversed);
            });
            _library.ApplyRenames(done);
            back = done.Where(m => TagService.IsSupported(m.To)).Select(m => m.To).FirstOrDefault();
            _renameUndo = null;
            if (errors.Count > 0)
                Toast.Warning("원래 이름으로 되돌리지 못했습니다", errors[0]);
            else
                Toast.Info("원래 이름으로 되돌렸습니다", Path.GetFileName(back) ?? "");
        }
        catch (Exception ex)
        {
            Toast.Warning("원래 이름으로 되돌리지 못했습니다", ex.Message);
        }
        finally
        {
            resume?.Invoke(back);
            IsRenaming = false;
            _ = LoadDetailsAsync(_selectionVersion);
            RefreshFileName();
        }
    }
}
