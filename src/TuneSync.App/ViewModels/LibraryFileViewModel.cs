using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Read-only display wrapper around a scanned <see cref="LibraryFile"/>, plus the
/// selection state the list/grid views need. Tag editing happens on the 태그 tab against
/// <see cref="Model"/> directly (added in a later phase) rather than through mirrored properties
/// here.</summary>
public sealed partial class LibraryFileViewModel : ObservableObject
{
    public LibraryFile Model { get; }

    public LibraryFileViewModel(LibraryFile model)
    {
        Model = model;
        CoverThumbnail = ImageHelper.ToBitmapImage(
            CoverArtService.GetThumbnail(model.CoverBytes)?.JpegBytes);
    }

    [ObservableProperty]
    private bool _isSelected;

    public string FileName => Model.FileName;
    public string Path => Model.Path;
    public string Title => Model.Tags.Title;
    public string Artist => Model.Tags.Artist;
    public string Album => Model.Tags.Album;

    public BitmapImage? CoverThumbnail { get; }

    public bool IsMissingCover => Model.IsMissingCover;
    public bool IsMissingLyrics => Model.IsMissingLyrics;
    public bool IsMissingTags => Model.IsMissingTags;
    public bool HasAnyGap => Model.HasAnyGap;

    public string CoverMark => Model.HasCover ? "✓" : "✗";
    public string LyricsMark => Model.HasLyrics ? "✓" : "✗";

    public string GapSummary
    {
        get
        {
            var parts = new List<string>();
            if (IsMissingTags) parts.Add($"태그 누락: {string.Join(", ", Model.MissingTagFields)}");
            if (IsMissingCover) parts.Add("커버 없음");
            if (IsMissingLyrics) parts.Add("가사 없음");
            return parts.Count == 0 ? "모두 채워짐" : string.Join(" / ", parts);
        }
    }
}
