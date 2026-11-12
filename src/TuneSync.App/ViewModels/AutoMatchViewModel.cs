using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Drives the 자동 채움 panel. "완전 자동 적용" and "검토 후 적용" are one pipeline
/// (BuildProposalsAsync always runs the same way) with a single branch at the end: automatic
/// mode applies everything right after building; review mode waits for the user to check rows
/// and press "선택 항목 적용".</summary>
public sealed partial class AutoMatchViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly PlaybackViewModel _playback;
    private readonly IAutoFillService _autoFillService;
    private readonly AppSettings _settings;

    public ObservableCollection<AutoMatchItemViewModel> Results { get; } = [];

    public bool HasResults => Results.Count > 0;

    [ObservableProperty]
    private bool _isAutomaticMode;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "채울 항목을 고른 뒤 검색을 누르세요.";

    // What to search for. Pre-checked from what the selected songs are missing; the user can
    // tick anything else (e.g. a better cover for a song that already has one).
    [ObservableProperty]
    private bool _fillTags = true;

    [ObservableProperty]
    private bool _fillCover = true;

    [ObservableProperty]
    private bool _fillLyrics = true;

    [ObservableProperty]
    private string _targetDescription = "선택한 곡이 없으면 표시된 전체 곡에서 누락된 항목만 찾습니다.";

    private IReadOnlyList<LibraryFileViewModel> _selection = [];

    /// <summary>Raised after proposals were written to files, so views showing a file's
    /// tags/cover/lyrics can reload them.</summary>
    public event EventHandler? Applied;

    /// <summary>The list selection changed: retarget, and pre-check what those songs are missing.</summary>
    public void OnSelectionChanged(IReadOnlyList<LibraryFileViewModel> selection)
    {
        _selection = selection;
        if (selection.Count == 0)
        {
            TargetDescription = "선택한 곡이 없으면 표시된 전체 곡에서 누락된 항목만 찾습니다.";
            FillTags = FillCover = FillLyrics = true;
            return;
        }

        TargetDescription = selection.Count == 1
            ? $"대상: {selection[0].DisplayTitle}"
            : $"대상: 선택한 {selection.Count}곡";
        FillTags = selection.Any(f => f.IsMissingTags);
        FillCover = selection.Any(f => f.IsMissingCover);
        FillLyrics = selection.Any(f => f.IsMissingLyrics);
    }

    public AutoMatchViewModel(LibraryViewModel library, PlaybackViewModel playback, IAutoFillService autoFillService, AppSettings settings)
    {
        _library = library;
        _playback = playback;
        _autoFillService = autoFillService;
        _settings = settings;
        _isAutomaticMode = settings.AutoFillIsAutomaticMode;
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
    }

    partial void OnIsAutomaticModeChanged(bool value)
    {
        _settings.AutoFillIsAutomaticMode = value;
        AppSettingsStore.Save(_settings);
    }

    /// <summary>The rows selected in the list, or - with nothing selected - every row the current
    /// 누락 filter shows (what the user is looking at, not the whole library behind the filter).</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken ct)
    {
        if (!FillTags && !FillCover && !FillLyrics)
        {
            StatusText = "채울 항목(태그, 앨범 커버, 가사)을 하나 이상 선택해 주세요.";
            return;
        }

        // Selected songs: the user picked them, so an explicitly ticked cover/lyrics is searched
        // even if one exists (to replace it after review). No selection: missing items only.
        var explicitTargets = _selection.Count > 0;
        List<LibraryFile> targets = explicitTargets
            ? [.. _selection.Select(f => f.Model)]
            : [.. _library.FilesView.Cast<LibraryFileViewModel>().Select(f => f.Model)];
        if (targets.Count == 0)
        {
            StatusText = "검색할 파일이 없습니다. 먼저 하단 + 버튼으로 폴더/파일을 추가해 주세요.";
            return;
        }

        var options = new AutoFillOptions(FillTags, FillCover, FillLyrics, ReplaceExisting: explicitTargets);

        IsRunning = true;
        Results.Clear();
        var progress = new Progress<string>(msg => StatusText = msg);

        List<AutoFillProposal> proposals;
        try
        {
            proposals = await _autoFillService.BuildProposalsAsync(targets, progress, ct, options);
        }
        catch (OperationCanceledException)
        {
            StatusText = "검색을 취소했습니다.";
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"검색 중 오류가 발생했습니다: {ex.Message}";
            Toast.Error("자동 채움 검색 실패", ex.Message);
            return;
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
            var errors = await ApplyAsync(proposals.Where(p => p.HasAnyProposal).ToList());
            StatusText = errors.Count == 0
                ? $"완전 자동 적용 완료: {proposals.Count}개 중 {withProposal}개 파일에 반영했습니다."
                : $"{withProposal - errors.Count}개 반영, {errors.Count}개 실패: {string.Join("; ", errors)}";
        }
        else
        {
            StatusText = withProposal == 0
                ? $"검색 완료: {proposals.Count}개 파일에서 채울 수 있는 항목을 찾지 못했습니다."
                : $"검색 완료: {proposals.Count}개 중 {withProposal}개 제안. 체크한 항목만 '선택 항목 적용'으로 반영됩니다.";
        }
    }

    private bool CanApplySelected() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private async Task ApplySelectedAsync()
    {
        var chosen = Results.Where(r => r.IsSelected && r.HasProposal).ToList();
        foreach (var item in chosen) item.ApplyChoices();
        var selected = chosen.Select(r => r.Proposal).Where(p => p.HasAnyProposal).ToList();
        if (selected.Count == 0)
        {
            StatusText = "적용할 항목이 없습니다. 체크박스로 적용할 항목을 선택해 주세요.";
            return;
        }

        var errors = await ApplyAsync(selected);
        StatusText = errors.Count == 0
            ? $"{selected.Count}개 항목을 적용했습니다."
            : $"{selected.Count - errors.Count}개 적용, {errors.Count}개 실패: {string.Join("; ", errors)}";
    }

    /// <summary>Writes the proposals to the files off the UI thread (tag/cover writes over a network
    /// share can take a while), then refreshes the affected library rows.</summary>
    private async Task<List<string>> ApplyAsync(List<AutoFillProposal> proposals)
    {
        var undoDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TuneSync", "autofill-history");

        IsRunning = true;
        StatusText = "파일에 적용하는 중...";
        List<string> errors;
        // The playing file is locked by the player; release it for the write and resume after.
        var resume = await _playback.ReleaseIfPlayingAsync(proposals.Select(p => p.File.Path));
        try
        {
            errors = await Task.Run(() => _autoFillService.ApplyProposals(proposals, undoDir));
        }
        finally
        {
            resume?.Invoke(null);
            IsRunning = false;
        }

        foreach (var item in Results) item.NotifyStatusChanged();
        _library.RefreshRows(proposals.Select(p => p.File));
        Applied?.Invoke(this, EventArgs.Empty);

        var applied = proposals.Count - errors.Count;
        if (errors.Count == 0)
            Toast.Success("자동 채움 적용 완료", $"{applied}개 파일에 반영했습니다.");
        else
            Toast.Warning("일부 파일에 적용하지 못했습니다", $"{applied}개 반영, {errors.Count}개 실패. 결과 표의 상태를 확인해 주세요.");
        return errors;
    }
}
