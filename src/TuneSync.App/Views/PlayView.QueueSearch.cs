using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TuneSync.App.ViewModels;
using TuneSync.Core.Models;

namespace TuneSync.App.Views;

// The play list's search box: it moves the selection from match to match and never filters the list.
public partial class PlayView
{
    // --- playlist search: moves the selection to matching songs, never filters the list ---

    private List<LibraryFileViewModel> _matches = [];

    /// <summary>Ctrl+F from anywhere in the player window.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        FindMatches();
        GoToMatch(_matches.Count > 0 ? 0 : -1);
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                FindMatches(); // the list may have been re-sorted or rescanned since typing
                if (_matches.Count == 0) break;
                var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var current = PlaylistBox.SelectedItem as LibraryFileViewModel;
                var at = current is null ? -1 : _matches.IndexOf(current);
                GoToMatch(at < 0 ? (step > 0 ? 0 : _matches.Count - 1) : ((at + step) % _matches.Count + _matches.Count) % _matches.Count);
                e.Handled = true;
                break;
            case Key.Down:
                FocusSelectedSong();
                e.Handled = true;
                break;
            case Key.Escape:
                SearchBox.Text = "";
                e.Handled = true;
                break;
        }
    }

    /// <summary>The songs the search finds, best first (see <see cref="TuneSync.Core.Services.SongSearch"/>).</summary>
    private void FindMatches() =>
        _matches = TuneSync.Core.Services.SongSearch.Find(_viewModel.Library.QueueOrder(), SearchBox.Text,
            file => new(file.DisplayTitle, file.Artist, file.Album, file.FileName));

    private LibraryFileViewModel? _searchHit;

    private void GoToMatch(int index)
    {
        var match = index >= 0 ? _matches[index] : null;
        if (!ReferenceEquals(_searchHit, match))
        {
            if (_searchHit is not null) _searchHit.IsSearchHit = false;
            _searchHit = match;
            if (match is not null) match.IsSearchHit = true;
        }
        UpdateBackToCurrent(); // the "지금 재생 중" button steps aside while searching
        if (match is null)
        {
            SearchCounter.Text = SearchBox.Text.Trim().Length == 0 ? "" : "없음";
            return;
        }

        _viewModel.Library.UnfoldGroupOf(match); // a hit in a folded group opens it
        PlaylistBox.SelectedItem = match;
        PlaylistBox.ScrollIntoView(match);
        Dispatcher.BeginInvoke(() => CenterInList(match), System.Windows.Threading.DispatcherPriority.Loaded);
        SearchCounter.Text = $"{index + 1}/{_matches.Count}";
    }

    /// <summary>Scrolls a song to the middle of the list (not just barely into view at an edge).</summary>
    private void CenterInList(LibraryFileViewModel song)
    {
        if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(song) is not FrameworkElement row
            || FindDescendant<ScrollViewer>(PlaylistBox) is not { } scroller) return;
        var top = row.TransformToAncestor(PlaylistBox).Transform(new Point(0, 0)).Y;
        var delta = top + row.ActualHeight / 2 - PlaylistBox.ActualHeight / 2;
        if (Math.Abs(delta) > 4) scroller.ScrollToVerticalOffset(scroller.VerticalOffset + delta);
    }

    /// <summary>↓ in the search box: continue with the keyboard in the list itself (Enter plays).</summary>
    private void FocusSelectedSong()
    {
        if (PlaylistBox.SelectedItem is not { } selected) return;
        PlaylistBox.ScrollIntoView(selected);
        PlaylistBox.UpdateLayout();
        if (PlaylistBox.ItemContainerGenerator.ContainerFromItem(selected) is ListBoxItem container)
            container.Focus();
    }
}
