using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;

namespace TuneSync.App.ViewModels;

/// <summary>One row in the auto-fill results grid: a proposal plus whether the user has it
/// checked for "선택 항목 적용".</summary>
public sealed partial class AutoMatchItemViewModel : ObservableObject
{
    public AutoFillProposal Proposal { get; }

    public AutoMatchItemViewModel(AutoFillProposal proposal)
    {
        Proposal = proposal;
    }

    [ObservableProperty]
    private bool _isSelected = true;

    public string FileName => Proposal.File.FileName;
    public bool HasProposal => Proposal.HasAnyProposal;

    public string Status => Proposal.Status;

    public string ChangeSummary
    {
        get
        {
            var parts = new List<string>();
            if (Proposal.ProposedTitle is not null) parts.Add($"제목: {Proposal.ProposedTitle}");
            if (Proposal.ProposedArtist is not null) parts.Add($"아티스트: {Proposal.ProposedArtist}");
            if (Proposal.ProposedAlbum is not null) parts.Add($"앨범: {Proposal.ProposedAlbum}");
            if (Proposal.ProposedAlbumArtist is not null) parts.Add($"앨범 아티스트: {Proposal.ProposedAlbumArtist}");
            if (Proposal.ProposedTrack is not null) parts.Add($"트랙: {Proposal.ProposedTrack}");
            if (Proposal.ProposedDisc is not null) parts.Add($"디스크: {Proposal.ProposedDisc}");
            if (Proposal.ProposedCoverBytes is { Length: > 0 }) parts.Add("커버 추가");
            if (!string.IsNullOrWhiteSpace(Proposal.ProposedLyrics)) parts.Add("가사 추가");
            return parts.Count == 0 ? "-" : string.Join(" / ", parts);
        }
    }

    public void NotifyStatusChanged() => OnPropertyChanged(nameof(Status));
}
