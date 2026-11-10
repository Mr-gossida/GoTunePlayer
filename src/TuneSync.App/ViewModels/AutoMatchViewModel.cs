using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Drives the 자동 채움 tab. "완전 자동 적용" and "검토 후 적용" are one pipeline
/// (BuildProposalsAsync always runs the same way) with a single branch at the end: automatic
/// mode calls ApplyProposals on everything right after building; review mode waits for the user
/// to check rows and press "선택 항목 적용".</summary>
public sealed partial class AutoMatchViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly IAutoFillService _autoFillService;

    public ObservableCollection<AutoMatchItemViewModel> Results { get; } = [];

    [ObservableProperty]
    private bool _isAutomaticMode;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "라이브러리에서 폴더를 스캔한 뒤 실행해 주세요.";

    public AutoMatchViewModel(LibraryViewModel library, IAutoFillService autoFillService)
    {
        _library = library;
        _autoFillService = autoFillService;
    }

    private List<LibraryFile> GetTargets()
    {
        var selected = _library.Files.Where(f => f.IsSelected).Select(f => f.Model).ToList();
        return selected.Count > 0 ? selected : [.. _library.Files.Select(f => f.Model)];
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsRunning) return;

        var targets = GetTargets();
        if (targets.Count == 0)
        {
            StatusText = "검색할 파일이 없습니다. 먼저 라이브러리에서 폴더를 스캔해 주세요.";
            return;
        }

        IsRunning = true;
        Results.Clear();
        var progress = new Progress<string>(msg => StatusText = msg);

        List<AutoFillProposal> proposals;
        try
        {
            proposals = await _autoFillService.BuildProposalsAsync(targets, progress);
        }
        finally
        {
            IsRunning = false;
        }

        foreach (var proposal in proposals)
            Results.Add(new AutoMatchItemViewModel(proposal));

        var withProposal = proposals.Count(p => p.HasAnyProposal);

        if (IsAutomaticMode && withProposal > 0)
        {
            ApplyInternal(proposals.Where(p => p.HasAnyProposal));
            StatusText = $"완전 자동 적용 완료: {proposals.Count}개 중 {withProposal}개 파일에 반영했습니다.";
        }
        else
        {
            StatusText = $"검색 완료: {proposals.Count}개 중 {withProposal}개 제안 생성. "
                + "체크한 항목만 '선택 항목 적용'으로 반영됩니다.";
        }
    }

    [RelayCommand]
    private void ApplySelected()
    {
        var selected = Results.Where(r => r.IsSelected && r.HasProposal).Select(r => r.Proposal).ToList();
        if (selected.Count == 0)
        {
            StatusText = "적용할 항목이 없습니다. 체크박스로 적용할 항목을 선택해 주세요.";
            return;
        }

        ApplyInternal(selected);
        StatusText = $"{selected.Count}개 항목을 적용했습니다.";
    }

    private void ApplyInternal(IEnumerable<AutoFillProposal> proposals)
    {
        var undoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuneSync", "autofill-history");

        var errors = _autoFillService.ApplyProposals(proposals, undoDir);

        foreach (var item in Results) item.NotifyStatusChanged();

        if (errors.Count > 0)
            StatusText = $"일부 적용 실패: {string.Join("; ", errors)}";
    }
}
