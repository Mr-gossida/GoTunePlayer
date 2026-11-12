using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;

namespace TuneSync.App.ViewModels;

/// <summary>Composition root for the player screen: now-playing cover/info, lyrics and the queue
/// are all driven by the same Playback/Library singletons the tag manager uses, so playing a
/// track from either keeps both in sync. Also runs the "가사 찾기" shortcut shown over the
/// no-lyrics notice.</summary>
public sealed partial class PlayPageViewModel : ObservableObject
{
    private readonly ILyricsService _lyrics;
    private CancellationTokenSource? _findCts;
    private LyricsResult? _found;
    private LibraryFileViewModel? _foundFor;

    public PlayPageViewModel(PlaybackViewModel playback, LibraryViewModel library, ILyricsService lyrics)
    {
        Playback = playback;
        Library = library;
        _lyrics = lyrics;
        playback.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackViewModel.CurrentFile)) ResetFinder();
        };
    }

    public PlaybackViewModel Playback { get; }
    public LibraryViewModel Library { get; }

    /// <summary>Idle → Searching → Found / NotFound.</summary>
    [ObservableProperty]
    private LyricsFindState _findState;

    [ObservableProperty]
    private string _foundSummary = "";

    [ObservableProperty]
    private string _foundPreview = "";

    [ObservableProperty]
    private bool _isSaving;

    private void ResetFinder()
    {
        _findCts?.Cancel();
        _found = null;
        _foundFor = null;
        FindState = LyricsFindState.Idle;
        FoundSummary = "";
        FoundPreview = "";
    }

    [RelayCommand]
    private async Task FindLyricsAsync()
    {
        if (Playback.CurrentFile is not { } file) return;
        _findCts?.Cancel();
        var cts = new CancellationTokenSource();
        _findCts = cts;
        FindState = LyricsFindState.Searching;

        var title = string.IsNullOrWhiteSpace(file.Title)
            ? System.IO.Path.GetFileNameWithoutExtension(file.FileName)
            : file.Title;
        LyricsResult? result;
        try
        {
            result = await _lyrics.FindAsync(new LyricsQuery(file.Artist, title, file.Album, file.Duration), cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            FindState = LyricsFindState.Idle;
            Toast.Error("가사를 찾지 못했습니다", ex.Message);
            return;
        }
        if (cts.IsCancellationRequested || file != Playback.CurrentFile) return;

        if (result?.BestLyrics is not { } text)
        {
            FindState = LyricsFindState.NotFound;
            return;
        }

        _found = result;
        _foundFor = file;
        var synced = !string.IsNullOrWhiteSpace(result.SyncedLyrics);
        FoundSummary = $"{(synced ? "싱크 가사" : "일반 가사")} · {result.ArtistName} - {result.TrackName}";
        var lines = synced
            ? LrcParser.Parse(text).Select(l => l.Text)
            : text.Split('\n').Select(l => l.Trim());
        FoundPreview = string.Join("\n", lines.Where(l => l.Length > 0).Take(6));
        FindState = LyricsFindState.Found;
    }

    /// <summary>Writes the found lyrics into the playing file (released for the write, then
    /// resumed where it was) and shows them right away.</summary>
    [RelayCommand]
    private async Task SaveFoundLyricsAsync()
    {
        if (_found?.BestLyrics is not { } text || _foundFor is not { } file || IsSaving) return;
        var synced = !string.IsNullOrWhiteSpace(_found.SyncedLyrics);
        IsSaving = true;
        var resume = await Playback.ReleaseIfPlayingAsync([file.Path]);
        try
        {
            await Task.Run(() => TagService.WriteLyrics(file.Path, text));
            file.Model.HasLyrics = true;
            file.Model.IsLyricsSynced = synced;
            Library.RefreshRows([file.Model]);
            Toast.Success("가사를 저장했습니다", file.DisplayTitle);
        }
        catch (Exception ex)
        {
            Toast.Error("가사를 저장하지 못했습니다", ex.Message);
        }
        finally
        {
            resume?.Invoke(null);
            IsSaving = false;
        }
        Playback.ReloadLyrics();
        ResetFinder();
    }

    [RelayCommand]
    private void CancelFind() => ResetFinder();
}

public enum LyricsFindState
{
    Idle,
    Searching,
    Found,
    NotFound,
}
