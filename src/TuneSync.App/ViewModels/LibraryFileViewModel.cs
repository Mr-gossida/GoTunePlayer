using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>Read-only display wrapper around a scanned <see cref="LibraryFile"/>, plus the
/// selection state the list/grid views need.</summary>
public sealed partial class LibraryFileViewModel : ObservableObject
{
    public LibraryFile Model { get; }

    public LibraryFileViewModel(LibraryFile model)
    {
        Model = model;
        // ThumbnailBytes was already resized/re-encoded small during the background scan
        // (MediaLibraryScanner), so this is a cheap decode of an already-tiny image rather than
        // the full embedded cover - keeps this constructor (run on the UI thread as files stream
        // in) from blocking on GDI+ work per file.
        _coverThumbnail = ImageHelper.ToBitmapImage(model.ThumbnailBytes);
    }

    /// <summary>Mirrors the list's selection, so "선택한 파일" actions (auto-fill) see it.</summary>
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private BitmapImage? _coverThumbnail;

    /// <summary>This is the track the player has loaded (the queue marks it).</summary>
    [ObservableProperty]
    private bool _isNowPlaying;

    /// <summary>Position in the "다음에 재생" list (1 = plays next); 0 when not reserved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUpNext))]
    private int _upNextOrder;

    public bool IsUpNext => UpNextOrder > 0;

    public string FileName => Model.FileName;
    public string Path => Model.Path;
    public string Title => Model.Tags.Title;
    public string Artist => Model.Tags.Artist;
    public string Album => Model.Tags.Album;
    public string AlbumArtist => Model.Tags.AlbumArtist;
    public string Genre => Model.Tags.Genre;
    public string YearText => Model.Tags.Year > 0 ? Model.Tags.Year.ToString() : "";
    public string TrackText => Model.Tags switch
    {
        { Track: 0 } => "",
        { Disc: > 0 } t => $"{t.Track} (디스크 {t.Disc})",
        var t => t.Track.ToString(),
    };

    /// <summary>Falls back to the filename when the title tag is empty, so a missing tag never
    /// shows as a blank row/title.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? FileName : Title;

    // Playlist sort/group keys.
    public TimeSpan Duration => Model.Duration;
    /// <summary>The folder (group key and header). A network song's folder reads
    /// "거실 NAS › Music/KPop" rather than its internal remote:// address.</summary>
    public string Folder => TuneSync.Core.Models.RemotePath.TryParse(Path, out var sourceId, out var remotePath)
        ? $"{RemoteSourceName?.Invoke(sourceId) ?? "네트워크"} › {remotePath[..Math.Max(0, remotePath.LastIndexOf('/'))].TrimStart('/')}"
        : System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>Source id → its display name (set by the app's network library).</summary>
    public static Func<string, string?>? RemoteSourceName { get; set; }

    public bool IsRemote => TuneSync.Core.Models.RemotePath.IsRemote(Path);
    public string ArtistGroup => string.IsNullOrWhiteSpace(Artist) ? "(알 수 없는 아티스트)" : Artist;
    public string AlbumGroup => string.IsNullOrWhiteSpace(Album) ? "(알 수 없는 앨범)" : Album;

    public string DurationText => Model.Duration.TotalHours >= 1
        ? Model.Duration.ToString(@"h\:mm\:ss")
        : Model.Duration.ToString(@"mm\:ss");

    public bool IsMissingCover => Model.IsMissingCover;
    public bool IsMissingLyrics => Model.IsMissingLyrics;
    public bool IsMissingTags => Model.IsMissingTags;
    public bool HasAnyGap => Model.HasAnyGap;

    public bool HasCover => Model.HasCover;

    public string LyricsMark => Model switch
    {
        { HasLyrics: false } => "없음",
        { IsLyricsSynced: true } => "싱크",
        _ => "일반",
    };

    /// <summary>Missing tag fields in Korean ("앨범", "제목, 아티스트"); empty when all are filled.
    /// Cover and lyrics have their own columns, so they aren't repeated here.</summary>
    public string MissingTagsText => string.Join(", ", Model.MissingTagFields.Select(name => name switch
    {
        "Title" => "제목",
        "Artist" => "아티스트",
        "Album" => "앨범",
        _ => name,
    }));

    public string GapSummary
    {
        get
        {
            var parts = new List<string>();
            if (IsMissingTags) parts.Add($"태그 누락: {MissingTagsText}");
            if (IsMissingCover) parts.Add("커버 없음");
            if (IsMissingLyrics) parts.Add("가사 없음");
            return parts.Count == 0 ? "모두 채워짐" : string.Join(" / ", parts);
        }
    }

    // ---- Unsaved edits (태그 관리 editor) ----
    // Edits wait here until saved, per song, so they survive moving the selection around and the
    // list can preview them. Values are already normalized (TagFields.TryNormalize).

    private readonly Dictionary<TagField, string> _pendingTags = [];

    /// <summary>Raised whenever a song's unsaved edits change (the editor keeps the global count).</summary>
    public static event Action<LibraryFileViewModel>? PendingChanged;

    public IReadOnlyDictionary<TagField, string> PendingTags => _pendingTags;

    /// <summary>New cover waiting to be saved; an empty array means "remove the cover".</summary>
    public byte[]? PendingCover { get; private set; }

    /// <summary>New lyrics waiting to be saved; "" means "remove the lyrics".</summary>
    public string? PendingLyrics { get; private set; }

    public bool HasPendingChanges => _pendingTags.Count > 0 || PendingCover is not null || PendingLyrics is not null;

    public int PendingChangeCount => _pendingTags.Count + (PendingCover is null ? 0 : 1) + (PendingLyrics is null ? 0 : 1);

    public string Original(TagField field) => TagFields.Get(Model.Tags, field);

    /// <summary>The value the song will have after saving: the unsaved edit, else what's in the file.</summary>
    public string Effective(TagField field) => _pendingTags.TryGetValue(field, out var value) ? value : Original(field);

    public void SetPending(TagField field, string value)
    {
        bool changed;
        if (value == Original(field))
        {
            changed = _pendingTags.Remove(field);
        }
        else
        {
            changed = !_pendingTags.TryGetValue(field, out var old) || old != value;
            _pendingTags[field] = value;
        }
        if (changed) NotifyPending();
    }

    public void ClearPending(TagField field)
    {
        if (_pendingTags.Remove(field)) NotifyPending();
    }

    public void SetPendingCover(byte[]? cover)
    {
        PendingCover = cover;
        NotifyPending();
    }

    public void SetPendingLyrics(string? lyrics)
    {
        PendingLyrics = lyrics;
        NotifyPending();
    }

    public void ClearAllPending()
    {
        if (!HasPendingChanges) return;
        _pendingTags.Clear();
        PendingCover = null;
        PendingLyrics = null;
        NotifyPending();
    }

    private void NotifyPending()
    {
        OnPropertyChanged(nameof(HasPendingChanges));
        OnPropertyChanged(nameof(ShownTitle));
        OnPropertyChanged(nameof(ShownArtist));
        OnPropertyChanged(nameof(ShownAlbum));
        OnPropertyChanged(nameof(IsTitlePending));
        OnPropertyChanged(nameof(IsArtistPending));
        OnPropertyChanged(nameof(IsAlbumPending));
        PendingChanged?.Invoke(this);
    }

    // List columns show the value-to-be (in accent italics while unsaved).
    public string ShownTitle => Effective(TagField.Title);
    public string ShownArtist => Effective(TagField.Artist);
    public string ShownAlbum => Effective(TagField.Album);
    public bool IsTitlePending => _pendingTags.ContainsKey(TagField.Title);
    public bool IsArtistPending => _pendingTags.ContainsKey(TagField.Artist);
    public bool IsAlbumPending => _pendingTags.ContainsKey(TagField.Album);

    /// <summary>Re-reads everything from <see cref="Model"/> after it was changed in place.</summary>
    public void Refresh()
    {
        CoverThumbnail = ImageHelper.ToBitmapImage(Model.ThumbnailBytes);
        OnPropertyChanged(string.Empty);
    }
}
