using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services.CoverSearch;

namespace TuneSync.App.ViewModels;

/// <summary>One tile in the 커버 tab's candidate gallery: a cover found online (or the song's
/// current cover, so the user can go back to it). The thumbnail loads when the tile scrolls into
/// view; the full image only when the tile is chosen.</summary>
public sealed partial class CoverCandidateViewModel : ObservableObject
{
    private readonly ICoverSearchService? _service;
    private Task<byte[]?>? _full;
    private bool _thumbRequested;

    public CoverCandidateViewModel(CoverCandidate candidate, ICoverSearchService service, bool isRecommended)
    {
        Candidate = candidate;
        _service = service;
        IsRecommended = isRecommended;
        AlbumText = string.IsNullOrWhiteSpace(candidate.Album) ? candidate.Title : candidate.Album;
        DetailText = $"{candidate.Source} · {candidate.ScorePercent}%";
        SizeText = candidate.MaxSize > 0 ? $"최대 {candidate.MaxSize}" : "";
        var year = candidate.Year is { } y ? $" ({y})" : "";
        ToolTip = $"{AlbumText}{year}\n{candidate.Title} - {candidate.Artist}\n{candidate.Source} · 일치 {candidate.ScorePercent}%";
    }

    /// <summary>The song's own cover, shown first.</summary>
    public CoverCandidateViewModel(BitmapSource? thumbnail, string sizeText)
    {
        IsOriginal = true;
        Thumbnail = thumbnail;
        AlbumText = "원래 커버";
        DetailText = "지금 파일";
        SizeText = sizeText;
        ToolTip = "저장하지 않은 새 커버를 버리고 원래 커버로 돌아갑니다";
        _thumbRequested = true;
    }

    public CoverCandidate? Candidate { get; }

    public bool IsOriginal { get; }

    public bool IsRecommended { get; }

    public string AlbumText { get; }

    public string DetailText { get; }

    public string ToolTip { get; }

    [ObservableProperty]
    private string _sizeText;

    [ObservableProperty]
    private BitmapSource? _thumbnail;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isLoadingFull;

    /// <summary>The full image once downloaded (also how the editor recognises it as the pending cover).</summary>
    public byte[]? FullBytes { get; private set; }

    /// <summary>Actual size once the full image is in, e.g. "1200×1200".</summary>
    public string? FullSize { get; private set; }

    /// <summary>Raised when the thumbnail turned out not to exist (the source had no art after all).</summary>
    public event Action<CoverCandidateViewModel>? Missing;

    /// <summary>Starts loading the thumbnail, once (the view calls this when the tile is on screen).</summary>
    public async void EnsureThumbnail()
    {
        if (_thumbRequested || _service is null || Candidate is null) return;
        _thumbRequested = true;
        var service = _service;
        var candidate = Candidate;
        var image = await Task.Run(async () => ImageHelper.ToBitmapImage(await service.GetThumbnailAsync(candidate), 240));
        if (image is null) Missing?.Invoke(this);
        else Thumbnail = image;
    }

    /// <summary>Downloads the full-size image (once; later calls share it). Null when no address answered.</summary>
    public Task<byte[]?> GetFullAsync(CancellationToken ct)
    {
        if (_service is null || Candidate is null) return Task.FromResult<byte[]?>(null);
        if (_full is { IsCanceled: false, IsFaulted: false }) return _full;
        return _full = LoadFullAsync(_service, Candidate, ct);
    }

    private async Task<byte[]?> LoadFullAsync(ICoverSearchService service, CoverCandidate candidate, CancellationToken ct)
    {
        IsLoadingFull = true;
        try
        {
            var bytes = await Task.Run(() => service.GetFullAsync(candidate, ct), ct);
            if (bytes is null) return null;
            var size = await Task.Run(() => ImageHelper.ToBitmapImage(bytes) is { } img ? $"{img.PixelWidth}×{img.PixelHeight}" : null);
            if (size is null) return null; // not an image WPF can show
            FullBytes = bytes;
            FullSize = size;
            SizeText = size;
            return bytes;
        }
        finally
        {
            IsLoadingFull = false;
        }
    }
}
