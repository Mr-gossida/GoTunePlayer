using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.App.ViewModels;

public enum SaveBarState
{
    Idle,
    Pending,
    Reviewing,
    Saving,
    Saved,
}

public enum EditorTab
{
    Tags,
    Cover,
    Lyrics,
    AutoFill,
}

/// <summary>The 태그 관리 editor panel. Edits never go straight to disk: they wait on each song
/// (<see cref="LibraryFileViewModel.PendingTags"/>), so they survive changing the selection, show
/// in the list as a preview, and are saved together - after a review when several songs change -
/// with one undo for the whole save.</summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    private readonly LibraryViewModel _library;
    private readonly PlaybackViewModel _playback;
    private readonly HashSet<LibraryFileViewModel> _pending = [];
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private IReadOnlyList<LibraryFileViewModel> _selection = [];
    private List<TagEditUndo> _lastSave = [];
    private int _selectionVersion;
    private bool _suggestionsDirty = true;
    private string? _lyricsOriginal;
    private bool _lyricsLoading;

    private readonly IAutoFillService _autoFill;

    public TagEditorViewModel(LibraryViewModel library, PlaybackViewModel playback, IAutoFillService autoFill, ICoverSearchService coverSearch)
    {
        _library = library;
        _playback = playback;
        _autoFill = autoFill;
        _coverSearch = coverSearch;

        TitleField = new(this, TagField.Title);
        ArtistField = new(this, TagField.Artist, usesSuggestions: true);
        AlbumField = new(this, TagField.Album, usesSuggestions: true);
        AlbumArtistField = new(this, TagField.AlbumArtist, usesSuggestions: true);
        YearField = new(this, TagField.Year);
        GenreField = new(this, TagField.Genre, usesSuggestions: true);
        TrackField = new(this, TagField.Track);
        DiscField = new(this, TagField.Disc);
        ComposerField = new(this, TagField.Composer, usesSuggestions: true);
        AllFields = [TitleField, ArtistField, AlbumField, AlbumArtistField, YearField, GenreField, TrackField, DiscField, ComposerField];

        LibraryFileViewModel.PendingChanged += OnPendingChanged;
        _library.Files.CollectionChanged += OnLibraryFilesChanged;
        _undoTimer.Tick += (_, _) => HideUndo();
    }

    public TagFieldViewModel TitleField { get; }
    public TagFieldViewModel ArtistField { get; }
    public TagFieldViewModel AlbumField { get; }
    public TagFieldViewModel AlbumArtistField { get; }
    public TagFieldViewModel YearField { get; }
    public TagFieldViewModel GenreField { get; }
    public TagFieldViewModel TrackField { get; }
    public TagFieldViewModel DiscField { get; }
    public TagFieldViewModel ComposerField { get; }
    public IReadOnlyList<TagFieldViewModel> AllFields { get; }

    [ObservableProperty]
    private EditorTab _tab = EditorTab.Tags;

    // ---- Selection ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSingle), nameof(IsMulti), nameof(IsEmpty), nameof(HasSelection))]
    private int _count;

    public bool IsSingle => Count == 1;
    public bool IsMulti => Count > 1;
    public bool IsEmpty => Count == 0;
    public bool HasSelection => Count > 0;

    /// <summary>The songs selected in the tag manager (이름 변경 can work on them).</summary>
    public IReadOnlyList<LibraryFileViewModel> Selection => _selection;

    [ObservableProperty]
    private string _headerTitle = "";

    [ObservableProperty]
    private string _headerSubtitle = "";

    /// <summary>One song: "아티스트 · 앨범" between its title and its format line.</summary>
    [ObservableProperty]
    private string _headerDetail = "";

    [ObservableProperty]
    private BitmapImage? _headerCover;

    /// <summary>Several songs: up to three different covers, drawn as a little stack.</summary>
    public ObservableCollection<BitmapImage?> HeaderCovers { get; } = [];

    // File details (one song).
    [ObservableProperty]
    private string _fileFormat = "";

    [ObservableProperty]
    private string _fileSize = "";

    [ObservableProperty]
    private string _fileLocation = "";

    [ObservableProperty]
    private string _filePath = "";

    private bool _headerDirty;

    public void SetSelection(IReadOnlyList<LibraryFileViewModel> selection)
    {
        _selection = selection;
        _selectionVersion++;
        Count = selection.Count;
        OnPropertyChanged(nameof(Selection));
        IsReviewing = false;
        if (!IsSearching) SearchStatus = "";
        Reload();
    }

    /// <summary>Re-reads every panel from the selection (after a selection change or a save).</summary>
    public void Reload()
    {
        if (_suggestionsDirty) BuildSuggestions();
        foreach (var field in AllFields) LoadField(field);
        LoadHeader();
        LoadCover();
        _ = LoadDetailsAsync(_selectionVersion);
        _ = LoadLyricsAsync(_selectionVersion);
    }

    private void LoadField(TagFieldViewModel field)
    {
        if (_selection.Count == 0)
        {
            field.Load("", false);
            field.IsChanged = false;
            field.PreviousText = "";
            field.Distribution = "";
            return;
        }

        var values = _selection.Select(f => f.Effective(field.Field)).Distinct().ToList();
        field.Load(values.Count == 1 ? values[0] : "", values.Count > 1);
        RefreshFieldState(field);
    }

    /// <summary>Changed flag, the "이전" line and the value spread - everything but the text itself
    /// (so updating it while typing never moves the caret).</summary>
    private void RefreshFieldState(TagFieldViewModel field)
    {
        var changed = _selection.Where(f => f.PendingTags.ContainsKey(field.Field)).ToList();
        field.SelectionCount = _selection.Count;
        field.ChangedCount = changed.Count;
        field.IsChanged = changed.Count > 0;
        field.PreviousText = changed.Count == 0 ? "" : Describe(changed.Select(f => f.Original(field.Field)));
        field.Distribution = _selection.Count > 1 ? Describe(_selection.Select(f => f.Effective(field.Field))) : "";
    }

    /// <summary>"(비어 있음)" / "BTS" / "방탄소년단 10곡 · BTS 2곡 외 1가지".</summary>
    private static string Describe(IEnumerable<string> values)
    {
        var groups = values.GroupBy(v => v).OrderByDescending(g => g.Count()).ToList();
        static string Name(string v) => v.Length == 0 ? "(비어 있음)" : v;
        if (groups.Count == 1) return Name(groups[0].Key);
        var shown = string.Join(" · ", groups.Take(2).Select(g => $"{Name(g.Key)} {g.Count()}곡"));
        return groups.Count > 2 ? $"{shown} 외 {groups.Count - 2}가지" : shown;
    }

    private void LoadHeader()
    {
        HeaderCovers.Clear();
        if (_selection.Count == 1)
        {
            var file = _selection[0];
            HeaderTitle = string.IsNullOrWhiteSpace(file.ShownTitle) ? file.FileName : file.ShownTitle;
            HeaderDetail = string.Join(" · ", new[] { file.Effective(TagField.Artist), file.Effective(TagField.Album) }.Where(s => s.Length > 0));
            HeaderSubtitle = file.DurationText;
            HeaderCover = PendingCoverImage(file) ?? file.CoverThumbnail;
        }
        else if (_selection.Count > 1)
        {
            HeaderTitle = $"일괄 편집 · {_selection.Count}곡";
            HeaderSubtitle = "체크한 칸만 선택한 모든 곡에 적용됩니다";
            HeaderCover = null;
            foreach (var cover in DistinctCovers(3)) HeaderCovers.Add(cover);
        }
        else
        {
            HeaderTitle = "";
            HeaderSubtitle = "";
            HeaderCover = null;
        }
    }

    private async Task LoadDetailsAsync(int version)
    {
        if (_selection.Count != 1)
        {
            FileFormat = FileSize = FileLocation = FilePath = "";
            return;
        }
        var file = _selection[0];
        FilePath = file.IsRemote ? file.Folder + " › " + file.FileName : file.Path;
        FileLocation = file.Folder;
        FileFormat = Path.GetExtension(file.Path).TrimStart('.').ToUpperInvariant();
        FileSize = "";

        var (quality, size) = await Task.Run(() =>
        {
            AudioQuality? q = null;
            long? length = null;
            try { q = TagService.ReadAudioQuality(file.Path); } catch { /* unreadable: keep the extension */ }
            try { if (!file.IsRemote) length = new FileInfo(file.Path).Length; } catch { /* gone */ }
            return (q, length);
        });
        if (version != _selectionVersion) return;

        if (quality is not null)
        {
            FileFormat = quality.Summary;
            HeaderSubtitle = $"{quality.Summary} · {file.DurationText}";
        }
        FileSize = size is { } bytes ? $"{bytes / 1024d / 1024d:0.0} MB" : file.IsRemote ? "네트워크 파일" : "";
    }

    // ---- Editing ----

    public void OnFieldEdited(TagFieldViewModel field)
    {
        if (field.Field is TagField.Artist or TagField.Album && _selection.Count == 1) _headerDirty = true;
        if (_selection.Count == 0) return;

        // A mixed field left (or made) empty means "keep each song's own value".
        if (field.IsMixed && field.Text.Length == 0)
        {
            foreach (var file in _selection) file.ClearPending(field.Field);
            field.Error = null;
            RefreshFieldState(field);
            return;
        }

        if (!TagFields.TryNormalize(field.Field, field.Text, out var value, out var error))
        {
            field.Error = error;
            return;
        }
        field.Error = null;
        field.IsMixed = false;
        foreach (var file in _selection) file.SetPending(field.Field, value);
        RefreshFieldState(field);
        if ((field.Field == TagField.Title || _headerDirty) && _selection.Count == 1) LoadHeader();
        _headerDirty = false;
    }

    /// <summary>The batch editor's "적용" check on a field. On: what the box shows goes to every
    /// selected song - unless the songs differ and nothing is typed yet, then the caret goes to
    /// the box for a value. Off: each song keeps its own value.</summary>
    public void SetFieldApplied(TagFieldViewModel field, bool apply)
    {
        if (!apply)
        {
            RevertField(field);
            return;
        }
        if (field.IsMixed && field.Text.Length == 0)
        {
            field.RequestFocus();
            return;
        }
        field.IsMixed = false;
        OnFieldEdited(field);
    }

    public void RevertField(TagFieldViewModel field)
    {
        foreach (var file in _selection) file.ClearPending(field.Field);
        LoadField(field);
        if (field.Field == TagField.Title) LoadHeader();
    }

    /// <summary>Numbers the selected songs 1, 2, 3… in the order the list shows them.</summary>
    [RelayCommand]
    private void NumberTracks()
    {
        if (_selection.Count < 2) return;
        var order = _library.FilesView.Cast<LibraryFileViewModel>()
            .Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i);
        var ordered = _selection.OrderBy(f => order.TryGetValue(f, out var i) ? i : int.MaxValue).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].SetPending(TagField.Track, (i + 1).ToString());
        LoadField(TrackField);
    }

    private void BuildSuggestions()
    {
        _suggestionsDirty = false;
        foreach (var field in AllFields.Where(f => f.UsesSuggestions))
        {
            field.Suggestions = _library.Files
                .Select(f => f.Original(field.Field))
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }

    private void OnLibraryFilesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _suggestionsDirty = true;
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            // A batch of songs arrived, or a rescan cleared the list: drop only the edits (and the
            // undo) of songs that are no longer in it.
            if (_pending.Count == 0 && _lastSave.Count == 0) return;
            var present = new HashSet<LibraryFileViewModel>(_library.Files);
            _pending.RemoveWhere(f => !present.Contains(f));
            if (_lastSave.Count > 0 && !_library.Files.Any(f => _lastSave.Any(u => u.File == f.Model)))
            {
                _lastSave = [];
                HideUndo();
            }
            UpdatePendingSummary();
        }
        else if (e.OldItems is not null)
        {
            foreach (LibraryFileViewModel file in e.OldItems) _pending.Remove(file);
            UpdatePendingSummary();
        }
    }

    // ---- online search (each tab's "찾기" button) ----

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _searchStatus = "";

    private CancellationTokenSource? _searchCts;

    /// <summary>Looks the selected songs up online and puts what it finds in as unsaved edits -
    /// highlighted, with the previous values shown, reviewed and saved like any other edit.</summary>
    [RelayCommand]
    private Task SearchTagsAsync() => SearchAsync(new AutoFillOptions(Tags: true, Cover: false, Lyrics: false, ReplaceExisting: true, ReplaceTags: true), "태그");

    [RelayCommand]
    private Task SearchCoverAsync() =>
        IsSearching ? Task.CompletedTask
        : _selection.Count == 1 ? SearchCoverCandidatesAsync(_selection[0])
        : SearchAsync(new AutoFillOptions(Tags: false, Cover: true, Lyrics: false, ReplaceExisting: true), "커버");

    [RelayCommand]
    private Task SearchLyricsAsync() => SearchAsync(new AutoFillOptions(Tags: false, Cover: false, Lyrics: true, ReplaceExisting: true), "가사");

    [RelayCommand]
    private void CancelSearch() => _searchCts?.Cancel();

    private async Task SearchAsync(AutoFillOptions options, string what)
    {
        if (IsSearching || _selection.Count == 0) return;
        var files = _selection.ToList();
        _searchCts = new CancellationTokenSource();
        IsSearching = true;
        SearchStatus = files.Count == 1 ? $"{what}를 찾는 중…" : $"{files.Count}곡의 {what}를 찾는 중…";
        try
        {
            var done = 0;
            var progress = new Progress<string>(_ =>
            {
                if (files.Count > 1) SearchStatus = $"{files.Count}곡의 {what}를 찾는 중… ({++done}/{files.Count})";
            });
            var proposals = await _autoFill.BuildProposalsAsync(files.Select(f => f.Model), progress, _searchCts.Token, options);
            var (songs, items) = Stage(proposals);
            SearchStatus = items > 0
                ? (files.Count == 1 ? $"{what}를 찾아 채웠습니다. 확인한 뒤 저장하세요." : $"{songs}곡의 {what}를 찾아 채웠습니다. 확인한 뒤 저장하세요.")
                : files.Count == 1 ? $"{what}를 찾지 못했습니다 ({proposals.FirstOrDefault()?.Status ?? "결과 없음"})."
                : $"{files.Count}곡 모두 새로 채울 {what}를 찾지 못했습니다.";
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

    /// <summary>Search results (from a tab's button or 통합 검색) as unsaved edits on their songs.
    /// Returns how many songs and items changed.</summary>
    public (int Songs, int Items) Stage(IEnumerable<AutoFillProposal> proposals)
    {
        var rows = _library.Files.ToDictionary(f => f.Model);
        int songs = 0, items = 0;
        foreach (var p in proposals)
        {
            if (!rows.TryGetValue(p.File, out var file)) continue; // the library was rescanned meanwhile
            var before = file.PendingChangeCount;
            void Text(TagField field, string? value)
            {
                if (value is not null && TagFields.TryNormalize(field, value, out var normalized, out _)) file.SetPending(field, normalized);
            }
            Text(TagField.Title, p.ProposedTitle);
            Text(TagField.Artist, p.ProposedArtist);
            Text(TagField.Album, p.ProposedAlbum);
            Text(TagField.AlbumArtist, p.ProposedAlbumArtist);
            Text(TagField.Track, p.ProposedTrack?.ToString());
            Text(TagField.Disc, p.ProposedDisc?.ToString());
            if (p.ProposedCoverBytes is { Length: > 0 } cover) file.SetPendingCover(cover);
            if (!string.IsNullOrWhiteSpace(p.ProposedLyrics)) file.SetPendingLyrics(p.ProposedLyrics.Trim());
            var added = file.PendingChangeCount - before;
            if (added > 0 || p.ProposedCoverBytes is { Length: > 0 } || !string.IsNullOrWhiteSpace(p.ProposedLyrics))
            {
                songs++;
                items += Math.Max(added, 1);
            }
        }
        Reload();
        return (songs, items);
    }

    // ---- Cover ----

    [ObservableProperty]
    private BitmapSource? _coverImage;

    [ObservableProperty]
    private string _coverInfo = "";

    [ObservableProperty]
    private bool _isCoverPending;

    /// <summary>The drop-here hint, shown only while there's no cover to show.</summary>
    public bool ShowCoverPlaceholder => CoverImage is null && CoverTiles.Count == 0;

    partial void OnCoverImageChanged(BitmapSource? value) => OnPropertyChanged(nameof(ShowCoverPlaceholder));

    public ObservableCollection<BitmapImage?> CoverTiles { get; } = [];

    private int _coverVersion;

    /// <summary>The selection the big cover was last loaded for: while a new cover for the same
    /// songs decodes, the one shown stays up rather than flickering.</summary>
    private int _coverSelection = -1;

    /// <summary>The big cover is decoded no larger than this: it's shown at up to 340 px (a little
    /// more on a high-DPI screen).</summary>
    private const int CoverDecodeSize = 800;

    private void LoadCover()
    {
        var version = ++_coverVersion;
        var sameSongs = _coverSelection == _selectionVersion;
        _coverSelection = _selectionVersion;
        RefreshCandidateList();
        CoverTiles.Clear();
        OnPropertyChanged(nameof(ShowCoverPlaceholder));
        IsCoverPending = _selection.Any(f => f.PendingCover is not null);

        if (_selection.Count == 1)
        {
            var file = _selection[0];
            if (file.PendingCover is { } pending)
            {
                if (pending.Length == 0)
                {
                    CoverImage = null;
                    CoverInfo = "커버를 지웁니다 (저장 전)";
                    return;
                }
                if (!sameSongs)
                {
                    CoverImage = file.CoverThumbnail;
                    CoverInfo = "불러오는 중…";
                }
                var picked = CoverCandidates.FirstOrDefault(c => c.IsSelected && !c.IsOriginal);
                _ = ShowPendingAsync();
                return;

                async Task ShowPendingAsync()
                {
                    var (cover, _) = await DecodeCoverAsync(() => pending);
                    if (version != _coverVersion) return;
                    CoverImage = cover?.Image;
                    var size = cover is null ? "" : $" · {cover.SizeText}";
                    CoverInfo = picked?.Candidate is { } found
                        ? $"{(picked.IsRecommended ? "추천 커버" : "새 커버")}{size} · {found.Source} · 일치 {found.ScorePercent}%"
                        : $"새 커버{size} (저장 전)";
                }
            }
            CoverImage = file.CoverThumbnail;
            CoverInfo = file.HasCover ? "불러오는 중…" : "커버 없음";
            if (!file.HasCover) return;
            _ = ShowEmbeddedAsync();
            return;

            async Task ShowEmbeddedAsync()
            {
                var (cover, length) = await DecodeCoverAsync(() => CoverArtService.GetCover(file.Path));
                if (version != _coverVersion) return;
                if (cover is null) { CoverInfo = "커버를 읽지 못했습니다"; return; }
                CoverImage = cover.Image;
                CoverInfo = $"{cover.SizeText} · {length / 1024d:0} KB";
            }
        }
        else if (_selection.Count > 1)
        {
            if (IsCoverPending)
            {
                var pending = _selection.First(f => f.PendingCover is not null).PendingCover!;
                CoverInfo = pending.Length > 0 ? $"{_selection.Count}곡 모두 이 커버로 바뀝니다 (저장 전)" : $"{_selection.Count}곡의 커버를 지웁니다 (저장 전)";
                if (pending.Length == 0 || !sameSongs) CoverImage = null;
                if (pending.Length > 0) _ = ShowSharedAsync();
                return;

                async Task ShowSharedAsync()
                {
                    var (cover, _) = await DecodeCoverAsync(() => pending);
                    if (version == _coverVersion) CoverImage = cover?.Image;
                }
            }
            CoverImage = null;
            foreach (var cover in DistinctCovers(6)) CoverTiles.Add(cover);
            OnPropertyChanged(nameof(ShowCoverPlaceholder));
            var groups = _selection.Where(f => f.HasCover).GroupBy(f => CoverKey(f)).Count();
            var missing = _selection.Count(f => !f.HasCover);
            CoverInfo = groups switch
            {
                0 => "모두 커버 없음",
                1 when missing == 0 => "모두 같은 커버",
                _ => $"커버 {groups}가지" + (missing > 0 ? $" · 없음 {missing}곡" : ""),
            };
        }
        else
        {
            CoverImage = null;
            CoverInfo = "";
        }
    }

    /// <summary>Reads and decodes a cover for the big picture off the UI thread: a 3000px scan
    /// decoded whole costs ~36 MB and a moment of the UI each time, and a file's picture can claim
    /// to be 20000px across. The cover is null when there's none or it can't be shown; the length
    /// is its size in bytes.</summary>
    private static Task<(ImageHelper.DisplayImage? Cover, int Length)> DecodeCoverAsync(Func<byte[]?> read) => Task.Run(() =>
    {
        byte[]? bytes;
        try { bytes = read(); } catch { bytes = null; }
        return (ImageHelper.ToDisplayImage(bytes, CoverDecodeSize), bytes?.Length ?? 0);
    });

    private static string CoverKey(LibraryFileViewModel file) =>
        file.Model.ThumbnailBytes is { } bytes ? Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes)) : "";

    private IEnumerable<BitmapImage?> DistinctCovers(int max) =>
        _selection.Where(f => f.HasCover && f.CoverThumbnail is not null)
            .GroupBy(CoverKey)
            .Take(max)
            .Select(g => g.First().CoverThumbnail);

    private static BitmapImage? PendingCoverImage(LibraryFileViewModel file) =>
        file.PendingCover is { Length: > 0 } bytes ? ImageHelper.ToBitmapImage(bytes, 256) : null;

    /// <summary>What clicking the cover shows large: one song's cover, and its new cover when one
    /// is waiting to be saved. Empty for several songs or no cover at all.</summary>
    public CoverPreviewItem[] CoverPreviewItems()
    {
        if (_selection.Count != 1) return [];
        var file = _selection[0];
        var items = new List<CoverPreviewItem>();
        if (file.HasCover)
            items.Add(new CoverPreviewItem(file.PendingCover is null ? "커버" : "현재 커버",
                () => Task.Run(() => CoverArtService.GetCover(file.Path))));
        if (file.PendingCover is { Length: > 0 } pending)
            items.Add(new CoverPreviewItem("새 커버 (저장 전)", () => Task.FromResult<byte[]?>(pending)));
        return [.. items];
    }

    [RelayCommand]
    private void PickCover()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "커버 이미지 선택",
            Filter = "이미지 (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png",
        };
        if (dialog.ShowDialog() == true) SetCoverFromFile(dialog.FileName);
    }

    public void SetCoverFromFile(string path)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            Toast.Error("이미지를 열지 못했습니다", ex.Message);
            return;
        }
        if (ImageHelper.TryGetPixelSize(bytes, out var width, out var height) && width > 0 && height > 0 && !ImageLimits.IsSensibleSize(width, height))
        {
            Toast.Warning("이미지가 너무 큽니다", $"{width}×{height} 이미지는 커버로 쓸 수 없습니다. 더 작은 이미지를 선택하세요.");
            return;
        }
        if (ImageHelper.ToBitmapImage(bytes, 64) is null)
        {
            Toast.Warning("이미지가 아닙니다", "JPG 또는 PNG 이미지를 선택하세요.");
            return;
        }
        SetPendingCover(bytes);
    }

    [RelayCommand]
    private void PasteCover()
    {
        try
        {
            if (Clipboard.ContainsFileDropList()
                && Clipboard.GetFileDropList().Cast<string>().FirstOrDefault(IsImageFile) is { } file)
            {
                SetCoverFromFile(file);
                return;
            }
            if (Clipboard.GetImage() is { } image)
            {
                var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
                encoder.Frames.Add(BitmapFrame.Create(image));
                using var stream = new MemoryStream();
                encoder.Save(stream);
                SetPendingCover(stream.ToArray());
                return;
            }
        }
        catch (Exception ex)
        {
            Toast.Error("클립보드를 읽지 못했습니다", ex.Message);
            return;
        }
        Toast.Info("붙여넣을 이미지가 없습니다", "이미지를 복사한 뒤 다시 시도하세요.");
    }

    public static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png";

    [RelayCommand]
    private void RemoveCover() => SetPendingCover([]);

    [RelayCommand]
    private void RevertCover()
    {
        foreach (var file in _selection) file.SetPendingCover(null);
        LoadCover();
        LoadHeader();
    }

    private void SetPendingCover(byte[] bytes)
    {
        if (_selection.Count == 0) return;
        foreach (var file in _selection)
            file.SetPendingCover(bytes.Length == 0 && !file.HasCover ? null : bytes);
        LoadCover();
        LoadHeader();
    }

    // ---- Lyrics ----

    [ObservableProperty]
    private string _lyricsText = "";

    [ObservableProperty]
    private string _lyricsInfo = "";

    [ObservableProperty]
    private bool _isLyricsPending;

    [ObservableProperty]
    private bool _isLyricsLoading;

    private async Task LoadLyricsAsync(int version)
    {
        if (_selection.Count != 1)
        {
            SetLyricsText("");
            IsLyricsPending = false;
            var with = _selection.Count(f => f.Model.HasLyrics);
            LyricsInfo = _selection.Count == 0 ? "" : $"가사 있음 {with}곡 · 없음 {_selection.Count - with}곡";
            return;
        }

        var file = _selection[0];
        IsLyricsLoading = true;
        string? lyrics;
        try
        {
            lyrics = await Task.Run(() => TagService.ReadLyrics(file.Path));
        }
        catch
        {
            lyrics = null;
        }
        if (version != _selectionVersion) return;
        IsLyricsLoading = false;

        // Shown with ordinary line breaks (a file may use lone CRs, which run together otherwise).
        _lyricsOriginal = LrcParser.NormalizeNewlines(lyrics).Replace("\n", Environment.NewLine);
        SetLyricsText(file.PendingLyrics ?? _lyricsOriginal);
        UpdateLyricsState();
    }

    private void SetLyricsText(string text)
    {
        _lyricsLoading = true;
        LyricsText = text;
        _lyricsLoading = false;
        OnPropertyChanged(nameof(CanMakeSynced));
        OnPropertyChanged(nameof(CanEditSynced));
    }

    partial void OnIsLyricsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanMakeSynced));
        OnPropertyChanged(nameof(CanEditSynced));
    }

    partial void OnLyricsTextChanged(string value)
    {
        if (_lyricsLoading || _selection.Count != 1 || IsLyricsLoading) return;
        var file = _selection[0];
        file.SetPendingLyrics(Normalize(value) == Normalize(_lyricsOriginal) ? null : value.Trim());
        UpdateLyricsState();
    }

    private static string Normalize(string? text) => LrcParser.NormalizeNewlines(text).Trim();

    private void UpdateLyricsState()
    {
        if (_selection.Count != 1) return;
        IsLyricsPending = _selection[0].PendingLyrics is not null;
        var text = Normalize(LyricsText);
        LyricsInfo = text.Length == 0 ? "가사 없음"
            : LrcParser.Parse(text).Count > 0 ? "싱크 가사 (LRC)"
            : "일반 가사";
        if (IsLyricsPending) LyricsInfo += " · 저장 전";
        OnPropertyChanged(nameof(CanMakeSynced));
        OnPropertyChanged(nameof(CanEditSynced));
    }

    /// <summary>One song whose lyrics (as shown, edits included) are plain text.</summary>
    public bool CanMakeSynced => _selection.Count == 1 && !IsLyricsLoading
        && Normalize(LyricsText).Length > 0 && LrcParser.Parse(Normalize(LyricsText)).Count == 0;

    /// <summary>One song whose lyrics (as shown, edits included) are already synced: "싱크 편집".</summary>
    public bool CanEditSynced => _selection.Count == 1 && !IsLyricsLoading && LrcParser.Parse(Normalize(LyricsText)).Count > 0;

    /// <summary>Opens the sync editor on the shown lyrics (plain ones to sync, synced ones to fix);
    /// once synced lyrics are saved, any unsaved lyrics edit here is replaced by them.</summary>
    [RelayCommand]
    private void MakeSynced()
    {
        if (!CanMakeSynced && !CanEditSynced) return;
        var file = _selection[0];
        var owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        if (LyricsSyncWindow.Run(owner, file, LyricsText) is not { } undo) return;
        file.SetPendingLyrics(null);
        Reload();

        // The sync editor's 저장 already wrote the file. Say so in this editor's save bar (with its
        // undo), or its own 저장 button reads as a second step still to do.
        _lastSave = [undo];
        UndoText = "싱크 가사를 저장했습니다";
        CanUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void RevertLyrics()
    {
        if (_selection.Count != 1) return;
        _selection[0].SetPendingLyrics(null);
        SetLyricsText(_lyricsOriginal ?? "");
        UpdateLyricsState();
    }

    // ---- Pending summary, save, review, undo ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPending), nameof(BarState))]
    private int _pendingFileCount;

    /// <summary>Which line the save bar at the bottom of the panel shows.</summary>
    public SaveBarState BarState =>
        IsSaving ? SaveBarState.Saving
        : IsReviewing ? SaveBarState.Reviewing
        : HasPending ? SaveBarState.Pending
        : CanUndo ? SaveBarState.Saved
        : SaveBarState.Idle;

    [ObservableProperty]
    private int _pendingItemCount;

    public bool HasPending => PendingFileCount > 0;

    [ObservableProperty]
    private string _pendingSummary = "";

    [ObservableProperty]
    private string _saveButtonText = "저장";

    [ObservableProperty]
    private string _confirmSaveText = "저장";

    private void OnPendingChanged(LibraryFileViewModel file)
    {
        if (_ownFiles is not null && !_ownFiles.Contains(file)) return;
        if (file.HasPendingChanges) _pending.Add(file);
        else _pending.Remove(file);
        if (file.HasPendingChanges) HideUndo();
        UpdatePendingSummary();
    }

    private void UpdatePendingSummary()
    {
        PendingFileCount = _pending.Count;
        PendingItemCount = _pending.Sum(f => f.PendingChangeCount);
        PendingSummary = PendingFileCount switch
        {
            0 => "",
            1 => $"변경 {PendingItemCount}개",
            _ => $"{PendingFileCount}곡 · 변경 {PendingItemCount}개",
        };
        SaveButtonText = PendingFileCount > 1 ? "검토 후 저장" : "저장";
        ConfirmSaveText = $"{PendingFileCount}곡 저장";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarState))]
    private bool _isSaving;

    [ObservableProperty]
    private string _saveProgress = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarState))]
    private bool _isReviewing;

    [ObservableProperty]
    private string _reviewTitle = "";

    [ObservableProperty]
    private string _reviewMore = "";

    public ObservableCollection<ReviewFileViewModel> ReviewItems { get; } = [];

    [ObservableProperty]
    private string _undoText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BarState))]
    private bool _canUndo;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving || _pending.Count == 0) return;
        if (AllFields.FirstOrDefault(f => f.HasError) is { } invalid)
        {
            Tab = EditorTab.Tags;
            Toast.Warning("입력값을 확인하세요", $"{invalid.Label}: {invalid.Error}");
            return;
        }
        if (_pending.Count > 1 && ReviewSeveral)
        {
            ShowReview();
            return;
        }
        await CommitAsync(OrderedPending());
    }

    /// <summary>Saving several songs shows the review first (태그 관리). The one-song editor, which
    /// only ever has the songs it stepped through, saves them straight away.</summary>
    public bool ReviewSeveral { get; set; } = true;

    // ---- the one-song editor's own instance (SongEditorView) ----

    private HashSet<LibraryFileViewModel>? _ownFiles;

    /// <summary>Tracks (and saves, and reverts) only these songs' edits - not ones made in 태그 관리
    /// meanwhile.</summary>
    public void LimitTo(IEnumerable<LibraryFileViewModel> files)
    {
        _ownFiles = [.. files];
        _pending.Clear();
        foreach (var file in _ownFiles.Where(f => f.HasPendingChanges)) _pending.Add(file);
        UpdatePendingSummary();
    }

    /// <summary>Stops listening to the library (the editor's window closed).</summary>
    public void Detach()
    {
        LibraryFileViewModel.PendingChanged -= OnPendingChanged;
        _library.Files.CollectionChanged -= OnLibraryFilesChanged;
        _undoTimer.Stop();
        _searchCts?.Cancel();
    }

    /// <summary>The songs with unsaved edits this editor answers for.</summary>
    public IReadOnlyCollection<LibraryFileViewModel> PendingFiles => _pending;

    private void ShowReview()
    {
        const int maxShown = 200;
        var files = OrderedPending();
        ReviewItems.Clear();
        foreach (var file in files.Take(maxShown)) ReviewItems.Add(new ReviewFileViewModel(file));
        ReviewTitle = $"{files.Count}곡 · 변경 {PendingItemCount}개";
        ReviewMore = files.Count > maxShown ? $"외 {files.Count - maxShown}곡" : "";
        IsReviewing = true;
    }

    private List<LibraryFileViewModel> OrderedPending()
    {
        var order = _library.Files.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i);
        return [.. _pending.OrderBy(f => order.TryGetValue(f, out var i) ? i : int.MaxValue)];
    }

    [RelayCommand]
    private void CancelReview() => IsReviewing = false;

    [RelayCommand]
    private async Task ConfirmSaveAsync()
    {
        IsReviewing = false;
        await CommitAsync(OrderedPending());
    }

    private async Task CommitAsync(List<LibraryFileViewModel> files)
    {
        IsSaving = true;
        var undo = new List<TagEditUndo>();
        var failed = new List<string>();
        var resume = await _playback.ReleaseIfPlayingAsync(files.Select(f => f.Path));
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                SaveProgress = files.Count > 1 ? $"저장 중… {i + 1}/{files.Count}" : "저장 중…";
                var tags = file.PendingTags.Count > 0 ? TagFields.Apply(file.Model.Tags, file.PendingTags) : null;
                var cover = file.PendingCover;
                var lyrics = file.PendingLyrics;
                try
                {
                    undo.Add(await Task.Run(() => TagEditService.Write(file.Model, tags, cover, lyrics)));
                    file.ClearAllPending();
                }
                catch (Exception ex)
                {
                    failed.Add($"{file.FileName}: {ex.Message}");
                }
            }
        }
        finally
        {
            resume?.Invoke(null);
            IsSaving = false;
            SaveProgress = "";
        }

        _library.RefreshRows(undo.Select(u => u.File));
        _suggestionsDirty = true;
        Reload();

        if (undo.Count > 0)
        {
            _lastSave = undo;
            UndoText = undo.Count == 1 ? "저장했습니다" : $"{undo.Count}곡 저장했습니다";
            CanUndo = true;
            _undoTimer.Stop();
            _undoTimer.Start();
        }
        if (failed.Count > 0)
            Toast.Warning($"{failed.Count}곡을 저장하지 못했습니다", string.Join(Environment.NewLine, failed.Take(3)) + (failed.Count > 3 ? $"{Environment.NewLine}외 {failed.Count - 3}곡" : "") + $"{Environment.NewLine}변경 내용은 그대로 남아 있습니다.");
        Saved?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after a save or undo wrote files (the lyrics preview etc. reload).</summary>
    public event EventHandler? Saved;

    [RelayCommand]
    private async Task UndoAsync()
    {
        if (_lastSave.Count == 0 || IsSaving) return;
        var entries = _lastSave;
        HideUndo();
        IsSaving = true;
        SaveProgress = "되돌리는 중…";
        var failed = 0;
        var resume = await _playback.ReleaseIfPlayingAsync(entries.Select(e => e.File.Path));
        try
        {
            foreach (var entry in entries)
            {
                try { await Task.Run(() => TagEditService.Undo(entry)); }
                catch { failed++; }
            }
        }
        finally
        {
            resume?.Invoke(null);
            IsSaving = false;
            SaveProgress = "";
        }
        _library.RefreshRows(entries.Select(e => e.File));
        _suggestionsDirty = true;
        Reload();
        if (failed > 0) Toast.Warning("일부 곡을 되돌리지 못했습니다", $"{failed}곡은 저장한 상태로 남아 있습니다.");
        else Toast.Info("저장을 취소했습니다", entries.Count == 1 ? "이전 값으로 되돌렸습니다." : $"{entries.Count}곡을 이전 값으로 되돌렸습니다.");
        Saved?.Invoke(this, EventArgs.Empty);
    }

    private void HideUndo()
    {
        _undoTimer.Stop();
        CanUndo = false;
    }

    /// <summary>Drops every unsaved edit (asks first when it's more than the selection).</summary>
    [RelayCommand]
    private async Task RevertAllAsync()
    {
        if (_pending.Count == 0) return;
        if (_pending.Count > 1
            && !await Dialogs.ConfirmAsync("변경 내용 되돌리기", $"저장하지 않은 {_pending.Count}곡의 변경 내용을 모두 되돌릴까요?", "모두 되돌리기"))
            return;
        foreach (var file in _pending.ToList()) file.ClearAllPending();
        IsReviewing = false;
        Reload();
    }
}

/// <summary>One song in the save review: each change as before → after. A lyrics change opens
/// into the lyrics themselves, before and after side by side (<see cref="ToggleCompareCommand"/>).</summary>
public sealed partial class ReviewFileViewModel : ObservableObject
{
    private readonly LibraryFileViewModel _file;

    public ReviewFileViewModel(LibraryFileViewModel file)
    {
        _file = file;
        Title = string.IsNullOrWhiteSpace(file.Title) ? file.FileName : file.Title;
        Subtitle = file.Artist;
        foreach (var (field, value) in file.PendingTags.OrderBy(p => p.Key))
            Changes.Add(new ReviewChange(TagFields.Label(field), Show(file.Original(field)), Show(value), value.Length == 0));
        if (file.PendingCover is { } cover)
            Changes.Add(new ReviewChange("커버", file.HasCover ? "있음" : "없음", cover.Length > 0 ? "새 이미지" : "지움", cover.Length == 0));
        if (file.PendingLyrics is { } lyrics)
        {
            var before = file.Model.HasLyrics ? (file.Model.IsLyricsSynced ? "싱크 가사" : "일반 가사") : "없음";
            var after = lyrics.Length == 0 ? "지움" : LrcParser.Parse(lyrics).Count > 0 ? "싱크 가사" : "일반 가사";
            if (after == before) after = "내용 수정"; // same kind: say what happens to it
            Changes.Add(new ReviewChange("가사", before, after, lyrics.Length == 0) { IsLyrics = true });
        }
    }

    // ---- 가사 비교: 교체 전 / 교체 후 ----

    [ObservableProperty]
    private bool _isCompareOpen;

    [ObservableProperty]
    private bool _isCompareLoading;

    [ObservableProperty]
    private IReadOnlyList<LyricsDiffLine> _beforeLines = [];

    [ObservableProperty]
    private IReadOnlyList<LyricsDiffLine> _afterLines = [];

    [ObservableProperty]
    private string _compareSummary = "";

    public string CompareTitle => $"{Title} · 가사 비교";

    private bool _compareLoaded;

    [RelayCommand]
    private async Task ToggleCompareAsync()
    {
        IsCompareOpen = !IsCompareOpen;
        if (IsCompareOpen) await LoadCompareAsync();
    }

    /// <summary>Reads the lyrics now in the file (off the UI thread: it may be on a NAS) and lines
    /// them up with the edited ones.</summary>
    public async Task LoadCompareAsync()
    {
        if (_compareLoaded || IsCompareLoading) return;
        IsCompareLoading = true;
        var after = _file.PendingLyrics ?? "";
        var path = _file.Path;
        var (before, diff) = await Task.Run(() =>
        {
            string? current;
            try { current = TagService.ReadLyrics(path); }
            catch { current = null; }
            return (current ?? "", LyricsDiff.Compare(current, after));
        });
        BeforeLines = diff.Before;
        AfterLines = diff.After;
        CompareSummary = after.Length == 0 ? "가사를 지웁니다"
            : before.Trim().Length == 0 ? $"가사가 없던 곡에 {diff.After.Count}줄을 넣습니다"
            : diff.Removed == 0 && diff.Added == 0
                ? (LrcParser.Parse(after).Count > 0 && LrcParser.Parse(before).Count == 0
                    ? "가사 내용은 같고, 줄마다 시간(싱크)이 붙습니다"
                    : "가사 내용은 같고, 시간이나 줄 간격만 달라집니다")
            : $"달라지는 줄: 빠지는 줄 {diff.Removed} · 새 줄 {diff.Added}";
        IsCompareLoading = false;
        _compareLoaded = true;
    }

    private static string Show(string value) => value.Length == 0 ? "(비어 있음)" : value;

    public string Title { get; }
    public string Subtitle { get; }
    public List<ReviewChange> Changes { get; } = [];
}

public sealed record ReviewChange(string Label, string Before, string After, bool IsRemoval)
{
    /// <summary>The lyrics row: it opens the before/after comparison.</summary>
    public bool IsLyrics { get; init; }
}
