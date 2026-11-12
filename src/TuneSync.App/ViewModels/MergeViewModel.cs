using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Which rows the merge plan shows (the summary tiles double as these filters).</summary>
public enum MergeFilter
{
    All,
    Move,
    Skip,
    Replace,
    Rename,
}

/// <summary>폴더 합치기: "옮길 폴더 → 받을 폴더" with live analysis. Every song is planned as a
/// new move, a duplicate left out (quarantined), a replacement by the better copy, or a move
/// under a new name; conflicts are compared side by side and each can be decided by hand.
/// Nothing is deleted - losers go to the quarantine folder - and a run can be undone.</summary>
public sealed partial class MergeViewModel : ObservableObject
{
    public ObservableCollection<MergeActionViewModel> Rows { get; } = [];
    public ICollectionView RowsView { get; }

    public IReadOnlyList<Choice<MergeConflictResolution>> ConflictResolutionOptions { get; } =
    [
        new(MergeConflictResolution.KeepBetterQuality, "음질 좋은 쪽 유지 (추천)"),
        new(MergeConflictResolution.KeepLargerFile, "더 큰 파일 유지"),
        new(MergeConflictResolution.KeepNewerFile, "더 최근 파일 유지"),
        new(MergeConflictResolution.KeepSourceAlways, "항상 옮길 폴더 쪽 유지"),
        new(MergeConflictResolution.KeepTargetAlways, "항상 받을 폴더 쪽 유지"),
        new(MergeConflictResolution.RenameWithSuffix, "둘 다 보존 (이름에 번호)"),
    ];

    [ObservableProperty]
    private string? _sourceFolder;

    [ObservableProperty]
    private string? _targetFolder;

    [ObservableProperty]
    private string _sourceStats = "";

    [ObservableProperty]
    private string _targetStats = "";

    [ObservableProperty]
    private MergeConflictResolution _conflictResolution = MergeConflictResolution.KeepBetterQuality;

    [ObservableProperty]
    private string _quarantineFolderName = "_TuneSync_Quarantine";

    [ObservableProperty]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    private bool _removeEmptyFolders;

    [ObservableProperty]
    private MergeFilter _filter = MergeFilter.All;

    [ObservableProperty]
    private MergeActionViewModel? _selectedRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsEmptyState))]
    private bool _isAnalyzing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExecuteLabel))]
    private int _totalCount;

    public string ExecuteLabel => TotalCount > 0 ? $"{TotalCount}곡 합치기" : "합치기";
    [ObservableProperty] private int _moveCount;
    [ObservableProperty] private int _skipCount;
    [ObservableProperty] private int _replaceCount;
    [ObservableProperty] private int _renameCount;
    [ObservableProperty] private string _incomingSize = "";

    [ObservableProperty] private double _progressValue;
    [ObservableProperty] private double _progressMax = 1;
    [ObservableProperty] private string _progressText = "";

    /// <summary>After a run: what happened, with undo and the quarantine folder at hand.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _resultSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UndoCommand))]
    private bool _canUndo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _isPlanReady;

    public bool HasResult => ResultSummary.Length > 0;
    public bool IsIdle => !IsAnalyzing && !IsRunning;
    public bool IsEmptyState => !IsAnalyzing && Rows.Count == 0;
    public bool HasRows => Rows.Count > 0;
    public string SourceTitle => Leaf(SourceFolder);
    public string TargetTitle => Leaf(TargetFolder);

    private static string Leaf(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "폴더를 고르거나 끌어다 놓으세요";
        var leaf = Path.GetFileName(folder.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(leaf) ? folder : leaf;
    }

    public bool BothFoldersSet => !string.IsNullOrWhiteSpace(SourceFolder) && !string.IsNullOrWhiteSpace(TargetFolder);

    private readonly AppSettings _settings;
    private readonly LibraryViewModel _library;
    private readonly PlaybackViewModel _playback;
    private CancellationTokenSource? _analyzeCts;
    private CancellationTokenSource? _runCts;
    private List<(string From, string To)> _lastManifest = [];
    private string? _lastSource, _lastTarget;
    private int _statsVersion;

    public MergeViewModel(AppSettings settings, LibraryViewModel library, PlaybackViewModel playback)
    {
        _settings = settings;
        _library = library;
        _playback = playback;

        _sourceFolder = settings.LastMergeSourceFolder;
        _targetFolder = settings.LastMergeTargetFolder;
        _conflictResolution = settings.MergeConflictResolution;
        _quarantineFolderName = settings.MergeQuarantineFolderName;
        _includeSubfolders = settings.MergeIncludeSubfolders;
        _removeEmptyFolders = settings.MergeRemoveEmptyFolders;

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = o => o is MergeActionViewModel row && Filter switch
        {
            MergeFilter.Move => row.ActionType == MergeActionType.Move,
            MergeFilter.Skip => row.ActionType == MergeActionType.Quarantine,
            MergeFilter.Replace => row.ActionType == MergeActionType.Replace,
            MergeFilter.Rename => row.ActionType == MergeActionType.RenameAndMove,
            _ => true,
        };
    }

    /// <summary>The window opened: folder figures and the plan, fresh from disk.</summary>
    public void Activate()
    {
        if (IsRunning) return;
        _ = UpdateStatsAsync();
        if (!HasResult) _ = AnalyzeAsync();
    }

    /// <summary>The settings page edits the same AppSettings instance; pick its value up here.</summary>
    public void ReloadQuarantineFolderName() => QuarantineFolderName = _settings.MergeQuarantineFolderName;

    private string QuarantineName => string.IsNullOrWhiteSpace(QuarantineFolderName) ? "_TuneSync_Quarantine" : QuarantineFolderName.Trim();
    public string QuarantinePath => string.IsNullOrWhiteSpace(TargetFolder) ? "" : Path.Combine(TargetFolder, QuarantineName);

    partial void OnSourceFolderChanged(string? value)
    {
        _settings.LastMergeSourceFolder = value;
        AppSettingsStore.Save(_settings);
        OnPropertyChanged(nameof(SourceTitle));
        OnPropertyChanged(nameof(BothFoldersSet));
        InputsChanged();
    }

    partial void OnTargetFolderChanged(string? value)
    {
        _settings.LastMergeTargetFolder = value;
        AppSettingsStore.Save(_settings);
        OnPropertyChanged(nameof(TargetTitle));
        OnPropertyChanged(nameof(BothFoldersSet));
        OnPropertyChanged(nameof(QuarantinePath));
        InputsChanged();
    }

    partial void OnConflictResolutionChanged(MergeConflictResolution value)
    {
        _settings.MergeConflictResolution = value;
        AppSettingsStore.Save(_settings);
        InputsChanged(stats: false);
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        _settings.MergeIncludeSubfolders = value;
        AppSettingsStore.Save(_settings);
        InputsChanged();
    }

    partial void OnRemoveEmptyFoldersChanged(bool value)
    {
        _settings.MergeRemoveEmptyFolders = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnQuarantineFolderNameChanged(string value)
    {
        _settings.MergeQuarantineFolderName = value;
        AppSettingsStore.Save(_settings);
        OnPropertyChanged(nameof(QuarantinePath));
    }

    partial void OnFilterChanged(MergeFilter value) => RowsView.Refresh();

    partial void OnSelectedRowChanged(MergeActionViewModel? value) => _ = value?.LoadFactsAsync();

    private bool _swapping;

    private void InputsChanged(bool stats = true)
    {
        if (IsRunning || _swapping) return;
        ResultSummary = "";
        if (stats) _ = UpdateStatsAsync();
        _ = AnalyzeAsync();
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var folder = FileSystemPicker.PickFolder("옮길 폴더 선택 (폴더로 이동한 뒤 '선택'을 누르세요)", SourceFolder);
        if (folder is not null) SourceFolder = folder;
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var folder = FileSystemPicker.PickFolder("받을 폴더 선택 (폴더로 이동한 뒤 '선택'을 누르세요)", TargetFolder);
        if (folder is not null) TargetFolder = folder;
    }

    [RelayCommand]
    private void Swap()
    {
        if (IsRunning) return;
        var (source, target) = (TargetFolder, SourceFolder);
        _swapping = true;
        try
        {
            SourceFolder = source;
            TargetFolder = target;
        }
        finally
        {
            _swapping = false;
        }
        InputsChanged();
    }

    /// <summary>A folder dropped on one of the cards.</summary>
    public void DropFolder(string path, bool asTarget)
    {
        if (IsRunning || !Directory.Exists(path)) return;
        if (asTarget) TargetFolder = path;
        else SourceFolder = path;
    }

    [RelayCommand]
    private Task RefreshAsync()
    {
        ResultSummary = "";
        _ = UpdateStatsAsync();
        return AnalyzeAsync();
    }

    private async Task UpdateStatsAsync()
    {
        var version = ++_statsVersion;
        var source = SourceFolder;
        var target = TargetFolder;
        var deep = IncludeSubfolders;
        static string Describe(string? folder, SearchOption option)
        {
            if (string.IsNullOrWhiteSpace(folder)) return "";
            if (!Directory.Exists(folder)) return "폴더를 찾을 수 없습니다";
            var files = Directory.EnumerateFiles(folder, "*.*", option).Where(TagService.IsSupported).Select(f => new FileInfo(f)).ToList();
            return $"{files.Count:N0}곡 · {MergeFileFacts.FormatSize(files.Sum(f => f.Length))}";
        }
        try
        {
            var (s, t) = await Task.Run(() => (
                Describe(source, deep ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly),
                Describe(target, SearchOption.TopDirectoryOnly)));
            if (version != _statsVersion) return;
            SourceStats = s;
            TargetStats = t;
        }
        catch (Exception)
        {
            if (version == _statsVersion) SourceStats = TargetStats = "";
        }
    }

    private string? ValidateFolders()
    {
        if (!BothFoldersSet) return "옮길 폴더와 받을 폴더를 골라 주세요. 폴더를 카드에 끌어다 놓아도 됩니다.";
        if (!Directory.Exists(SourceFolder) || !Directory.Exists(TargetFolder)) return "폴더를 찾을 수 없습니다. 다시 골라 주세요.";
        var source = Path.GetFullPath(SourceFolder!).TrimEnd('\\') + "\\";
        var target = Path.GetFullPath(TargetFolder!).TrimEnd('\\') + "\\";
        if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
            return "두 폴더는 서로 달라야 하며, 한쪽이 다른 쪽 안에 있으면 안 됩니다.";
        return null;
    }

    private async Task AnalyzeAsync()
    {
        _analyzeCts?.Cancel();
        var cts = _analyzeCts = new CancellationTokenSource();
        Rows.Clear();
        SelectedRow = null;
        IsPlanReady = false;
        UpdateCounts();

        if (ValidateFolders() is { } problem)
        {
            StatusText = problem;
            IsAnalyzing = false;
            return;
        }

        var source = SourceFolder!;
        var target = TargetFolder!;
        var rules = new MergeRuleSet { ConflictResolution = ConflictResolution, IncludeSubfolders = IncludeSubfolders, QuarantineFolderName = QuarantineName };
        IsAnalyzing = true;
        StatusText = "두 폴더의 곡을 비교하는 중…";
        try
        {
            var plan = await Task.Run(() => MergeService.BuildPlan(source, target, rules, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var action in plan) Rows.Add(new MergeActionViewModel(action, source, OnRowChanged));
            UpdateCounts();
            IsPlanReady = Rows.Count > 0;
            StatusText = Rows.Count == 0 ? "옮길 폴더에 음악 파일이 없습니다."
                : ReplaceCount + SkipCount > 0 ? "겹치는 곡은 목록에서 골라 두 파일을 비교하고, 남길 쪽을 직접 바꿀 수 있습니다."
                : "겹치는 곡이 없습니다. 모두 받을 폴더로 옮겨집니다.";
            SelectedRow = Rows.FirstOrDefault(r => r.IsConflict);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested) StatusText = $"폴더를 비교하지 못했습니다: {ex.Message}";
        }
        finally
        {
            if (_analyzeCts == cts) IsAnalyzing = false;
            OnPropertyChanged(nameof(IsEmptyState));
            OnPropertyChanged(nameof(HasRows));
        }
    }

    private void OnRowChanged()
    {
        UpdateCounts();
        RowsView.Refresh();
    }

    private void UpdateCounts()
    {
        TotalCount = Rows.Count;
        MoveCount = Rows.Count(r => r.ActionType == MergeActionType.Move);
        SkipCount = Rows.Count(r => r.ActionType == MergeActionType.Quarantine);
        ReplaceCount = Rows.Count(r => r.ActionType == MergeActionType.Replace);
        RenameCount = Rows.Count(r => r.ActionType == MergeActionType.RenameAndMove);
        IncomingSize = MergeFileFacts.FormatSize(Rows.Where(r => r.ActionType != MergeActionType.Quarantine).Sum(r => r.Action.SourceSize));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(IsEmptyState));
    }

    private bool CanExecute() => IsPlanReady;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var plan = Rows.Select(r => r.Action).ToList();
        if (plan.Count == 0 || TargetFolder is null || SourceFolder is null) return;

        var lines = new List<string> { $"{plan.Count}곡을 정리해 받을 폴더로 합칩니다." };
        if (MoveCount + RenameCount > 0) lines.Add($"· 새로 옮기기 {MoveCount + RenameCount}곡");
        if (ReplaceCount > 0) lines.Add($"· 더 좋은 파일로 교체 {ReplaceCount}곡 (기존 파일은 격리)");
        if (SkipCount > 0) lines.Add($"· 중복이라 옮기지 않음 {SkipCount}곡 (격리)");
        lines.Add("");
        lines.Add($"지우는 파일은 없습니다. 밀려난 파일은 '{QuarantineName}' 폴더로 가며, 끝난 뒤 되돌릴 수 있습니다.");
        if (!await Dialogs.ConfirmAsync("폴더 합치기", string.Join("\n", lines), "합치기")) return;

        var source = SourceFolder;
        var target = TargetFolder;
        var quarantine = Path.Combine(target, QuarantineName);
        var removeEmpty = RemoveEmptyFolders;
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        IsRunning = true;
        IsPlanReady = false;
        ProgressMax = plan.Count;
        ProgressValue = 0;
        ProgressText = $"0 / {plan.Count}";

        // The playing file is locked by the player; release it and pick it up again afterwards
        // from wherever it went (unless that's the quarantine folder).
        var touched = plan.Select(a => a.SourcePath).Concat(plan.Where(a => a.ConflictWithPath is not null).Select(a => a.ConflictWithPath!));
        var playingPath = _playback.CurrentFile?.Path;
        var resume = await _playback.ReleaseIfPlayingAsync(touched);

        var manifest = new List<(string From, string To)>();
        List<string> errors = [];
        var cancelled = false;
        var removedFolders = 0;
        try
        {
            var progress = new Progress<int>(n =>
            {
                ProgressValue = n;
                ProgressText = $"{n} / {plan.Count}";
            });
            errors = await Task.Run(() =>
            {
                var e = MergeService.ExecutePlan(plan, target, quarantine, manifest, progress, ct);
                if (removeEmpty) removedFolders = MergeService.RemoveEmptyFolders(source);
                return e;
            });
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
        }
        finally
        {
            IsRunning = false;
            _runCts = null;
        }

        if (resume is not null)
        {
            var moved = manifest.LastOrDefault(m => string.Equals(m.From, playingPath, StringComparison.OrdinalIgnoreCase)).To;
            if (moved is not null && moved.StartsWith(quarantine, StringComparison.OrdinalIgnoreCase))
                Toast.Info("재생을 멈췄습니다", "재생 중이던 곡이 격리 폴더로 옮겨졌습니다.");
            else
                resume(moved);
        }

        _lastManifest = manifest;
        _lastSource = source;
        _lastTarget = target;
        CanUndo = manifest.Count > 0;

        var songs = manifest.Where(m => TagService.IsSupported(m.To)).ToList();
        var toTarget = songs.Count(m => !m.To.StartsWith(quarantine, StringComparison.OrdinalIgnoreCase));
        var toQuarantine = songs.Count - toTarget;
        var parts = new List<string> { $"받을 폴더로 {toTarget}곡" };
        if (toQuarantine > 0) parts.Add($"격리 {toQuarantine}곡");
        if (removedFolders > 0) parts.Add($"빈 폴더 {removedFolders}개 정리");
        if (errors.Count > 0) parts.Add($"실패 {errors.Count}곡");
        ResultSummary = (cancelled ? "중간에 멈췄습니다: " : "합치기를 마쳤습니다: ") + string.Join(" · ", parts);
        StatusText = errors.Count > 0 ? "실패: " + string.Join("; ", errors.Take(3)) : "";
        Rows.Clear();
        SelectedRow = null;
        UpdateCounts();

        if (errors.Count == 0 && !cancelled) Toast.Success("폴더 합치기 완료", string.Join(" · ", parts));
        else if (cancelled) Toast.Warning("폴더 합치기를 멈췄습니다", "그때까지 옮긴 파일은 '되돌리기'로 되돌릴 수 있습니다.");
        else Toast.Warning("일부 파일을 옮기지 못했습니다", string.Join(" · ", parts));

        _library.RescanIfAffected(source, target);
        _ = UpdateStatsAsync();
    }

    [RelayCommand]
    private void Cancel() => _runCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var manifest = _lastManifest;
        if (manifest.Count == 0) return;
        IsRunning = true;
        ProgressMax = 1;
        ProgressValue = 0;
        ProgressText = "되돌리는 중…";
        var playingPath = _playback.CurrentFile?.Path;
        var resume = await _playback.ReleaseIfPlayingAsync(manifest.Select(m => m.To));
        List<string> errors;
        try
        {
            errors = await Task.Run(() => MergeService.Undo(manifest));
        }
        catch (Exception ex)
        {
            errors = [ex.Message];
        }
        finally
        {
            IsRunning = false;
        }

        resume?.Invoke(manifest.LastOrDefault(m => string.Equals(m.To, playingPath, StringComparison.OrdinalIgnoreCase)).From);
        _lastManifest = [];
        CanUndo = false;
        ResultSummary = "";
        if (errors.Count == 0) Toast.Success("되돌리기 완료", "합치기 전 상태로 돌렸습니다.");
        else Toast.Warning("일부를 되돌리지 못했습니다", string.Join("; ", errors.Take(3)));
        _library.RescanIfAffected(_lastSource, _lastTarget);
        await RefreshAsync();
        if (errors.Count > 0) StatusText = "되돌리지 못한 파일: " + string.Join("; ", errors.Take(3));
    }

    [RelayCommand]
    private void OpenQuarantine()
    {
        var path = QuarantinePath;
        if (string.IsNullOrEmpty(path)) return;
        if (!Directory.Exists(path))
        {
            Toast.Info("격리 폴더가 없습니다", "아직 격리된 파일이 없습니다.");
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { Toast.Error("폴더를 열지 못했습니다", ex.Message); }
    }

    [RelayCommand]
    private void DismissResult() => _ = RefreshAsync();
}
