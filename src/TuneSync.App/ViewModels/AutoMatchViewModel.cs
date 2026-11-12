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
    private readonly TagEditorViewModel _editor;

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

    public AutoMatchViewModel(LibraryViewModel library, PlaybackViewModel playback, IAutoFillService autoFillService, AppSettings settings,
        TagEditorViewModel editor)
    {
        _editor = editor;
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
            var (songs, items) = _editor.Stage(proposals.Where(p => p.HasAnyProposal));
            StatusText = $"{songs}곡의 변경 {items}개를 편집에 넣었습니다. 아래 저장 바에서 확인하고 저장하세요.";
        }
        else
        {
            StatusText = withProposal == 0
                ? $"검색 완료: {proposals.Count}곡에서 채울 수 있는 항목을 찾지 못했습니다."
                : $"검색 완료: {proposals.Count}곡 중 {withProposal}곡에서 찾았습니다. 체크한 항목을 '편집에 넣기'로 넣은 뒤 저장하세요.";
        }
    }

    private bool CanApplySelected() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private void ApplySelected()
    {
        var chosen = Results.Where(r => r.IsSelected && r.HasProposal).ToList();
        foreach (var item in chosen) item.ApplyChoices();
        var selected = chosen.Select(r => r.Proposal).Where(p => p.HasAnyProposal).ToList();
        if (selected.Count == 0)
        {
            StatusText = "넣을 항목이 없습니다. 체크박스로 넣을 항목을 고르세요.";
            return;
        }

        // Into the editor as unsaved edits: highlighted in the list, reviewed and saved (with undo)
        // the same way as edits made by hand.
        var (songs, items) = _editor.Stage(selected);
        foreach (var item in chosen) Results.Remove(item);
        StatusText = $"{songs}곡의 변경 {items}개를 편집에 넣었습니다. 아래 저장 바에서 확인하고 저장하세요.";
    }
}
