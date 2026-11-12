using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Where the songs to rename come from.</summary>
public enum RenameSource
{
    /// <summary>The songs selected in 태그 관리.</summary>
    Selection,
    /// <summary>Every song in a folder (optionally with its subfolders).</summary>
    Folder,
}

/// <summary>Which rows the preview shows.</summary>
public enum RenameFilter
{
    All,
    Changed,
    Same,
    Check,
}

/// <summary>A ready-made pattern, one click away.</summary>
public sealed record RenamePreset(string Label, string Pattern, bool IsAlbum);

/// <summary>A token chip: inserts <see cref="Token"/> into the pattern being edited.</summary>
public sealed record RenameToken(string Label, string Token);

/// <summary>이름 변경: picks songs (the tag manager's selection or a folder), names them from
/// their tags with a pattern built from chips and presets, and previews every name live as the
/// pattern is typed - what changes, what stays, what needs a look - with a tick box per row.
/// Renames take the .lrc along, never overwrite (" (2)"...), and can be undone.</summary>
public sealed partial class RenameViewModel : ObservableObject
{
    public ObservableCollection<RenamePreviewItemViewModel> Preview { get; } = [];
    public ICollectionView PreviewView { get; }

    public IReadOnlyList<RenameToken> Tokens { get; } =
    [
        new("아티스트", "{Artist}"),
        new("제목", "{Title}"),
        new("앨범", "{Album}"),
        new("앨범 아티스트", "{AlbumArtist}"),
        new("트랙", "{Track}"),
        new("디스크", "{Disc}"),
        new("연도", "{Year}"),
    ];

    public IReadOnlyList<RenamePreset> SinglePresets { get; } =
    [
        new("아티스트 - 제목", "{Artist} - {Title}", false),
        new("[아티스트] 제목", "[{Artist}] {Title}", false),
        new("제목 - 아티스트", "{Title} - {Artist}", false),
        new("제목", "{Title}", false),
    ];

    public IReadOnlyList<RenamePreset> AlbumPresets { get; } =
    [
        new("01 제목", "{Track} {Title}", true),
        new("01. 제목", "{Track}. {Title}", true),
        new("01 아티스트 - 제목", "{Track} {Artist} - {Title}", true),
        new("01 - 제목", "{Track} - {Title}", true),
    ];

    [ObservableProperty]
    private RenameSource _source = RenameSource.Selection;

    [ObservableProperty]
    private string? _folderPath;

    [ObservableProperty]
    private bool _includeSubfolders;

    [ObservableProperty]
    private RenameMode _mode;

    [ObservableProperty]
    private string _singleTrackPattern = "{Artist} - {Title}";

    [ObservableProperty]
    private string _albumTrackPattern = "{Track} {Title}";

    [ObservableProperty]
    private RenameFilter _filter = RenameFilter.All;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string _sourceSummary = "";

    [ObservableProperty]
    private int _totalCount;

    [ObservableProperty]
    private int _changedCount;

    [ObservableProperty]
    private int _sameCount;

    [ObservableProperty]
    private int _checkCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    [NotifyPropertyChangedFor(nameof(ExecuteLabel))]
    private int _includedCount;

    public string ExecuteLabel => IncludedCount > 0 ? $"{IncludedCount}곡 이름 바꾸기" : "이름 바꾸기";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    private string _undoSummary = "";

    /// <summary>Selection songs left out: remote ones, and ones with unsaved tag edits.</summary>
    [ObservableProperty]
    private string _excludedNote = "";

    public bool UsesSingle => Mode != RenameMode.Album;
    public bool UsesAlbum => Mode != RenameMode.Single;
    public bool HasRows => Preview.Count > 0;
    public bool IsEmptyState => !IsLoading && Preview.Count == 0;

    private readonly AppSettings _settings;
    private readonly LibraryViewModel _library;
    private readonly PlaybackViewModel _playback;
    private readonly TagEditorViewModel _editor;
    private readonly DispatcherTimer _replanTimer;
    private List<string> _targetPaths = [];
    private List<(string Path, TrackTags Tags)> _targets = [];
    private List<RenamePreviewItem> _plan = [];
    private List<(string From, string To)> _lastMoves = [];
    private string? _lastFolder;
    private int _loadVersion;
    private int _planVersion;

    public RenameViewModel(AppSettings settings, LibraryViewModel library, PlaybackViewModel playback, TagEditorViewModel editor)
    {
        _settings = settings;
        _library = library;
        _playback = playback;
        _editor = editor;

        _folderPath = settings.LastRenameFolder;
        _includeSubfolders = settings.RenameIncludeSubfolders;
        _mode = settings.RenameMode;
        _singleTrackPattern = settings.RenameSingleTrackPattern;
        _albumTrackPattern = settings.RenameAlbumTrackPattern;

        PreviewView = CollectionViewSource.GetDefaultView(Preview);
        PreviewView.Filter = o => o is RenamePreviewItemViewModel row && Filter switch
        {
            RenameFilter.Changed => row.WillChange,
            RenameFilter.Same => row.Kind == RenameKind.Same,
            RenameFilter.Check => row.NeedsLook,
            _ => true,
        };

        // Typing a pattern re-plans after a short pause, not on every key.
        _replanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _replanTimer.Tick += (_, _) =>
        {
            _replanTimer.Stop();
            _ = ReplanAsync();
        };

        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TagEditorViewModel.Count) && editor.Count > 0 && Source == RenameSource.Selection && _isShown && !IsBusy)
                _ = LoadTargetsAsync();
        };
    }

    private bool _isShown;

    /// <summary>The window opened (again): starts from the tag manager's selection when there is
    /// one, otherwise from the last folder.</summary>
    public void Activate()
    {
        _isShown = true;
        if (IsBusy) return;
        var hasSelection = _editor.Selection.Count > 0;
        if (hasSelection) Source = RenameSource.Selection;
        else if (Source == RenameSource.Selection && !string.IsNullOrEmpty(FolderPath)) Source = RenameSource.Folder;
        _ = LoadTargetsAsync();
    }

    public void Deactivate() => _isShown = false;

    partial void OnSourceChanged(RenameSource value) => _ = LoadTargetsAsync();

    partial void OnFolderPathChanged(string? value)
    {
        _settings.LastRenameFolder = value;
        AppSettingsStore.Save(_settings);
        if (Source == RenameSource.Folder) _ = LoadTargetsAsync();
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        _settings.RenameIncludeSubfolders = value;
        AppSettingsStore.Save(_settings);
        if (Source == RenameSource.Folder) _ = LoadTargetsAsync();
    }

    partial void OnModeChanged(RenameMode value)
    {
        _settings.RenameMode = value;
        AppSettingsStore.Save(_settings);
        OnPropertyChanged(nameof(UsesSingle));
        OnPropertyChanged(nameof(UsesAlbum));
        ScheduleReplan();
    }

    partial void OnSingleTrackPatternChanged(string value) => ScheduleReplan();
    partial void OnAlbumTrackPatternChanged(string value) => ScheduleReplan();
    partial void OnFilterChanged(RenameFilter value) => PreviewView.Refresh();
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsEmptyState));

    private void ScheduleReplan()
    {
        _replanTimer.Stop();
        _replanTimer.Start();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var folder = FileSystemPicker.PickFolder("이름을 바꿀 폴더 선택 (폴더로 이동한 뒤 '선택'을 누르세요)", FolderPath);
        if (folder is null) return;
        FolderPath = folder;
        Source = RenameSource.Folder;
    }

    [RelayCommand]
    private void ApplyPreset(RenamePreset preset)
    {
        if (preset.IsAlbum) AlbumTrackPattern = preset.Pattern;
        else SingleTrackPattern = preset.Pattern;
    }

    /// <summary>Re-reads the songs (their tags), e.g. after the selection or the folder changed.</summary>
    [RelayCommand]
    private Task RefreshAsync() => LoadTargetsAsync();

    private async Task LoadTargetsAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        try
        {
            List<string> paths;
            ExcludedNote = "";
            if (Source == RenameSource.Selection)
            {
                var selection = _editor.Selection;
                var remote = selection.Count(f => f.IsRemote);
                var pending = selection.Count(f => !f.IsRemote && f.HasPendingChanges);
                paths = selection.Where(f => !f.IsRemote && !f.HasPendingChanges).Select(f => f.Path).ToList();
                var notes = new List<string>();
                if (pending > 0) notes.Add($"저장하지 않은 태그 변경이 있는 {pending}곡은 빠졌습니다 (먼저 저장하세요)");
                if (remote > 0) notes.Add($"네트워크 위치의 {remote}곡은 이름을 바꿀 수 없어 빠졌습니다");
                ExcludedNote = string.Join(" · ", notes);
                SourceSummary = selection.Count == 0 ? "태그 관리에서 선택한 곡이 없습니다" : $"태그 관리에서 선택한 {selection.Count}곡";
            }
            else
            {
                var folder = FolderPath;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    SourceSummary = string.IsNullOrWhiteSpace(folder) ? "폴더를 선택하세요" : "폴더를 찾을 수 없습니다";
                    paths = [];
                }
                else
                {
                    var option = IncludeSubfolders ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                    paths = await Task.Run(() => Directory.EnumerateFiles(folder, "*.*", option).Where(TagService.IsSupported).ToList());
                    SourceSummary = $"{folder} · {paths.Count}곡";
                }
            }
            if (version != _loadVersion) return;

            StatusText = paths.Count > 0 ? $"{paths.Count}곡의 태그를 읽는 중…" : "";
            var targets = await Task.Run(() => RenameService.ReadTargets(paths));
            if (version != _loadVersion) return;
            _targetPaths = paths;
            _targets = targets;
            await ReplanAsync();
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) StatusText = $"곡을 읽지 못했습니다: {ex.Message}";
        }
        finally
        {
            if (version == _loadVersion) IsLoading = false;
        }
    }

    private async Task ReplanAsync()
    {
        var version = ++_planVersion;
        var rules = new RenameRuleSet { SingleTrackPattern = SingleTrackPattern, AlbumTrackPattern = AlbumTrackPattern };
        var mode = Mode;
        var targets = _targets;

        if (_settings.RenameSingleTrackPattern != SingleTrackPattern || _settings.RenameAlbumTrackPattern != AlbumTrackPattern)
        {
            _settings.RenameSingleTrackPattern = SingleTrackPattern;
            _settings.RenameAlbumTrackPattern = AlbumTrackPattern;
            AppSettingsStore.Save(_settings);
        }

        List<RenamePreviewItem> plan;
        try
        {
            plan = await Task.Run(() => RenameService.Plan(targets, rules, mode));
        }
        catch (Exception ex)
        {
            StatusText = $"미리보기를 만들지 못했습니다: {ex.Message}";
            return;
        }
        if (version != _planVersion) return;

        _plan = plan;
        var folders = plan.Select(p => Path.GetDirectoryName(p.OldPath) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var root = Source == RenameSource.Folder ? FolderPath : null;
        string LocationOf(string path)
        {
            if (folders <= 1) return "";
            var dir = Path.GetDirectoryName(path) ?? "";
            if (root is not null)
            {
                var rel = Path.GetRelativePath(root, dir);
                return rel == "." ? "(이 폴더)" : rel;
            }
            return dir;
        }

        Preview.Clear();
        foreach (var item in plan)
            Preview.Add(new RenamePreviewItemViewModel(item, LocationOf(item.OldPath), UpdateCounts));
        UpdateCounts();
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmptyState));

        var activeOk = (!UsesSingle || SingleTrackPattern.Contains('{')) && (!UsesAlbum || AlbumTrackPattern.Contains('{'));
        StatusText = !activeOk ? "패턴에 아티스트·제목 같은 태그 칩을 하나 이상 넣어 주세요."
            : plan.Count == 0 ? ""
            : CheckCount > 0 ? $"확인이 필요한 곡이 {CheckCount}개 있습니다. '확인 필요'에서 살펴보세요."
            : "";
    }

    private void UpdateCounts()
    {
        TotalCount = _plan.Count;
        ChangedCount = _plan.Count(p => p.WillChange);
        SameCount = _plan.Count(p => p.Kind == RenameKind.Same);
        CheckCount = _plan.Count(p => p.Kind is RenameKind.MissingTags or RenameKind.Collision);
        IncludedCount = _plan.Count(p => p.WillChange && p.IsIncluded);
    }

    /// <summary>Ticks or unticks every changing row that the current filter shows.</summary>
    [RelayCommand]
    private void SetAllVisible(string? on)
    {
        var value = on == "True";
        foreach (var row in PreviewView.Cast<RenamePreviewItemViewModel>().Where(r => r.WillChange))
            row.IsIncluded = value;
    }

    private bool CanExecute() => IncludedCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var plan = _plan;
        var chosen = plan.Where(p => p.WillChange && p.IsIncluded).ToList();
        if (chosen.Count == 0) return;

        var collisions = chosen.Count(p => p.Kind == RenameKind.Collision);
        var lrc = chosen.Count(p => File.Exists(Path.ChangeExtension(p.OldPath, ".lrc")));
        var detail = new List<string>();
        if (lrc > 0) detail.Add($"가사 파일(.lrc) {lrc}개도 함께 바뀝니다.");
        if (collisions > 0) detail.Add($"{collisions}곡은 이름이 겹쳐 번호가 붙습니다.");
        detail.Add("바꾼 뒤 '되돌리기'로 원래 이름으로 돌릴 수 있습니다.");
        var confirmed = await Dialogs.ConfirmAsync("이름 바꾸기",
            $"{chosen.Count}곡의 파일 이름을 바꿉니다.\n\n{string.Join("\n", detail)}", "이름 바꾸기");
        if (!confirmed) return;

        IsBusy = true;
        ExecuteCommand.NotifyCanExecuteChanged();
        StatusText = $"이름을 바꾸는 중… (0/{chosen.Count})";
        var moves = new List<(string From, string To)>();
        List<string> errors;
        // A playing file can't be renamed while the player holds it; release it, then resume
        // from its new name (or the old one, if its rename failed).
        var playingPath = _playback.CurrentFile?.Path;
        var resume = await _playback.ReleaseIfPlayingAsync(chosen.Select(i => i.OldPath));
        try
        {
            var progress = new Progress<int>(n => StatusText = $"이름을 바꾸는 중… ({n}/{chosen.Count})");
            errors = await Task.Run(() => RenameService.Execute(plan, moves, progress));
        }
        catch (Exception ex)
        {
            errors = [ex.Message];
        }
        finally
        {
            IsBusy = false;
            ExecuteCommand.NotifyCanExecuteChanged();
        }

        resume?.Invoke(NewPathOf(playingPath, moves));
        foreach (var row in Preview) row.SyncStatus();

        _lastMoves = moves;
        _lastFolder = FolderPath;
        var renamed = moves.Count(m => TagService.IsSupported(m.To));
        CanUndo = renamed > 0;
        UndoSummary = CanUndo ? $"방금 {renamed}곡의 이름을 바꿨습니다" : "";

        RescanAffected(moves);
        if (errors.Count == 0)
            Toast.Success("이름 바꾸기 완료", $"{renamed}곡의 이름을 바꿨습니다.");
        else
            Toast.Warning("일부 파일 이름을 바꾸지 못했습니다", $"{renamed}곡 변경, {errors.Count}곡 실패.");

        // The songs now live under their new names: plan again from those.
        await ReloadMovedAsync(moves);
        StatusText = errors.Count == 0
            ? $"{renamed}곡의 이름을 바꿨습니다."
            : $"{renamed}곡 변경, {errors.Count}곡 실패: {string.Join("; ", errors.Take(3))}";
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var moves = _lastMoves;
        if (moves.Count == 0) return;

        IsBusy = true;
        StatusText = "원래 이름으로 되돌리는 중…";
        var playingPath = _playback.CurrentFile?.Path;
        var resume = await _playback.ReleaseIfPlayingAsync(moves.Select(m => m.To));
        List<string> errors;
        try
        {
            errors = await Task.Run(() => RenameService.Undo(moves));
        }
        catch (Exception ex)
        {
            errors = [ex.Message];
        }
        finally
        {
            IsBusy = false;
        }

        var back = moves.Select(m => (From: m.To, To: m.From)).ToList();
        resume?.Invoke(NewPathOf(playingPath, back));
        _lastMoves = [];
        CanUndo = false;
        UndoSummary = "";
        RescanAffected(moves);
        await ReloadMovedAsync(back);
        StatusText = errors.Count == 0 ? "원래 이름으로 되돌렸습니다." : $"일부를 되돌리지 못했습니다: {string.Join("; ", errors.Take(3))}";
        if (errors.Count == 0) Toast.Success("되돌리기 완료", "파일 이름을 원래대로 돌렸습니다.");
    }

    private static string? NewPathOf(string? path, IEnumerable<(string From, string To)> moves)
    {
        if (path is null) return null;
        foreach (var (from, to) in moves)
            if (string.Equals(from, path, StringComparison.OrdinalIgnoreCase)) return to;
        return null;
    }

    private void RescanAffected(IEnumerable<(string From, string To)> moves) =>
        _library.RescanIfAffected(moves.Select(m => Path.GetDirectoryName(m.To)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    private async Task ReloadMovedAsync(IReadOnlyList<(string From, string To)> moves)
    {
        var map = moves.Where(m => TagService.IsSupported(m.To))
            .GroupBy(m => m.From, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().To, StringComparer.OrdinalIgnoreCase);
        var paths = _targetPaths.Select(p => map.TryGetValue(p, out var to) ? to : p).ToList();
        var version = ++_loadVersion;
        var targets = await Task.Run(() => RenameService.ReadTargets(paths));
        if (version != _loadVersion) return;
        _targetPaths = paths;
        _targets = targets;
        await ReplanAsync();
    }
}
