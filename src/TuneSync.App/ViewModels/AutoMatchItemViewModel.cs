using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.TagSearch;

namespace TuneSync.App.ViewModels;

/// <summary>One song's 통합 검색 result: what would change (tags as before → after, the proposed
/// cover next to the current one, the start of the proposed lyrics), where each part came from,
/// the other results found for it, and whether it's checked for "편집에 넣기". Picking another
/// result swaps the proposal; once the song's result is in the editor (<see cref="IsStaged"/>),
/// it swaps the unsaved edits too, as the one-song cards do.</summary>
public sealed partial class AutoMatchItemViewModel : ObservableObject
{
    private readonly TagEditorViewModel _editor;
    private readonly List<TagCandidateViewModel> _tagItems;
    private readonly List<LyricsCandidateViewModel> _lyricsItems;

    /// <summary>What went into the editor (to take back when another result is picked).</summary>
    private StagedEdit? _staged;

    public AutoMatchItemViewModel(AutoFillProposal proposal, TagEditorViewModel editor)
    {
        Proposal = proposal;
        _editor = editor;
        _isSelected = proposal.HasAnyProposal;
        // shown 52 px wide (104 on a high-DPI screen): a found cover is about 1200 px, and every
        // result kept it decoded at full size - megabytes each, decoded on the UI thread
        ProposedCover = ImageHelper.ToBitmapImage(proposal.ProposedCoverBytes, decodePixelWidth: 104);
        CurrentCover = ImageHelper.ToBitmapImage(proposal.File.ThumbnailBytes);

        var length = proposal.File.Duration;
        _tagItems = [.. proposal.TagCandidates.Select(c => new TagCandidateViewModel(c, length, c == proposal.ChosenTags))];
        foreach (var item in _tagItems)
        {
            var changes = TagChanges.Of(item.Candidate, f => TagFields.Get(proposal.File.Tags, f), proposal.ReplaceTags);
            item.HasChanges = changes.Count > 0;
            // short on the cards: the section above says it at length
            item.Changes = changes.Count > 0 ? $"바뀌는 칸: {TagChanges.Names(changes)}" : proposal.ReplaceTags ? "지금 태그와 같습니다" : "채울 빈 칸 없음";
        }
        _lyricsItems = [.. proposal.LyricsCandidates.Select(c => new LyricsCandidateViewModel(c, length, c == proposal.ChosenLyrics))];
        BuildTagRows();
        Refresh();
    }

    public AutoFillProposal Proposal { get; }

    public string FileName => Proposal.File.FileName;
    public string Path => Proposal.File.Path;
    public bool HasProposal => Proposal.HasAnyProposal;

    /// <summary>Checked for "편집에 넣기".</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>This song's result is in the editor (as unsaved edits).</summary>
    [ObservableProperty]
    private bool _isStaged;

    /// <summary>Not in the editor yet: what goes in can still be ticked off.</summary>
    public bool IsEditable => !IsStaged;

    partial void OnIsStagedChanged(bool value) => OnPropertyChanged(nameof(IsEditable));

    /// <summary>"태그 3칸 · 커버 · 싱크 가사", what's in the editor, or why nothing was found.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (TagChangeRows.Count > 0) parts.Add($"태그 {TagChangeRows.Count}칸");
            if (HasProposedCover) parts.Add("커버");
            if (HasProposedLyrics) parts.Add(LyricsLabel);
            var failed = Proposal.FailedSources.Count > 0 ? $" · {string.Join(", ", Proposal.FailedSources.Distinct())} 응답 없음" : "";
            if (parts.Count == 0)
                return (Proposal.NeedsChoice ? "확실한 결과가 없어 넣지 않았습니다. 아래에서 골라 주세요"
                    : Proposal.Status == "제안 생성됨" ? "넣을 것이 없습니다" : Proposal.Status) + failed;
            return (IsStaged ? "편집에 넣음 · " : "") + string.Join(" · ", parts)
                   + (Proposal.NeedsChoice ? " · 고를 결과가 남았습니다" : "") + failed;
        }
    }

    /// <summary>The proposal still lacks a part the user has to choose (shown in a caution color).</summary>
    public bool NeedsChoice => Proposal.NeedsChoice;

    // ---- tags ----

    /// <summary>Each proposed tag as its own before → after row, ticked off one by one.</summary>
    public ObservableCollection<AutoFillChangeViewModel> TagChangeRows { get; } = [];

    public bool HasTagSection => _tagItems.Count > 0 || TagChangeRows.Count > 0;

    /// <summary>Where the proposed tags are from ("MusicBrainz"); empty when none was picked.</summary>
    public string TagSource => Proposal.ChosenTags?.Source ?? "";

    public bool HasTagSource => TagSource.Length > 0;

    /// <summary>"일치 95%".</summary>
    public string TagScore => Proposal.ChosenTags is { } c ? $"일치 {Math.Round(c.Score * 100):0}%" : "";

    /// <summary>Why no tag rows: nothing fitted clearly, or the picked result changes nothing.</summary>
    public string TagNote =>
        IsTagUnsure ? "어느 결과가 이 곡인지 확실하지 않아 넣지 않았습니다"
        : Proposal.ChosenTags is not null && TagChangeRows.Count == 0 ? NoTagChangeText
        : "";

    public bool HasTagNote => TagNote.Length > 0;

    /// <summary>Tag results were found but none put in: one is for the user to pick.</summary>
    public bool IsTagUnsure => Proposal.ChosenTags is null && _tagItems.Count > 0;

    private string NoTagChangeText => Proposal.ReplaceTags ? "지금 태그와 같습니다" : "채울 빈 칸이 없습니다 (있는 값은 바꾸지 않습니다)";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TagCandidates), nameof(MoreTagsText))]
    private bool _showAllTags;

    /// <summary>The results to pick from, once asked for.</summary>
    public IReadOnlyList<TagCandidateViewModel> TagCandidates => ShowAllTags ? _tagItems : [];

    public bool HasMoreTags => Proposal.ChosenTags is null ? _tagItems.Count > 0 : _tagItems.Count > 1;

    public string MoreTagsText =>
        ShowAllTags ? "접기"
        : Proposal.ChosenTags is null ? $"결과 {_tagItems.Count}개 중에서 고르기"
        : $"다른 결과 {_tagItems.Count - 1}개";

    [RelayCommand]
    private void ToggleMoreTags() => ShowAllTags = !ShowAllTags;

    /// <summary>A result card was clicked: its tags become the proposal (and the edits, once in the editor).</summary>
    [RelayCommand]
    private void PickTagCandidate(TagCandidateViewModel? item)
    {
        if (item is null) return;
        Swap(() =>
        {
            Proposal.UseTags(item.Candidate);
            BuildTagRows();
        });
    }

    private void BuildTagRows()
    {
        TagChangeRows.Clear();
        var tags = Proposal.File.Tags;
        foreach (var (field, value) in StagedEdit.TagsOf(Proposal))
        {
            var now = TagFields.Get(tags, field);
            TagChangeRows.Add(new AutoFillChangeViewModel(field, TagFields.Label(field), now.Length == 0 ? "(비어 있음)" : now, value));
        }
    }

    // ---- cover ----

    public BitmapImage? ProposedCover { get; }
    public BitmapImage? CurrentCover { get; }
    public bool HasProposedCover => ProposedCover is not null;

    /// <summary>Where the proposed cover is from.</summary>
    public string CoverSource => Proposal.CoverSource ?? "";

    public bool HasCoverSource => CoverSource.Length > 0;

    [ObservableProperty]
    private bool _includeCover = true;

    // ---- lyrics ----

    public bool HasLyricsSection => _lyricsItems.Count > 0 || HasProposedLyrics;

    /// <summary>"싱크 가사" / "일반 가사".</summary>
    public string LyricsLabel { get; private set; } = "";

    /// <summary>The first lines of the proposed lyrics.</summary>
    public string LyricsPreview { get; private set; } = "";

    public bool HasProposedLyrics => LyricsPreview.Length > 0;

    public string LyricsSource => Proposal.ChosenLyrics?.Source ?? "";

    public bool HasLyricsSource => LyricsSource.Length > 0;

    /// <summary>"한국어", or empty.</summary>
    public string LyricsLanguage => Proposal.ChosenLyrics?.Language ?? "";

    public bool HasLyricsLanguage => LyricsLanguage.Length > 0;

    public string LyricsNote => IsLyricsUnsure ? "어느 가사가 이 곡인지 확실하지 않아 넣지 않았습니다" : "";

    public bool IsLyricsUnsure => Proposal.ChosenLyrics is null && _lyricsItems.Count > 0;

    public bool HasLyricsNote => LyricsNote.Length > 0;

    [ObservableProperty]
    private bool _includeLyrics = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LyricsCandidates), nameof(MoreLyricsText))]
    private bool _showAllLyrics;

    public IReadOnlyList<LyricsCandidateViewModel> LyricsCandidates => ShowAllLyrics ? _lyricsItems : [];

    public bool HasMoreLyrics => Proposal.ChosenLyrics is null ? _lyricsItems.Count > 0 : _lyricsItems.Count > 1;

    public string MoreLyricsText =>
        ShowAllLyrics ? "접기"
        : Proposal.ChosenLyrics is null ? $"가사 {_lyricsItems.Count}개 중에서 고르기"
        : $"다른 가사 {_lyricsItems.Count - 1}개";

    [RelayCommand]
    private void ToggleMoreLyrics() => ShowAllLyrics = !ShowAllLyrics;

    [RelayCommand]
    private void PickLyricsCandidate(LyricsCandidateViewModel? item)
    {
        if (item is null) return;
        Swap(() => Proposal.UseLyrics(item.Candidate));
    }

    // ---- into the editor ----

    /// <summary>What "편집에 넣기" puts in: the ticked tag rows, and the cover and lyrics if ticked.</summary>
    public StagedEdit Edit() => new(Proposal.File,
        [.. TagChangeRows.Where(r => r.IsChecked).Select(r => (r.Field, r.After))],
        IncludeCover && Proposal.ProposedCoverBytes is { Length: > 0 } cover ? cover : null,
        IncludeLyrics && !string.IsNullOrWhiteSpace(Proposal.ProposedLyrics) ? Proposal.ProposedLyrics.Trim() : null);

    /// <summary>It went into the editor as <paramref name="edit"/>.</summary>
    public void MarkStaged(StagedEdit edit)
    {
        _staged = edit;
        IsStaged = true;
        Refresh();
    }

    /// <summary>빼기: takes what this song's result put in back out of the editor.</summary>
    [RelayCommand]
    private void Unstage()
    {
        if (_staged is not { } staged) return;
        _editor.TakeBack([staged]);
        _staged = null;
        IsStaged = false;
        IsSelected = HasProposal;
        Refresh();
    }

    /// <summary>Raised after another result was picked (the search's source summary follows).</summary>
    public event Action? Swapped;

    /// <summary>Changes the proposal; in the editor already, the edits follow (what the last result
    /// put in comes out, the new one's goes in).</summary>
    private void Swap(Action change)
    {
        var staged = _staged;
        if (staged is not null) _editor.TakeBack([staged]);
        change();
        if (staged is not null)
        {
            var edit = Edit();
            _staged = null;
            IsStaged = false;
            if (!edit.IsEmpty)
            {
                _editor.Stage([edit]);
                _staged = edit;
                IsStaged = true;
            }
        }
        else
        {
            IsSelected = HasProposal;
        }
        Refresh();
        Swapped?.Invoke();
    }

    private void Refresh()
    {
        (LyricsLabel, LyricsPreview) = DescribeLyrics(Proposal.ProposedLyrics);
        foreach (var item in _tagItems) item.IsApplied = item.Candidate == Proposal.ChosenTags;
        foreach (var item in _lyricsItems) item.IsApplied = item.Candidate == Proposal.ChosenLyrics;
        OnPropertyChanged(string.Empty); // every derived property at once
    }

    private static (string Label, string Preview) DescribeLyrics(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return ("", "");
        var synced = LrcParser.Parse(lyrics);
        var lines = synced.Count > 0
            ? synced.Select(l => l.Text)
            : LrcParser.SplitLines(lyrics);
        var preview = string.Join("\n", lines.Where(l => l.Length > 0).Take(4));
        return (synced.Count > 0 ? "싱크 가사" : "일반 가사", preview);
    }
}

/// <summary>One proposed tag change in an auto-fill result.</summary>
public sealed partial class AutoFillChangeViewModel(TagField field, string label, string before, string after) : ObservableObject
{
    public TagField Field { get; } = field;
    public string Label { get; } = label;
    public string Before { get; } = before;
    public string After { get; } = after;

    [ObservableProperty]
    private bool _isChecked = true;
}
