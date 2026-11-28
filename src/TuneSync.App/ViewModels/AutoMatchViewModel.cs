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
    [NotifyCanExecuteChangedFor(nameof(ApplySelectedCommand), nameof(RunCommand))]
    private bool _isRunning;

    /// <summary>The tag editor, whose tab searches share "one search at a time" with this one.</summary>
    public TagEditorViewModel Editor => _editor;

    partial void OnIsRunningChanged(bool value) => _editor.IsAutoFillRunning = value;

    partial void OnStatusTextChanged(string value) => _editor.AutoFillStatus = value;

    /// <summary>Not while a tab's 찾기 runs (its progress shows here instead).</summary>
    private bool CanRun() => !_editor.IsSearching;

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
        TagEditorViewModel editor, OnlineSourcesViewModel sources)
    {
        _editor = editor;
        _library = library;
        _playback = playback;
        _autoFillService = autoFillService;
        _settings = settings;
        Sources = sources;
        _isAutomaticMode = settings.AutoFillIsAutomaticMode;
        Results.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasResults));
        editor.CancelAutoFill = () => RunCancelCommand.Execute(null);
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TagEditorViewModel.IsSearching)) RunCommand.NotifyCanExecuteChanged();
        };
    }

    /// <summary>Where tags and lyrics are looked for (the same switches as the tag editor's chips
    /// and 설정 › 도구).</summary>
    public OnlineSourcesViewModel Sources { get; }

    /// <summary>Where the last search's answers came from, a line per kind: "태그: MusicBrainz 8곡 · iTunes 2곡".</summary>
    [ObservableProperty]
    private string _sourceSummary = "";

    partial void OnIsAutomaticModeChanged(bool value)
    {
        _settings.AutoFillIsAutomaticMode = value;
        AppSettingsStore.Save(_settings);
    }

    /// <summary>The rows selected in the list, or - with nothing selected - every row the current
    /// 누락 filter shows (what the user is looking at, not the whole library behind the filter).</summary>
    [RelayCommand(IncludeCancelCommand = true, CanExecute = nameof(CanRun))]
    private async Task RunAsync(CancellationToken ct)
    {
        if (_editor.IsSearching) return;
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

        var options = new AutoFillOptions(FillTags, FillCover, FillLyrics, ReplaceExisting: explicitTargets)
        {
            DisabledTagSources = Sources.DisabledTagSources,
            DisabledLyricsSources = Sources.DisabledLyricsSources,
        };

        IsRunning = true;
        Results.Clear();
        SourceSummary = "";
        var done = 0;
        var progress = new Progress<string>(msg => StatusText = targets.Count > 1 ? $"({++done}/{targets.Count}) {msg}" : msg);

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
        {
            var item = new AutoMatchItemViewModel(proposal, _editor);
            item.Swapped += () => SourceSummary = Summarize([.. Results.Select(r => r.Proposal)]);
            Results.Add(item);
        }

        var withProposal = proposals.Count(p => p.HasAnyProposal);
        var unsure = proposals.Count(p => p.NeedsChoice);
        SourceSummary = Summarize(proposals);

        if (IsAutomaticMode && withProposal > 0)
        {
            var (songs, items) = StageAll(Results.Where(r => r.HasProposal));
            StatusText = $"{songs}곡의 변경 {items}개를 편집에 넣었습니다. 아래 저장 바에서 확인하고 저장하세요."
                         + (unsure > 0 ? $" 확실하지 않은 {unsure}곡은 넣지 않았으니, 아래 결과에서 골라 주세요." : "");
        }
        else
        {
            StatusText = withProposal == 0 && unsure == 0
                ? $"검색 완료: {proposals.Count}곡에서 채울 수 있는 항목을 찾지 못했습니다."
                : $"검색 완료: {proposals.Count}곡 중 {withProposal}곡에서 찾았습니다. 체크한 항목을 '편집에 넣기'로 넣은 뒤 저장하세요."
                  + (unsure > 0 ? $" 확실하지 않은 {unsure}곡은 결과를 펼쳐 골라 주세요." : "");
        }
    }

    /// <summary>Puts each result's ticked parts in the editor, and marks them as in.</summary>
    private (int Songs, int Items) StageAll(IEnumerable<AutoMatchItemViewModel> results)
    {
        var pairs = results.Select(r => (Item: r, Edit: r.Edit())).Where(p => !p.Edit.IsEmpty).ToList();
        if (pairs.Count == 0) return (0, 0);
        var counts = _editor.Stage([.. pairs.Select(p => p.Edit)]);
        foreach (var (item, edit) in pairs) item.MarkStaged(edit);
        return counts;
    }

    /// <summary>"태그: MusicBrainz 8곡 · iTunes 2곡" and the like, a line for each kind found.</summary>
    private static string Summarize(IReadOnlyList<AutoFillProposal> proposals)
    {
        var lines = new List<string>();
        void Line(string kind, IEnumerable<string?> sources)
        {
            var text = SearchSummary.BySource(sources);
            if (text.Length > 0) lines.Add($"{kind}: {text}");
        }
        Line("태그", proposals.Select(p => p.HasTagProposal ? p.ChosenTags?.Source : null));
        Line("커버", proposals.Select(p => p.ProposedCoverBytes is not null ? p.CoverSource : null));
        Line("가사", proposals.Select(p => string.IsNullOrWhiteSpace(p.ProposedLyrics) ? null : p.ChosenLyrics?.Source));
        Line("응답 없음", proposals.SelectMany(p => p.FailedSources.Distinct()));
        return string.Join(Environment.NewLine, lines);
    }

    private bool CanApplySelected() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanApplySelected))]
    private void ApplySelected()
    {
        // Into the editor as unsaved edits: highlighted in the list, reviewed and saved (with undo)
        // the same way as edits made by hand. The results stay, marked as in: another result is
        // still one click away, and 빼기 takes one back out.
        var (songs, items) = StageAll(Results.Where(r => r.IsSelected && r.HasProposal && !r.IsStaged));
        if (songs == 0)
        {
            StatusText = Results.Any(r => r.IsStaged) && !Results.Any(r => r.IsSelected && r.HasProposal && !r.IsStaged)
                ? "체크한 결과는 모두 편집에 넣었습니다. 아래 저장 바에서 확인하고 저장하세요."
                : "넣을 항목이 없습니다. 체크박스로 넣을 항목을 고르세요.";
            return;
        }
        var left = Results.Count(r => r.NeedsChoice && !r.IsStaged);
        StatusText = $"{songs}곡의 변경 {items}개를 편집에 넣었습니다. 아래 저장 바에서 확인하고 저장하세요."
                     + (left > 0 ? $" 확실하지 않은 {left}곡은 결과를 펼쳐 골라 주세요." : "");
    }
}
