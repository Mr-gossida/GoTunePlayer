using System.Linq;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>One auto-fill result: what would change (tags as before → after, the proposed cover
/// next to the current one, the start of the proposed lyrics), plus whether it's checked for
/// "선택 항목 적용".</summary>
public sealed partial class AutoMatchItemViewModel : ObservableObject
{
    public AutoFillProposal Proposal { get; }

    public AutoMatchItemViewModel(AutoFillProposal proposal)
    {
        Proposal = proposal;
        _isSelected = proposal.HasAnyProposal;
        ProposedCover = ImageHelper.ToBitmapImage(proposal.ProposedCoverBytes);
        CurrentCover = ImageHelper.ToBitmapImage(proposal.File.ThumbnailBytes);
        (LyricsLabel, LyricsPreview) = DescribeLyrics(proposal.ProposedLyrics);
    }

    [ObservableProperty]
    private bool _isSelected;

    public string FileName => Proposal.File.FileName;
    public string Path => Proposal.File.Path;
    public bool HasProposal => Proposal.HasAnyProposal;
    public string Status => Proposal.Status;

    public BitmapImage? ProposedCover { get; }
    public BitmapImage? CurrentCover { get; }
    public bool HasProposedCover => ProposedCover is not null;

    public string LyricsLabel { get; }
    public string LyricsPreview { get; }
    public bool HasProposedLyrics => LyricsPreview.Length > 0;

    /// <summary>One line per changed tag: "앨범: (없음) → Real Album".</summary>
    public string TagChanges
    {
        get
        {
            var tags = Proposal.File.Tags;
            var lines = new List<string>();
            void Add(string label, string current, string? proposed)
            {
                if (proposed is not null) lines.Add($"{label}: {(string.IsNullOrWhiteSpace(current) ? "(없음)" : current)} → {proposed}");
            }
            Add("제목", tags.Title, Proposal.ProposedTitle);
            Add("아티스트", tags.Artist, Proposal.ProposedArtist);
            Add("앨범", tags.Album, Proposal.ProposedAlbum);
            Add("앨범 아티스트", tags.AlbumArtist, Proposal.ProposedAlbumArtist);
            Add("트랙", tags.Track > 0 ? tags.Track.ToString() : "", Proposal.ProposedTrack?.ToString());
            Add("디스크", tags.Disc > 0 ? tags.Disc.ToString() : "", Proposal.ProposedDisc?.ToString());
            return string.Join("\n", lines);
        }
    }

    public bool HasTagChanges => TagChanges.Length > 0;

    private static (string Label, string Preview) DescribeLyrics(string? lyrics)
    {
        if (string.IsNullOrWhiteSpace(lyrics)) return ("", "");
        var synced = LrcParser.Parse(lyrics);
        var lines = synced.Count > 0
            ? synced.Select(l => l.Text)
            : lyrics.Split('\n').Select(l => l.Trim());
        var preview = string.Join("\n", lines.Where(l => l.Length > 0).Take(4));
        return (synced.Count > 0 ? "싱크 가사" : "일반 가사", preview);
    }

    public void NotifyStatusChanged() => OnPropertyChanged(nameof(Status));
}
