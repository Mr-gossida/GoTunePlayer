using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

/// <summary>One row in the "자동 검색" preview grid: the file's current tags next to whatever
/// MusicBrainz/Cover Art Archive/LRCLIB found for it, plus the checkbox that gates whether
/// "선택 항목 적용" is allowed to touch this file at all.</summary>
public sealed class AutoMatchItem(LibraryFileItem source) : INotifyPropertyChanged
{
    public LibraryFileItem Source { get; } = source;
    public string FileName => Source.FileName;
    public string OldArtist => Source.Tags.Artist;
    public string OldTitle => Source.Tags.Title;
    public string OldAlbum => Source.Tags.Album;

    private bool _isSelected = true;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnChanged(); }
    }

    private string _status = "대기 중";
    public string Status
    {
        get => _status;
        set { _status = value; OnChanged(); }
    }

    private MusicBrainzMatch? _match;
    public MusicBrainzMatch? Match
    {
        get => _match;
        set
        {
            _match = value;
            OnChanged();
            OnChanged(nameof(NewArtist));
            OnChanged(nameof(NewTitle));
            OnChanged(nameof(NewAlbum));
            OnChanged(nameof(ArtistDiff));
            OnChanged(nameof(TitleDiff));
            OnChanged(nameof(AlbumDiff));
        }
    }

    private byte[]? _newCoverBytes;
    public byte[]? NewCoverBytes
    {
        get => _newCoverBytes;
        set
        {
            _newCoverBytes = value;
            OnChanged();
            OnChanged(nameof(NewCoverThumbnail));
            OnChanged(nameof(CoverStatus));
        }
    }

    private LyricsResult? _lyricsMatch;
    public LyricsResult? LyricsMatch
    {
        get => _lyricsMatch;
        set
        {
            _lyricsMatch = value;
            OnChanged();
            OnChanged(nameof(LyricsStatus));
        }
    }

    public string NewArtist => Match?.Artist ?? "";
    public string NewTitle => Match?.Title ?? "";
    public string NewAlbum => Match?.Album ?? "";

    public string ArtistDiff => Match is null ? "-" : $"{OldArtist}  →  {NewArtist}";
    public string TitleDiff => Match is null ? "-" : $"{OldTitle}  →  {NewTitle}";
    public string AlbumDiff => Match is null ? "-" : $"{OldAlbum}  →  {NewAlbum}";

    public BitmapImage? NewCoverThumbnail => LibraryFileItem.ToBitmapImage(NewCoverBytes);
    public string CoverStatus => Match is null ? "-" : NewCoverBytes is null ? "못 찾음" : "찾음";
    public string LyricsStatus => Match is null ? "-" : LyricsMatch?.BestLyrics is not null ? "찾음" : "못 찾음";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
