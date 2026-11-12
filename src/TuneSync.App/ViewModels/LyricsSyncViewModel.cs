using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.App.ViewModels;

/// <summary>One lyric line in the sync editor.</summary>
public sealed partial class SyncLineViewModel(string text) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(HasTime), nameof(NeedsCheck))]
    private TimeSpan? _time;

    /// <summary>Heard by recognition or set by hand; false = estimated, worth a listen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsCheck))]
    private bool _isConfident = true;

    /// <summary>The line being sung right now.</summary>
    [ObservableProperty]
    private bool _isCurrent;

    public bool HasTime => Time is not null;
    public bool NeedsCheck => Time is not null && !IsConfident;

    public string TimeText => Time is { } t ? $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}" : "--:--.--";
}

/// <summary>Turns plain lyrics into synced (LRC) lyrics: "자동으로 맞추기" lets local speech
/// recognition place every line, then the lines can be fixed by ear - Space stamps the selected
/// line at the playing position, ←/→ nudge it, clicking a time plays from there.</summary>
public sealed partial class LyricsSyncViewModel : ObservableObject, IDisposable
{
    /// <summary>A tap lands a little after the singing starts; take that back.</summary>
    private static readonly TimeSpan ReactionTime = TimeSpan.FromMilliseconds(120);

    private readonly PlaybackViewModel _playback;
    private readonly LibraryViewModel _library;
    private readonly Network.NetworkLibrary _network;
    private LyricsSyncEngine? _engine;
    private CancellationTokenSource? _work;

    public LyricsSyncViewModel(PlaybackViewModel playback, LibraryViewModel library, Network.NetworkLibrary network,
        LibraryFileViewModel file, string plainLyrics)
    {
        _playback = playback;
        _library = library;
        _network = network;
        File = file;
        foreach (var line in LrcWriter.PlainLines(plainLyrics)) Lines.Add(new SyncLineViewModel(line));
        SelectedLine = Lines.FirstOrDefault();
        UpdateSummary();
    }

    public LibraryFileViewModel File { get; }
    public PlaybackViewModel Playback => _playback;
    public ObservableCollection<SyncLineViewModel> Lines { get; } = [];

    [ObservableProperty]
    private SyncLineViewModel? _selectedLine;

    /// <summary>Raised when the selection should be scrolled into view.</summary>
    public event Action<SyncLineViewModel>? ScrollRequested;

    /// <summary>Raised when the editor is done (saved = true) and the window should close.</summary>
    public event Action<bool>? Finished;

    public bool IsThisSongLoaded => _playback.CurrentFile == File;

    // ---- status ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isSyncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    private bool _isDownloading;

    public bool IsBusy => IsSyncing || IsDownloading;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _hasChanges;

    public bool NeedsModel => !WhisperModelStore.IsDownloaded;

    private void UpdateSummary()
    {
        var timed = Lines.Count(l => l.HasTime);
        var check = Lines.Count(l => l.NeedsCheck);
        Summary = timed == 0 ? $"{Lines.Count}줄 · 아직 시간이 없습니다"
            : timed < Lines.Count ? $"{Lines.Count}줄 중 {timed}줄 시간 지정" + (check > 0 ? $" · 확인 필요 {check}줄" : "")
            : check > 0 ? $"{Lines.Count}줄 모두 지정 · 확인 필요 {check}줄" : $"{Lines.Count}줄 모두 지정";
    }

    // ---- automatic sync ----

    [RelayCommand]
    private async Task AutoSyncAsync()
    {
        if (IsBusy) return;
        if (NeedsModel)
        {
            var ok = await Dialogs.ConfirmAsync("음성 인식 모델 받기",
                $"자동 맞추기는 음성 인식 모델(Whisper, {WhisperModelStore.DisplaySize})로 노래를 이 PC에서 직접 듣고 가사 줄의 시간을 찾습니다." +
                $"{Environment.NewLine}{Environment.NewLine}처음 한 번만 Hugging Face에서 모델을 받습니다. 노래와 가사는 외부로 보내지 않습니다.",
                $"받기 ({WhisperModelStore.DisplaySize})");
            if (!ok) return;
            if (!await DownloadModelAsync()) return;
        }

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        IsSyncing = true;
        Progress = 0;
        try
        {
            Status = "노래를 준비하는 중…";
            var audio = File.IsRemote ? await _network.Cache.CompletedPathAsync(File.Path) : File.Path;
            var plain = string.Join("\n", Lines.Select(l => l.Text));
            _engine ??= new LyricsSyncEngine();
            var progress = new Progress<(LyricsSyncEngine.Stage Stage, double Value)>(p =>
            {
                (Status, Progress) = p.Stage switch
                {
                    LyricsSyncEngine.Stage.Decoding => ("노래를 읽는 중…", 0.02),
                    LyricsSyncEngine.Stage.Listening => ($"노래를 듣는 중… {(int)(p.Value * 100)}%", 0.05 + p.Value * 0.9),
                    _ => ("가사 줄에 시간을 맞추는 중…", 0.97),
                };
            });
            var result = await _engine.SyncAsync(audio, plain, progress, ct);
            for (var i = 0; i < Lines.Count && i < result.Count; i++)
            {
                Lines[i].Time = result[i].Time;
                Lines[i].IsConfident = result[i].IsConfident;
            }
            HasChanges = true;
            var check = result.Count(r => !r.IsConfident);
            Status = check == 0
                ? "자동으로 맞췄습니다. 재생하며 확인해 보세요."
                : $"자동으로 맞췄습니다. 노란색으로 표시된 {check}줄은 추정한 시간이니 들어보고 확인하세요.";
            SelectedLine = Lines.FirstOrDefault(l => l.NeedsCheck) ?? Lines.FirstOrDefault();
            if (SelectedLine is { } s) ScrollRequested?.Invoke(s);
        }
        catch (OperationCanceledException)
        {
            Status = "자동 맞추기를 취소했습니다.";
        }
        catch (Exception ex)
        {
            Status = "자동으로 맞추지 못했습니다.";
            Toast.Error("자동 맞추기 실패", ex.Message);
        }
        finally
        {
            IsSyncing = false;
            Progress = 0;
            UpdateSummary();
        }
    }

    private async Task<bool> DownloadModelAsync()
    {
        _work = new CancellationTokenSource();
        IsDownloading = true;
        try
        {
            Status = "음성 인식 모델을 받는 중…";
            await WhisperModelStore.DownloadAsync(new Progress<double>(p =>
            {
                Progress = p;
                Status = $"음성 인식 모델을 받는 중… {(int)(p * 100)}% ({p * 190:0}/{WhisperModelStore.DisplaySize})";
            }), _work.Token);
            OnPropertyChanged(nameof(NeedsModel));
            return true;
        }
        catch (OperationCanceledException)
        {
            Status = "모델 받기를 취소했습니다.";
            return false;
        }
        catch (Exception ex)
        {
            Status = "모델을 받지 못했습니다.";
            Toast.Error("음성 인식 모델을 받지 못했습니다", ex.Message);
            return false;
        }
        finally
        {
            IsDownloading = false;
            Progress = 0;
        }
    }

    [RelayCommand]
    private void CancelWork() => _work?.Cancel();

    // ---- listening and tapping ----

    private void EnsureLoaded()
    {
        if (!IsThisSongLoaded) _playback.PlayFile(File);
    }

    [RelayCommand]
    private void TogglePlay()
    {
        if (!IsThisSongLoaded) { _playback.PlayFile(File); return; }
        _playback.TogglePlayPauseCommand.Execute(null);
    }

    /// <summary>Sets the selected line to the playing position and moves on to the next line.</summary>
    [RelayCommand]
    private void Stamp()
    {
        if (!IsThisSongLoaded) { EnsureLoaded(); return; }
        var line = SelectedLine ?? Lines.FirstOrDefault(l => !l.HasTime);
        if (line is null) return;
        var at = _playback.CurrentPosition - ReactionTime;
        line.Time = at < TimeSpan.Zero ? TimeSpan.Zero : at;
        line.IsConfident = true;
        HasChanges = true;
        var next = Lines.IndexOf(line) + 1;
        if (next < Lines.Count)
        {
            SelectedLine = Lines[next];
            ScrollRequested?.Invoke(Lines[next]);
        }
        UpdateSummary();
    }

    /// <summary>Moves the selected line's time by <paramref name="seconds"/>.</summary>
    public void Nudge(double seconds)
    {
        if (SelectedLine is not { Time: { } t } line) return;
        var moved = t + TimeSpan.FromSeconds(seconds);
        line.Time = moved < TimeSpan.Zero ? TimeSpan.Zero : moved;
        line.IsConfident = true;
        HasChanges = true;
        UpdateSummary();
    }

    [RelayCommand]
    private void NudgeBack() => Nudge(-0.1);

    [RelayCommand]
    private void NudgeForward() => Nudge(0.1);

    /// <summary>Plays from a little before the line, so its start can be heard.</summary>
    [RelayCommand]
    private void PlayFrom(SyncLineViewModel? line)
    {
        line ??= SelectedLine;
        if (line is null) return;
        SelectedLine = line;
        var from = (line.Time ?? Lines.TakeWhile(l => l != line).LastOrDefault(l => l.HasTime)?.Time ?? TimeSpan.Zero) - TimeSpan.FromSeconds(1.5);
        if (from < TimeSpan.Zero) from = TimeSpan.Zero;
        if (!IsThisSongLoaded)
        {
            _playback.PlayFile(File);
            // The song opens asynchronously; seek once it's loaded.
            _ = SeekWhenLoadedAsync(from);
            return;
        }
        _playback.SeekTo(from);
    }

    private async Task SeekWhenLoadedAsync(TimeSpan to)
    {
        for (var i = 0; i < 40 && _playback.DurationSeconds <= 1; i++) await Task.Delay(50);
        if (IsThisSongLoaded) _playback.SeekTo(to);
    }

    [RelayCommand]
    private void ClearTime()
    {
        if (SelectedLine is not { } line || line.Time is null) return;
        line.Time = null;
        line.IsConfident = true;
        HasChanges = true;
        UpdateSummary();
    }

    /// <summary>Called ~20 times a second by the window: marks the line being sung.</summary>
    public void Tick()
    {
        SyncLineViewModel? current = null;
        if (IsThisSongLoaded)
        {
            var pos = _playback.CurrentPosition;
            foreach (var line in Lines)
                if (line.Time is { } t && t <= pos && (current?.Time is not { } c || t >= c)) current = line;
        }
        foreach (var line in Lines) line.IsCurrent = line == current;
    }

    // ---- saving ----

    [ObservableProperty]
    private bool _isSaving;

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy || IsSaving) return;
        var missing = Lines.Count(l => !l.HasTime);
        if (missing > 0)
        {
            SelectedLine = Lines.First(l => !l.HasTime);
            ScrollRequested?.Invoke(SelectedLine);
            Toast.Warning("시간이 없는 줄이 있습니다", $"{missing}줄에 시간을 지정한 뒤 저장하세요. (자동으로 맞추기 또는 Space)");
            return;
        }

        // Keep the file's order; LRC readers sort by time anyway.
        var lrc = LrcWriter.Write(Lines.Select(l => (l.Time!.Value, l.Text)));
        IsSaving = true;
        var resume = await _playback.ReleaseIfPlayingAsync([File.Path]);
        try
        {
            await Task.Run(() => TagEditService.Write(File.Model, null, null, lrc));
        }
        catch (Exception ex)
        {
            Toast.Error("싱크 가사를 저장하지 못했습니다", ex.Message);
            return;
        }
        finally
        {
            resume?.Invoke(null);
            IsSaving = false;
        }

        _library.RefreshRows([File.Model]);
        if (IsThisSongLoaded) _playback.ReloadLyrics();
        HasChanges = false;
        Toast.Success("싱크 가사를 저장했습니다", File.DisplayTitle);
        Finished?.Invoke(true);
    }

    public void Dispose()
    {
        _work?.Cancel();
        _engine?.Dispose();
    }
}
