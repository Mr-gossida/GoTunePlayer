using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

public sealed partial class MergeViewModel : ObservableObject
{
    public ObservableCollection<MergeActionViewModel> Plan { get; } = [];

    public IReadOnlyList<(MergeConflictResolution Value, string Label)> ConflictResolutionOptions { get; } =
    [
        (MergeConflictResolution.KeepLargerFile, "더 큰 파일 유지"),
        (MergeConflictResolution.KeepNewerFile, "더 최신 파일 유지"),
        (MergeConflictResolution.KeepSourceAlways, "항상 소스 우선"),
        (MergeConflictResolution.KeepTargetAlways, "항상 대상 우선"),
        (MergeConflictResolution.RenameWithSuffix, "이름 변경 후 둘 다 보존"),
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
    private string _statusText = "소스와 타겟 폴더를 선택한 뒤 미리보기를 실행해 주세요.";

    private List<MergeAction>? _builtPlan;

    [RelayCommand]
    private void BrowseSource()
    {
        var dialog = new OpenFolderDialog { Title = "소스 폴더 선택" };
        if (dialog.ShowDialog() == true)
        {
            SourceFolder = dialog.FolderName;
            InvalidatePlan();
        }
    }

    [RelayCommand]
    private void BrowseTarget()
    {
        var dialog = new OpenFolderDialog { Title = "타겟 폴더 선택" };
        if (dialog.ShowDialog() == true)
        {
            TargetFolder = dialog.FolderName;
            InvalidatePlan();
        }
    }

    [RelayCommand]
    private void BuildPreview()
    {
        if (string.IsNullOrWhiteSpace(SourceFolder) || string.IsNullOrWhiteSpace(TargetFolder))
        {
            StatusText = "소스와 타겟 폴더를 모두 선택해 주세요.";
            return;
        }

        var rules = new MergeRuleSet
        {
            ConflictResolution = ConflictResolution,
            IncludeSubfolders = IncludeSubfolders,
            QuarantineFolderName = string.IsNullOrWhiteSpace(QuarantineFolderName) ? "_TuneSync_Quarantine" : QuarantineFolderName,
        };

        try
        {
            _builtPlan = MergeService.BuildPlan(SourceFolder, TargetFolder, rules);
        }
        catch (Exception ex)
        {
            StatusText = $"미리보기 생성 실패: {ex.Message}";
            return;
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
    private void Execute()
    {
        if (_builtPlan is not { Count: > 0 } || string.IsNullOrWhiteSpace(TargetFolder)) return;

        var quarantine = Path.Combine(TargetFolder,
            string.IsNullOrWhiteSpace(QuarantineFolderName) ? "_TuneSync_Quarantine" : QuarantineFolderName);

        try
        {
            var manifest = MergeService.ExecutePlan(_builtPlan, TargetFolder, quarantine);
            StatusText = $"{manifest.Count}개 파일을 이동했습니다.";
        }
        catch (Exception ex)
        {
            StatusText = $"실행 중 오류: {ex.Message}";
        }

        InvalidatePlan();
    }

    private void InvalidatePlan()
    {
        _builtPlan = null;
        Plan.Clear();
        IsPlanReady = false;
    }
}
