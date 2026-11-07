using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

/// <summary>One row in the unified file list. Carries everything the four task-panel tabs
/// (cover/rename/lyrics/tags) need so none of them has to re-scan the folder on its own.</summary>
public sealed class LibraryFileItem(string path, TrackTags tags, byte[]? coverBytes, bool hasLyrics) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);
    public TrackTags Tags { get; set; } = tags;

    private byte[]? _coverBytes = coverBytes;
    public byte[]? CoverBytes
    {
        get => _coverBytes;
        set
        {
            _coverBytes = value;
            OnChanged();
            OnChanged(nameof(HasCoverLabel));
            OnChanged(nameof(Thumbnail));
        }
    }

    private bool _hasLyrics = hasLyrics;
    public bool HasLyrics
    {
        get => _hasLyrics;
        set
        {
            _hasLyrics = value;
            OnChanged();
            OnChanged(nameof(HasLyricsLabel));
        }
    }

    public LyricsResult? FoundLyrics { get; set; }

    private string _lyricsSearchLabel = "미검색";
    public string LyricsSearchLabel
    {
        get => _lyricsSearchLabel;
        set { _lyricsSearchLabel = value; OnChanged(); }
    }

    public string HasCoverLabel => CoverBytes is null ? "없음" : "있음";
    public string HasLyricsLabel => HasLyrics ? "있음" : "없음";

    public BitmapImage? Thumbnail => ToBitmapImage(CoverBytes);

    public static BitmapImage? ToBitmapImage(byte[]? bytes)
    {
        if (bytes is null) return null;
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(bytes);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
