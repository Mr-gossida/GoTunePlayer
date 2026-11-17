using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Services;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.App.ViewModels;

/// <summary>커버 찾기 for one song: several covers ranked by how well they match, the best put in
/// as the pending cover (추천 커버) and the rest offered as a gallery to swap in with one click.
/// Nothing is written until the user saves, as with every other edit. (With several songs
/// selected, 커버 찾기 still puts each song's best cover in directly.)</summary>
public sealed partial class TagEditorViewModel
{
    private readonly ICoverSearchService _coverSearch;
    private LibraryFileViewModel? _candidatesFile;
    private List<CoverCandidateViewModel> _candidateItems = [];

    /// <summary>The gallery; empty unless the selected song is the one last searched.</summary>
    public ObservableCollection<CoverCandidateViewModel> CoverCandidates { get; } = [];

    public bool HasCoverCandidates => CoverCandidates.Count > 0;

    public string CoverCandidatesHeader =>
        $"다른 후보 {CoverCandidates.Count(c => !c.IsOriginal && !c.IsRecommended)}개";

    /// <summary>The big cover is the recommended one (shows the 추천 커버 badge).</summary>
    public bool IsRecommendedCoverShown => CoverCandidates.Any(c => c.IsRecommended && c.IsSelected);

    /// <summary>Album, year and source of the candidate now pending, under the cover info.</summary>
    public string CoverCandidateDetail =>
        CoverCandidates.FirstOrDefault(c => c.IsSelected && !c.IsOriginal) is { Candidate: { } c }
            ? $"{(string.IsNullOrWhiteSpace(c.Album) ? c.Title : c.Album)}{(c.Year is { } y ? $" ({y})" : "")} · {c.Artist}"
            : "";

    private async Task SearchCoverCandidatesAsync(LibraryFileViewModel file)
    {
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        IsSearching = true;
        SearchStatus = "커버 후보를 찾는 중…";
        ShowCandidates(null, []);
        try
        {
            var query = AutoFillService.CoverQueryFor(file.Model);
            if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title))
            {
                SearchStatus = "아티스트와 제목을 알 수 없어 찾지 못했습니다. 태그를 먼저 채워 주세요.";
                return;
            }

            var progress = new Progress<CoverSearchProgress>(p =>
            {
                if (IsSearching && p.Done < p.Total) SearchStatus = $"커버 후보를 찾는 중… ({p.Done}/{p.Total})";
            });
            var result = await Task.Run(() => _coverSearch.SearchAsync(query, progress, ct), ct);
            if (result.Candidates.Count == 0)
            {
                SearchStatus = "맞는 커버를 찾지 못했습니다." + FailedNote(result);
                return;
            }

            var items = new List<CoverCandidateViewModel>();
            if (file.HasCover) items.Add(new CoverCandidateViewModel(file.CoverThumbnail, ""));
            items.AddRange(result.Candidates.Select((c, i) => new CoverCandidateViewModel(c, _coverSearch, i == 0)));
            ShowCandidates(file, items);

            // Put the best one in; if its image won't download, the next one becomes the recommendation.
            SearchStatus = "추천 커버를 받는 중…";
            foreach (var item in items.Where(i => !i.IsOriginal).Take(3).ToList())
            {
                var bytes = await item.GetFullAsync(ct);
                if (bytes is null) { RemoveCandidate(item); continue; }
                if (IsShowing(file)) ApplyCandidate(file, item);
                var others = CoverCandidates.Count(c => !c.IsOriginal) - 1;
                SearchStatus = (others > 0
                    ? $"추천 커버를 넣었습니다. 다른 후보 {others}개를 눌러 바꿀 수 있습니다. 확인한 뒤 저장하세요."
                    : "추천 커버를 넣었습니다. 확인한 뒤 저장하세요.") + FailedNote(result);
                return;
            }
            SearchStatus = "찾은 커버를 받지 못했습니다. 다른 후보를 눌러 보세요." + FailedNote(result);
        }
        catch (OperationCanceledException)
        {
            SearchStatus = "검색을 취소했습니다.";
        }
        catch (Exception ex)
        {
            SearchStatus = $"검색하지 못했습니다: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    private static string FailedNote(CoverSearchResult result) =>
        result.FailedSources.Count > 0 ? $" ({string.Join(", ", result.FailedSources)} 응답 없음)" : "";

    /// <summary>A tile was clicked: download it if needed and make it the pending cover.</summary>
    [RelayCommand]
    private async Task PickCoverCandidateAsync(CoverCandidateViewModel? item)
    {
        var file = _candidatesFile;
        if (item is null || file is null || !IsShowing(file)) return;
        if (item.IsOriginal)
        {
            file.SetPendingCover(null);
            LoadCover();
            LoadHeader();
            return;
        }
        if (item.IsLoadingFull) return;
        try
        {
            var bytes = await item.GetFullAsync(CancellationToken.None);
            if (bytes is null)
            {
                SearchStatus = "이 커버를 받지 못했습니다. 다른 후보를 골라 보세요.";
                RemoveCandidate(item);
                return;
            }
            if (IsShowing(file)) ApplyCandidate(file, item);
        }
        catch (Exception ex)
        {
            SearchStatus = $"커버를 받지 못했습니다: {ex.Message}";
        }
    }

    private void ApplyCandidate(LibraryFileViewModel file, CoverCandidateViewModel item)
    {
        file.SetPendingCover(item.FullBytes);
        LoadCover();
        LoadHeader();
    }

    private bool IsShowing(LibraryFileViewModel file) => _selection.Count == 1 && _selection[0] == file;

    private void ShowCandidates(LibraryFileViewModel? file, List<CoverCandidateViewModel> items)
    {
        foreach (var old in _candidateItems) old.Missing -= RemoveCandidate;
        _candidatesFile = file;
        _candidateItems = items;
        foreach (var item in items) item.Missing += RemoveCandidate;
        RefreshCandidateList();
    }

    private void RemoveCandidate(CoverCandidateViewModel item)
    {
        item.Missing -= RemoveCandidate;
        _candidateItems.Remove(item);
        CoverCandidates.Remove(item);
        NotifyCandidates();
    }

    /// <summary>Shows the gallery when the selected song is the one it belongs to (it comes back
    /// when the user returns to that song) and marks which tile is the pending cover.</summary>
    private void RefreshCandidateList()
    {
        var show = _candidatesFile is { } f && IsShowing(f);
        if (!show && CoverCandidates.Count > 0) CoverCandidates.Clear();
        else if (show && CoverCandidates.Count == 0)
            foreach (var item in _candidateItems) CoverCandidates.Add(item);

        if (show)
        {
            var pending = _candidatesFile!.PendingCover;
            foreach (var item in CoverCandidates)
            {
                // "원래 커버" is what the file holds now - after a save, that's the saved one.
                if (item.IsOriginal) item.Thumbnail = _candidatesFile.CoverThumbnail;
                item.IsSelected = item.IsOriginal ? pending is null : pending is not null && ReferenceEquals(pending, item.FullBytes);
            }
        }
        NotifyCandidates();
    }

    private void NotifyCandidates()
    {
        OnPropertyChanged(nameof(HasCoverCandidates));
        OnPropertyChanged(nameof(CoverCandidatesHeader));
        OnPropertyChanged(nameof(IsRecommendedCoverShown));
        OnPropertyChanged(nameof(CoverCandidateDetail));
    }
}
