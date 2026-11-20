using System.Windows;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using TuneSync.App.Views;
using TuneSync.Core.Models;
using Wpf.Ui.Controls;

namespace TuneSync.App;

// The playback bar's cover: a click blinks the playing song in the list and opens its card
// (big cover, details, shortcuts); a right-click only opens the card.
public partial class ShellWindow
{
    private DateTime _cardClosedAt;

    private PlayView Player => _services.GetRequiredService<PlayView>();

    private void BarCover_Click(object sender, MouseButtonEventArgs e)
    {
        if (_playback.CurrentFile is null) return;
        if (!ToggleCard()) return;
        Player.RevealCurrent(openQueue: false);
    }

    private void BarCover_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (_playback.CurrentFile is null) return;
        ToggleCard();
        e.Handled = true;
    }

    /// <summary>Opens the card, or closes it when it's open. True when it opened.</summary>
    private bool ToggleCard()
    {
        // The press that lands on the cover already closed the open card (StaysOpen=False):
        // that click means "close", not "open again".
        if (NowPlayingCard.IsOpen || DateTime.UtcNow - _cardClosedAt < TimeSpan.FromMilliseconds(250))
        {
            NowPlayingCard.IsOpen = false;
            return false;
        }
        FillCard();
        NowPlayingCard.IsOpen = true;
        return true;
    }

    private void NowPlayingCard_Closed(object? sender, EventArgs e) => _cardClosedAt = DateTime.UtcNow;

    /// <summary>The shortcuts that fit this song: no album/artist search without the tag, no
    /// Explorer for a song on a server.</summary>
    private void FillCard()
    {
        if (_playback.CurrentFile is not { } file) return;
        CardAlbumButton.Content = $"같은 앨범 곡 찾기 · {file.Album}";
        CardAlbumButton.Visibility = string.IsNullOrWhiteSpace(file.Album) ? Visibility.Collapsed : Visibility.Visible;
        CardArtistButton.Content = $"같은 아티스트 곡 찾기 · {file.Artist}";
        CardArtistButton.Visibility = string.IsNullOrWhiteSpace(file.Artist) ? Visibility.Collapsed : Visibility.Visible;
        CardDetails.Visibility = string.IsNullOrWhiteSpace(_playback.NowPlayingDetails) ? Visibility.Collapsed : Visibility.Visible;

        CardFolderButton.Visibility = RemotePath.IsRemote(file.Path) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Runs a shortcut after closing the card (so a window it opens isn't under it).</summary>
    private void FromCard(Action<TuneSync.App.ViewModels.LibraryFileViewModel> action)
    {
        NowPlayingCard.IsOpen = false;
        if (_playback.CurrentFile is { } file) action(file);
    }

    private void CardShowInList_Click(object sender, RoutedEventArgs e) => FromCard(_ => Player.RevealCurrent(openQueue: true));

    private void CardFindAlbum_Click(object sender, RoutedEventArgs e) => FromCard(f => Player.SearchQueue(f.Album ?? ""));

    private void CardFindArtist_Click(object sender, RoutedEventArgs e) => FromCard(f => Player.SearchQueue(f.Artist ?? ""));

    /// <summary>This one song in the single-song editor (as the queue's 태그 편집), not 태그 관리.</summary>
    private void CardEditTags_Click(object sender, RoutedEventArgs e) => FromCard(f => Views.SongEditorView.Open(this, [f]));

    private void CardFolder_Click(object sender, RoutedEventArgs e) => FromCard(f => PlayView.RevealInExplorer(f.Path));

    private void CardCopyPath_Click(object sender, RoutedEventArgs e) => FromCard(f => PlayView.CopyPath(f.Path));
}
