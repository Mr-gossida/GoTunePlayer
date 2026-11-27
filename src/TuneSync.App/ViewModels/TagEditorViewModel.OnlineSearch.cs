using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.App.ViewModels;

/// <summary>태그 찾기 and 가사 찾기 for one song: every source the user has on is asked, the
/// answers are shown as cards - each with where it's from and how well it fits - and the best one
/// is put in as unsaved edits, as 커버 찾기 does. Another card is one click away; nothing is
/// written until the user saves. (With several songs selected, each song still gets its best
/// answer directly.)</summary>
public sealed partial class TagEditorViewModel
{
    private readonly TagSearchService _tagSearch;
    private readonly ILyricsService _lyrics;

    /// <summary>The sources, as chips beside the search buttons (and in 설정 › 도구).</summary>
    public OnlineSourcesViewModel Sources { get; }

    // The cards fold to the one in use (or the best, when none is), so the fields and the lyrics
    // keep their room; "다른 결과 n개 보기" lays them all out to choose from.

    // ---- tags ----

    private LibraryFileViewModel? _tagCandidatesFile;
    private List<TagCandidateViewModel> _tagCandidateItems = [];

    /// <summary>What the last card put in, field by field: taken back when another card is picked
    /// (where it's still as the card put it - a value typed over it stays).</summary>
    private readonly Dictionary<TagField, string> _appliedTags = [];

    /// <summary>The cards shown: the one in use (or the best), or all once the user asked for them.</summary>
    public ObservableCollection<TagCandidateViewModel> TagCandidates { get; } = [];

    public bool HasTagCandidates => TagCandidates.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreTagCandidatesText), nameof(HasMoreTagCandidates), nameof(TagCandidatesHeader))]
    private bool _showAllTagCandidates;

    public bool HasMoreTagCandidates => _tagCandidateItems.Count > 1 && IsShowingTagCandidates;

    public string MoreTagCandidatesText => ShowAllTagCandidates ? "접기" : $"다른 결과 {_tagCandidateItems.Count - 1}개 보기";

    public string TagCandidatesHeader =>
        ShowAllTagCandidates ? $"찾은 결과 {_tagCandidateItems.Count}개"
        : IsTagCandidateApplied ? "넣은 결과"
        : "가장 잘 맞는 결과";

    /// <summary>A card's values are in the editor (so 원래 태그로 has something to take back).</summary>
    public bool IsTagCandidateApplied => _tagCandidateItems.Any(c => c.IsApplied) && _appliedTags.Count > 0;

    private bool IsShowingTagCandidates => _tagCandidatesFile is { } f && IsShowing(f);

    partial void OnShowAllTagCandidatesChanged(bool value) => RefreshTagCandidateList();

    private async Task SearchTagCandidatesAsync(LibraryFileViewModel file)
    {
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        IsSearching = true;
        var cover = AutoFillService.CoverQueryFor(file.Model);
        var query = new TagQuery(cover.Artist, cover.Title, cover.Album, cover.Duration);
        var names = OnlineSourcesViewModel.Names(Sources.TagSources);
        SearchStatus = $"{names}에서 찾는 중…";
        try
        {
            if (string.IsNullOrWhiteSpace(query.Artist) || string.IsNullOrWhiteSpace(query.Title))
            {
                SearchStatus = "아티스트와 제목을 알 수 없어 찾지 못했습니다. 두 칸을 먼저 채우거나, 파일 이름을 '아티스트 - 제목'으로 바꿔 주세요.";
                return;
            }
            var progress = new Progress<SourceProgress>(p =>
            {
                if (IsSearching && p.Done < p.Total) SearchStatus = $"{names}에서 찾는 중… ({p.Done}/{p.Total})";
            });
            var result = await Task.Run(() => _tagSearch.SearchAsync(query, Sources.DisabledTagSources, progress, ct), ct);
            var failed = result.FailedSources.Count > 0 ? $" ({string.Join(", ", result.FailedSources)} 응답 없음)" : "";
            if (result.Candidates.Count == 0)
            {
                ShowTagCandidates(null, []);
                SearchStatus = $"{string.Join(" · ", result.Sources)}에서 맞는 결과를 찾지 못했습니다.{failed} 제목·아티스트 철자를 확인해 보세요.";
                return;
            }

            var items = result.Candidates.Select((c, i) => new TagCandidateViewModel(c, file.Model.Duration, i == 0)).ToList();
            // searched again: what the last search's card put in makes way for the new pick
            if (_tagCandidatesFile == file) TakeBackTags(file);
            ShowTagCandidates(file, items);
            if (!IsShowing(file)) return; // another song was picked meanwhile: its cards wait for it
            var best = items[0];
            ApplyTagCandidate(file, best);
            var others = items.Count - 1;
            var more = others > 0 ? $" 다른 결과 {others}개 중에서 바꿀 수 있습니다." : "";
            SearchStatus = best.HasChanges
                ? $"가장 잘 맞는 {best.Source} 결과(일치 {best.ScoreText})를 넣었습니다.{more} 확인한 뒤 저장하세요.{failed}"
                : $"가장 잘 맞는 {best.Source} 결과(일치 {best.ScoreText})가 지금 태그와 같습니다.{more}{failed}";
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

    /// <summary>A card was clicked: its values go in (the last card's taken back first).</summary>
    [RelayCommand]
    private void PickTagCandidate(TagCandidateViewModel? item)
    {
        if (item is null || _tagCandidatesFile is not { } file || !IsShowing(file)) return;
        ApplyTagCandidate(file, item);
        SearchStatus = item.HasChanges
            ? $"{item.Source} 결과를 넣었습니다. 확인한 뒤 저장하세요."
            : $"{item.Source} 결과는 지금 태그와 같습니다.";
    }

    /// <summary>원래 태그로: takes back what the cards put in (what was typed over stays).</summary>
    [RelayCommand]
    private void UndoTagCandidate()
    {
        if (_tagCandidatesFile is not { } file || !IsShowing(file)) return;
        TakeBackTags(file);
        foreach (var item in _tagCandidateItems) item.IsApplied = false;
        ReloadFields();
        SearchStatus = "찾은 값을 빼고 원래 태그로 되돌렸습니다.";
    }

    [RelayCommand]
    private void ToggleMoreTagCandidates() => ShowAllTagCandidates = !ShowAllTagCandidates;

    private void ApplyTagCandidate(LibraryFileViewModel file, TagCandidateViewModel item)
    {
        TakeBackTags(file);
        foreach (var (field, value) in Changes(file, item.Candidate))
        {
            file.SetPending(field, value);
            _appliedTags[field] = value;
        }
        foreach (var candidate in _tagCandidateItems) candidate.IsApplied = candidate == item;
        ReloadFields();
    }

    private void TakeBackTags(LibraryFileViewModel file)
    {
        foreach (var (field, value) in _appliedTags)
            if (file.Effective(field) == value) file.ClearPending(field);
        _appliedTags.Clear();
    }

    /// <summary>The fields a candidate would change: those it has a value for that differs from
    /// what the file holds (capitalization alone isn't a change), the value as the field takes it.
    /// A genre only fills an empty one: the stores' are broad ("K-Pop", "Asian Music"), and one
    /// the user set (often finer) shouldn't be swapped on every search.</summary>
    private static List<(TagField Field, string Value)> Changes(LibraryFileViewModel file, TagCandidate c)
    {
        var list = new List<(TagField, string)>();
        void Check(TagField field, string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !TagFields.TryNormalize(field, value, out var normalized, out _) || normalized.Length == 0) return;
            if (!string.Equals(file.Original(field).Trim(), normalized, StringComparison.OrdinalIgnoreCase)) list.Add((field, normalized));
        }
        Check(TagField.Title, c.Title);
        Check(TagField.Artist, c.Artist);
        Check(TagField.Album, c.Album);
        Check(TagField.AlbumArtist, c.AlbumArtist);
        Check(TagField.Year, c.Year?.ToString());
        if (string.IsNullOrWhiteSpace(file.Original(TagField.Genre))) Check(TagField.Genre, c.Genre);
        Check(TagField.Track, c.Track?.ToString());
        Check(TagField.Disc, c.Disc?.ToString());
        return list;
    }

    private static string FieldName(TagField field) => field switch
    {
        TagField.Title => "제목",
        TagField.Artist => "아티스트",
        TagField.Album => "앨범",
        TagField.AlbumArtist => "앨범 아티스트",
        TagField.Year => "연도",
        TagField.Genre => "장르",
        TagField.Track => "트랙",
        TagField.Disc => "디스크",
        _ => field.ToString(),
    };

    private void ShowTagCandidates(LibraryFileViewModel? file, List<TagCandidateViewModel> items)
    {
        _tagCandidatesFile = file;
        _tagCandidateItems = items;
        _appliedTags.Clear();
        ShowAllTagCandidates = false;
        RefreshTagCandidateList();
    }

    /// <summary>Shows the cards when the selected song is the one they're for (they come back when
    /// the user returns to it), each saying what it would change now.</summary>
    private void RefreshTagCandidateList()
    {
        var shown = !IsShowingTagCandidates || _tagCandidateItems.Count == 0 ? []
            : ShowAllTagCandidates ? _tagCandidateItems
            : [_tagCandidateItems.FirstOrDefault(c => c.IsApplied) ?? _tagCandidateItems[0]];
        if (!shown.SequenceEqual(TagCandidates))
        {
            TagCandidates.Clear();
            foreach (var item in shown) TagCandidates.Add(item);
        }
        if (IsShowingTagCandidates)
        {
            foreach (var item in _tagCandidateItems)
            {
                var changes = Changes(_tagCandidatesFile!, item.Candidate);
                item.HasChanges = changes.Count > 0;
                item.Changes = changes.Count > 0 ? $"바뀌는 칸: {string.Join(" · ", changes.Select(c => FieldName(c.Field)))}" : "지금 태그와 같습니다";
            }
        }
        OnPropertyChanged(nameof(HasTagCandidates));
        OnPropertyChanged(nameof(HasMoreTagCandidates));
        OnPropertyChanged(nameof(MoreTagCandidatesText));
        OnPropertyChanged(nameof(TagCandidatesHeader));
        OnPropertyChanged(nameof(IsTagCandidateApplied));
    }

    private void ReloadFields()
    {
        foreach (var field in AllFields) LoadField(field);
        LoadHeader();
        RefreshTagCandidateList();
    }

    // ---- lyrics ----

    private LibraryFileViewModel? _lyricsCandidatesFile;
    private List<LyricsCandidateViewModel> _lyricsCandidateItems = [];

    /// <summary>The lyrics the last card put in (to tell whether the user has edited them since).</summary>
    private string? _appliedLyrics;

    public ObservableCollection<LyricsCandidateViewModel> LyricsCandidates { get; } = [];

    public bool HasLyricsCandidates => LyricsCandidates.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreLyricsCandidatesText), nameof(HasMoreLyricsCandidates), nameof(LyricsCandidatesHeader))]
    private bool _showAllLyricsCandidates;

    public bool HasMoreLyricsCandidates => _lyricsCandidateItems.Count > 1 && IsShowingLyricsCandidates;

    public string MoreLyricsCandidatesText => ShowAllLyricsCandidates ? "접기" : $"다른 가사 {_lyricsCandidateItems.Count - 1}개 보기";

    public string LyricsCandidatesHeader =>
        ShowAllLyricsCandidates ? $"찾은 가사 {_lyricsCandidateItems.Count}개"
        : _lyricsCandidateItems.Any(c => c.IsApplied) ? "넣은 가사"
        : "가장 잘 맞는 가사";

    private bool IsShowingLyricsCandidates => _lyricsCandidatesFile is { } f && IsShowing(f);

    partial void OnShowAllLyricsCandidatesChanged(bool value) => RefreshLyricsCandidateList();

    [RelayCommand]
    private void ToggleMoreLyricsCandidates() => ShowAllLyricsCandidates = !ShowAllLyricsCandidates;

    private async Task SearchLyricsCandidatesAsync(LibraryFileViewModel file)
    {
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        IsSearching = true;
        var key = AutoFillService.CoverQueryFor(file.Model);
        var names = OnlineSourcesViewModel.Names(Sources.LyricsSources);
        SearchStatus = $"{names}에서 가사를 찾는 중…";
        try
        {
            var list = new List<LyricsCandidate>();
            var certain = true;
            if (Sources.IsLyricsSourceOn(LocalLyricsSource.SourceName))
                list.AddRange(await Task.Run(() => LocalLyricsSource.Find(file.Path, key.Title, key.Artist), ct));
            var online = Sources.IsLyricsSourceOn(LrcLibLyricsService.SourceName);
            if (online && (string.IsNullOrWhiteSpace(key.Artist) || string.IsNullOrWhiteSpace(key.Title)))
            {
                online = false;
                if (list.Count == 0)
                {
                    SearchStatus = "아티스트와 제목을 알 수 없어 찾지 못했습니다. 태그 탭에서 두 칸을 먼저 채워 주세요.";
                    return;
                }
            }
            if (online)
            {
                var found = await Task.Run(() => _lyrics.SearchAsync(new LyricsQuery(key.Artist, key.Title, key.Album, key.Duration), ct), ct);
                list.AddRange(found.Candidates);
                certain = found.Certain;
            }
            if (list.Count == 0)
            {
                ShowLyricsCandidates(null, []);
                SearchStatus = $"{names}에서 맞는 가사를 찾지 못했습니다. 제목·아티스트 철자를 확인해 보세요.";
                return;
            }

            // The pick: synced lyrics kept beside the song (the user's own), else the best online
            // ones - unless it's unclear which song they're of - else plain lyrics beside it.
            var best = list.FirstOrDefault(c => c.FileName is not null && c.IsSynced)
                       ?? (certain ? list.FirstOrDefault(c => c.FileName is null) : null)
                       ?? list.FirstOrDefault(c => c.FileName is not null);
            var items = list.Select(c => new LyricsCandidateViewModel(c, file.Model.Duration, c == best)).ToList();
            if (best is not null) items = [.. items.OrderByDescending(i => i.IsRecommended)];
            ShowLyricsCandidates(file, items);
            if (!IsShowing(file)) return;
            var others = items.Count - 1;
            if (best is null)
            {
                ShowAllLyricsCandidates = items.Count > 1; // the user has to choose: all of them, laid out
                SearchStatus = $"가사 {items.Count}개를 찾았지만 어느 것이 이 곡인지 확실하지 않아 넣지 않았습니다. 미리 보기를 보고 골라 주세요.";
                return;
            }
            var chosen = items.First(i => i.IsRecommended);
            await ApplyLyricsCandidateAsync(file, chosen, ask: false);
            var more = others > 0 ? $" 다른 가사 {others}개 중에서 바꿀 수 있습니다." : "";
            SearchStatus = $"{chosen.Source}의 {chosen.KindText} 가사를 넣었습니다.{more} 확인한 뒤 저장하세요.";
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

    [RelayCommand]
    private async Task PickLyricsCandidateAsync(LyricsCandidateViewModel? item)
    {
        if (item is null || _lyricsCandidatesFile is not { } file || !IsShowing(file) || IsLyricsLoading) return;
        if (await ApplyLyricsCandidateAsync(file, item, ask: true))
            SearchStatus = $"{item.Source}의 {item.KindText} 가사를 넣었습니다. 확인한 뒤 저장하세요.";
    }

    /// <summary>Puts a card's lyrics in the editor (as an unsaved edit). Lyrics the user has typed
    /// into since aren't thrown away without asking.</summary>
    private async Task<bool> ApplyLyricsCandidateAsync(LibraryFileViewModel file, LyricsCandidateViewModel item, bool ask)
    {
        var typed = Normalize(LyricsText) != Normalize(_appliedLyrics ?? _lyricsOriginal);
        if (ask && typed && !item.IsApplied
            && !await Dialogs.ConfirmAsync("가사 바꾸기", KeepWords.Joined("직접 고친 가사가 있습니다. 고른 가사로 바꿀까요? 고친 내용은 사라집니다."), "바꾸기"))
            return false;
        if (!IsShowing(file)) return false;
        _appliedLyrics = item.Lyrics;
        LyricsText = item.Lyrics; // an unsaved edit, like typing it
        foreach (var candidate in _lyricsCandidateItems) candidate.IsApplied = candidate == item;
        return true;
    }

    private void ShowLyricsCandidates(LibraryFileViewModel? file, List<LyricsCandidateViewModel> items)
    {
        _lyricsCandidatesFile = file;
        _lyricsCandidateItems = items;
        _appliedLyrics = null;
        ShowAllLyricsCandidates = false;
        RefreshLyricsCandidateList();
    }

    private void RefreshLyricsCandidateList()
    {
        // the card whose lyrics are in the editor now (none once they're edited or reverted)
        if (IsShowingLyricsCandidates)
            foreach (var item in _lyricsCandidateItems)
                item.IsApplied = Normalize(item.Lyrics) == Normalize(LyricsText);
        var shown = !IsShowingLyricsCandidates || _lyricsCandidateItems.Count == 0 ? []
            : ShowAllLyricsCandidates ? _lyricsCandidateItems
            : [_lyricsCandidateItems.FirstOrDefault(c => c.IsApplied) ?? _lyricsCandidateItems[0]];
        if (!shown.SequenceEqual(LyricsCandidates))
        {
            LyricsCandidates.Clear();
            foreach (var item in shown) LyricsCandidates.Add(item);
        }
        OnPropertyChanged(nameof(HasLyricsCandidates));
        OnPropertyChanged(nameof(HasMoreLyricsCandidates));
        OnPropertyChanged(nameof(MoreLyricsCandidatesText));
        OnPropertyChanged(nameof(LyricsCandidatesHeader));
    }

    /// <summary>After the selection changed or a save: the cards of the song now shown.</summary>
    private void RefreshOnlineCandidates()
    {
        RefreshTagCandidateList();
        RefreshLyricsCandidateList();
    }
}
