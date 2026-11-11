using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Composition root for the 태그 관리 page: groups the file browser, the auto-fill task
/// panel and playback for the view's DataContext, and owns the lyrics preview, which follows the
/// file selected in the list (not the track that happens to be playing).</summary>
public sealed partial class LibraryPageViewModel : ObservableObject
{
    private int _lyricsLoadVersion;

    public LibraryPageViewModel(LibraryViewModel library, AutoMatchViewModel autoMatch, PlaybackViewModel playback)
    {
        Library = library;
        AutoMatch = autoMatch;
        Playback = playback;
        // Lyrics just written by auto-fill should show up in the preview right away.
        autoMatch.Applied += (_, _) => _ = LoadSelectedLyricsAsync(SelectedFile);
    }

    public LibraryViewModel Library { get; }
    public AutoMatchViewModel AutoMatch { get; }
    public PlaybackViewModel Playback { get; }

    [ObservableProperty]
    private LibraryFileViewModel? _selectedFile;

    /// <summary>How many rows are selected; the details panel shows one song's details only when
    /// exactly one is.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingleSelection), nameof(ShowMultiSelection), nameof(ShowNoSelection))]
    private int _selectedCount;

    public bool ShowSingleSelection => SelectedCount == 1 && SelectedFile is not null;
    public bool ShowMultiSelection => SelectedCount > 1;
    public bool ShowNoSelection => SelectedCount == 0;

    /// <summary>Called by the view whenever the list/grid selection changes.</summary>
    public void OnSelectionChanged()
    {
        var selection = Library.Files.Where(f => f.IsSelected).ToList();
        SelectedCount = selection.Count;
        OnPropertyChanged(nameof(ShowSingleSelection));
        AutoMatch.OnSelectionChanged(selection);
    }

    [ObservableProperty]
    private string? _selectedLyricsText;

    [ObservableProperty]
    private string _selectedLyricsLabel = "파일을 선택하면 가사가 표시됩니다.";

    partial void OnSelectedFileChanged(LibraryFileViewModel? value) => _ = LoadSelectedLyricsAsync(value);

    private async Task LoadSelectedLyricsAsync(LibraryFileViewModel? file)
    {
        var version = ++_lyricsLoadVersion;

        if (file is null)
        {
            SelectedLyricsText = null;
            SelectedLyricsLabel = "파일을 선택하면 가사가 표시됩니다.";
            return;
        }

        string? lyrics;
        try
        {
            lyrics = await Task.Run(() => TagService.ReadLyrics(file.Path));
        }
        catch
        {
            lyrics = null;
        }

        if (version != _lyricsLoadVersion) return; // the selection moved on while this was loading

        if (string.IsNullOrWhiteSpace(lyrics))
        {
            SelectedLyricsText = null;
            SelectedLyricsLabel = "가사 없음";
            return;
        }

        var synced = LrcParser.Parse(lyrics);
        if (synced.Count > 0)
        {
            SelectedLyricsText = string.Join("\n", synced.Select(l => $"[{l.Time:mm\\:ss\\.ff}] {l.Text}"));
            SelectedLyricsLabel = "싱크 가사";
        }
        else
        {
            SelectedLyricsText = lyrics.Trim();
            SelectedLyricsLabel = "일반 가사";
        }
    }
}
