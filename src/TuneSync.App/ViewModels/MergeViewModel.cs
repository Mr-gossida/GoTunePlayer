using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

public sealed partial class MergeViewModel : ObservableObject
{
    public ObservableCollection<MergeActionViewModel> Plan { get; } = [];

    public IReadOnlyList<Choice<MergeConflictResolution>> ConflictResolutionOptions { get; } =
    [
        new(MergeConflictResolution.KeepLargerFile, "더 큰 파일 유지"),
        new(MergeConflictResolution.KeepNewerFile, "더 최신 파일 유지"),
        new(MergeConflictResolution.KeepSourceAlways, "항상 소스 우선"),
        new(MergeConflictResolution.KeepTargetAlways, "항상 타겟 우선"),
        new(MergeConflictResolution.RenameWithSuffix, "이름 변경 후 둘 다 보존"),
    ];

    [ObservableProperty]
    private string? _sourceFolder;

    [ObservableProperty]
    private string? _targetFolder;

    [ObservableProperty]
    private MergeConflictResolution _conflictResolution = MergeConflictResolution.KeepLargerFile;

    [ObservableProperty]
    private string _quarantineFolderName = "_TuneSync_Quarantine";

    [ObservableProperty]
    private bool _includeSubfolders = true;

    [ObservableProperty]
    private bool _isPlanReady;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = "소스와 타겟 폴더를 선택한 뒤 미리보기를 실행해 주세요.";

    private List<MergeAction>? _builtPlan;
    private readonly AppSettings _settings;
    private readonly LibraryViewModel _library;
    private readonly PlaybackViewModel _playback;

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
    }

    [RelayCommand]
    private void BrowseSource()
    {
        var folder = FileSystemPicker.PickFolder("소스 폴더 선택 (폴더로 이동한 뒤 '선택'을 누르세요)", SourceFolder);
        if (folder is null) return;

        SourceFolder = folder;
        _settings.LastMergeSourceFolder = SourceFolder;
        AppSettingsStore.Save(_settings);
        InvalidatePlan();
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var folder = FileSystemPicker.PickFolder("타겟 폴더 선택 (폴더로 이동한 뒤 '선택'을 누르세요)", TargetFolder);
        if (folder is null) return;

        TargetFolder = folder;
        _settings.LastMergeTargetFolder = TargetFolder;
        AppSettingsStore.Save(_settings);
        InvalidatePlan();
    }

    /// <summary>The settings page edits the same AppSettings instance; pick its value up here.</summary>
    public void ReloadQuarantineFolderName() => QuarantineFolderName = _settings.MergeQuarantineFolderName;

    // Any change to the inputs makes an existing plan stale - "실행" must never move files
    // according to rules the user has since changed.
    partial void OnConflictResolutionChanged(MergeConflictResolution value)
    {
        _settings.MergeConflictResolution = value;
        AppSettingsStore.Save(_settings);
        InvalidatePlan();
    }

    partial void OnQuarantineFolderNameChanged(string value)
    {
        _settings.MergeQuarantineFolderName = value;
        AppSettingsStore.Save(_settings);
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        _settings.MergeIncludeSubfolders = value;
        AppSettingsStore.Save(_settings);
        InvalidatePlan();
    }

    [RelayCommand]
    private async Task BuildPreviewAsync()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder) || string.IsNullOrWhiteSpace(TargetFolder))
        {
            StatusText = "소스와 타겟 폴더를 모두 선택해 주세요.";
            return;
        }

        if (!Directory.Exists(SourceFolder) || !Directory.Exists(TargetFolder))
        {
            StatusText = "소스 또는 타겟 폴더를 찾을 수 없습니다. 폴더를 다시 선택해 주세요.";
            return;
        }

        var source = Path.GetFullPath(SourceFolder).TrimEnd('\\') + "\\";
        var target = Path.GetFullPath(TargetFolder).TrimEnd('\\') + "\\";
        if (source.StartsWith(target, StringComparison.OrdinalIgnoreCase) || target.StartsWith(source, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "소스와 타겟은 서로 다른 폴더여야 하며, 한쪽이 다른 쪽의 하위 폴더일 수 없습니다.";
            return;
        }

        var rules = new MergeRuleSet
        {
            ConflictResolution = ConflictResolution,
            IncludeSubfolders = IncludeSubfolders,
            QuarantineFolderName = string.IsNullOrWhiteSpace(QuarantineFolderName) ? "_TuneSync_Quarantine" : QuarantineFolderName,
        };

        IsBusy = true;
        StatusText = "폴더를 분석하는 중...";
        try
        {
            // BuildPlan reads every file's tags in both folders - can take a while for large
            // libraries, so it must never run on the UI thread (it did; the app froze).
            _builtPlan = await Task.Run(() => MergeService.BuildPlan(SourceFolder, TargetFolder, rules));
        }
        catch (Exception ex)
        {
            StatusText = $"미리보기 생성 실패: {ex.Message}";
            Toast.Error("미리보기 생성 실패", ex.Message);
            return;
        }
        finally
        {
            IsBusy = false;
        }

        Plan.Clear();
        foreach (var action in _builtPlan)
            Plan.Add(new MergeActionViewModel(action));

        IsPlanReady = Plan.Count > 0;
        StatusText = Plan.Count == 0
            ? "이동할 음원 파일이 없습니다."
            : $"{Plan.Count}개 파일에 대한 계획이 생성되었습니다. 검토 후 실행해 주세요.";
    }

    [RelayCommand]
    private async Task ExecuteAsync()
    {
        if (_builtPlan is not { Count: > 0 } plan || string.IsNullOrWhiteSpace(TargetFolder)) return;

        var quarantineName = string.IsNullOrWhiteSpace(QuarantineFolderName) ? "_TuneSync_Quarantine" : QuarantineFolderName;
        var quarantine = Path.Combine(TargetFolder, quarantineName);
        var quarantined = plan.Count(a => a.ActionType is MergeActionType.Quarantine or MergeActionType.Replace);

        var confirmed = await Dialogs.ConfirmAsync("폴더 합치기 실행",
            $"{plan.Count}개 파일을 타겟 폴더로 이동합니다."
            + (quarantined > 0 ? $"\n충돌한 {quarantined}개 파일은 삭제하지 않고 '{quarantineName}' 폴더로 옮깁니다." : "")
            + "\n\n계속할까요?",
            "실행");
        if (!confirmed) return;

        var sourceFolder = SourceFolder;
        var targetFolder = TargetFolder;
        IsBusy = true;
        StatusText = "파일을 이동하는 중...";
        string result;

        // The playing file is locked by the player and couldn't be moved; release it. It may end
        // up somewhere else (or in quarantine), so playback resumes only if it's still in place.
        var touched = plan.Select(a => a.SourcePath)
            .Concat(plan.Where(a => a.ConflictWithPath is not null).Select(a => a.ConflictWithPath!));
        var resume = await _playback.ReleaseIfPlayingAsync(touched);
        var playingPath = _playback.CurrentFile?.Path;

        try
        {
            var manifest = await Task.Run(() => MergeService.ExecutePlan(plan, targetFolder, quarantine));
            result = $"{manifest.Count}개 파일을 이동했습니다.";
            Toast.Success("폴더 합치기 완료", result);
        }
        catch (Exception ex)
        {
            result = $"실행 중 오류: {ex.Message}";
            Toast.Error("폴더 합치기 실패", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }

        if (resume is not null)
        {
            if (playingPath is not null && File.Exists(playingPath))
                resume(null);
            else
                Toast.Info("재생을 멈췄습니다", "재생 중이던 파일이 폴더 합치기로 이동되었습니다.");
        }

        InvalidatePlan(announce: false);
        StatusText = result;
        _library.RescanIfAffected(sourceFolder, targetFolder);
    }

    private void InvalidatePlan() => InvalidatePlan(announce: true);

    private void InvalidatePlan(bool announce)
    {
        if (_builtPlan is null && Plan.Count == 0) return;
        _builtPlan = null;
        Plan.Clear();
        IsPlanReady = false;
        if (announce) StatusText = "설정이 바뀌었습니다. 미리보기를 다시 실행해 주세요.";
    }
}
