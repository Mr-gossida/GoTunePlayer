using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.App.ViewModels;

/// <summary>One lyric line in the sync editor.</summary>
public sealed partial class SyncLineViewModel(string text) : ObservableObject
{
    public string Text { get; } = text;

    /// <summary>A timed empty line from existing synced lyrics: where a line stops showing (an
    /// instrumental break). Optional - never required for saving, dropped when left without a time.</summary>
    public bool IsGap => Text.Length == 0;

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
    private readonly AppSettings _settings;
    private LyricsSyncEngine? _engine;
    private CancellationTokenSource? _work;

    public LyricsSyncViewModel(PlaybackViewModel playback, LibraryViewModel library, Network.NetworkLibrary network,
        AppSettings settings, LibraryFileViewModel file, string lyrics)
    {
        _settings = settings;
        _playback = playback;
        _library = library;
        _network = network;
        File = file;
        var synced = LrcParser.Parse(lyrics);
        IsEditing = synced.Count > 0;
        if (IsEditing)
        {
            // Already synced: start from its times (and keep its [ar:]/[ti:]… tags for saving).
            _header = LrcWriter.HeaderLines(lyrics);
            foreach (var line in synced) Lines.Add(new SyncLineViewModel(line.Text) { Time = line.Time });
        }
        else
        {
            foreach (var line in LrcWriter.PlainLines(lyrics)) Lines.Add(new SyncLineViewModel(line));
        }
        SelectedLine = Lines.FirstOrDefault(l => !l.IsGap) ?? Lines.FirstOrDefault();
        UpdateSummary();
    }

    private readonly List<string> _header = [];

    /// <summary>The song already had synced lyrics: this edits their timing ("싱크 가사 편집")
    /// rather than making them from plain text.</summary>
    public bool IsEditing { get; }

    public string WindowTitle => IsEditing ? "싱크 가사 편집" : "싱크 가사 만들기";

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

    /// <summary>The speech recognition model or the vocal separation model is still to be downloaded.</summary>
    public bool NeedsModel => !WhisperModelStore.IsDownloaded || !VocalModelStore.IsDownloaded;

    private void UpdateSummary()
    {
        var sung = Lines.Where(l => !l.IsGap).ToList();
        var timed = sung.Count(l => l.HasTime);
        var check = sung.Count(l => l.NeedsCheck);
        Summary = timed == 0 ? $"{sung.Count}줄 · 아직 시간이 없습니다"
            : timed < sung.Count ? $"{sung.Count}줄 중 {timed}줄 시간 지정" + (check > 0 ? $" · 확인 필요 {check}줄" : "")
            : check > 0 ? $"{sung.Count}줄 모두 지정 · 확인 필요 {check}줄" : $"{sung.Count}줄 모두 지정";
    }

    // ---- automatic sync ----

    [RelayCommand]
    private async Task AutoSyncAsync()
    {
        if (IsBusy) return;
        if (NeedsModel)
        {
            // Someone who synced before the vocal separation came along has Whisper already.
            var (title, text, size) = WhisperModelStore.IsDownloaded
                ? ("보컬 분리 모델 받기",
                    $"자동 맞추기가 더 정확해졌습니다. 노래에서 목소리만 분리하는 모델({VocalModelStore.DisplaySize})을 추가로 받아, 반주를 뺀 목소리로 가사 줄의 시간을 찾습니다." +
                    $"{Environment.NewLine}{Environment.NewLine}처음 한 번만 GitHub에서 모델을 받습니다. 노래와 가사는 외부로 보내지 않습니다.",
                    VocalModelStore.DisplaySize)
                : ("음성 인식 모델 받기",
                    $"자동 맞추기는 노래에서 목소리를 분리하는 모델({VocalModelStore.DisplaySize})과 음성 인식 모델(Whisper, {WhisperModelStore.DisplaySize})로 노래를 이 PC에서 직접 듣고 가사 줄의 시간을 찾습니다." +
                    $"{Environment.NewLine}{Environment.NewLine}처음 한 번만 Hugging Face와 GitHub에서 모델을 받습니다. 노래와 가사는 외부로 보내지 않습니다.",
                    "254MB");
            var ok = await Dialogs.ConfirmAsync(title, text, $"받기 ({size})");
            if (!ok) return;
            if (!await DownloadModelAsync()) return;
        }
        if (Lines.Any(l => l.HasTime) && !await Dialogs.ConfirmAsync("자동으로 맞추기",
                "지금 있는 시간을 모두 새로 맞춥니다. 저장하기 전까지는 파일이 바뀌지 않으니, 마음에 들지 않으면 저장하지 않고 닫으면 됩니다.",
                "새로 맞추기"))
            return;

        _work = new CancellationTokenSource();
        var ct = _work.Token;
        IsSyncing = true;
        Progress = 0;
        try
        {
            Status = "노래를 준비하는 중…";
            var audio = File.IsRemote ? await _network.Cache.CompletedPathAsync(File.Path) : File.Path;
            var sung = Lines.Where(l => !l.IsGap).ToList();
            var plain = string.Join("\n", sung.Select(l => l.Text));
            _engine ??= new LyricsSyncEngine(LyricsSyncOptions.From(_settings));
            var progress = new Progress<(LyricsSyncEngine.Stage Stage, double Value)>(p =>
            {
                (Status, Progress) = p.Stage switch
                {
                    LyricsSyncEngine.Stage.Decoding => ("노래를 읽는 중…", 0.02),
                    LyricsSyncEngine.Stage.Separating => ($"목소리를 분리하는 중… {(int)(p.Value * 100)}%", 0.03 + p.Value * 0.17),
                    LyricsSyncEngine.Stage.Listening => ($"노래를 듣는 중… {(int)(p.Value * 100)}%{DeviceNote()}", 0.2 + p.Value * 0.77),
                    _ => ("가사 줄에 시간을 맞추는 중…", 1.0),
                };
            });
            var result = await _engine.SyncAsync(audio, plain, progress, ct);
            for (var i = 0; i < sung.Count && i < result.Count; i++)
            {
                sung[i].Time = result[i].Time;
                sung[i].IsConfident = result[i].IsConfident;
            }
            // The old breaks' times no longer fit the new line times (and recognition doesn't
            // place breaks): clear them, so they're left out unless stamped again.
            foreach (var gap in Lines.Where(l => l.IsGap)) gap.Time = null;
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

    /// <summary>" · 그래픽 카드" or " · CPU" once the listening has started, for the status line.</summary>
    private static string DeviceNote() => LyricsSyncEngine.ListeningDevice switch
    {
        null => "",
        "CPU" => " · CPU",
        _ => " · 그래픽 카드",
    };

    private async Task<bool> DownloadModelAsync()
    {
        _work = new CancellationTokenSource();
        IsDownloading = true;
        try
        {
            if (!VocalModelStore.IsDownloaded)
            {
                Status = "보컬 분리 모델을 받는 중…";
                await VocalModelStore.DownloadAsync(new Progress<double>(p =>
                {
                    Progress = p;
                    Status = $"보컬 분리 모델을 받는 중… {(int)(p * 100)}% ({p * 64:0}/{VocalModelStore.DisplaySize})";
                }), _work.Token);
            }
            if (!WhisperModelStore.IsDownloaded)
            {
                Status = "음성 인식 모델을 받는 중…";
                await WhisperModelStore.DownloadAsync(new Progress<double>(p =>
                {
                    Progress = p;
                    Status = $"음성 인식 모델을 받는 중… {(int)(p * 100)}% ({p * 190:0}/{WhisperModelStore.DisplaySize})";
                }), _work.Token);
            }
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
            Toast.Error("모델을 받지 못했습니다", ex.Message);
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
        var missing = Lines.Count(l => !l.HasTime && !l.IsGap);
        if (missing > 0)
        {
            SelectedLine = Lines.First(l => !l.HasTime && !l.IsGap);
            ScrollRequested?.Invoke(SelectedLine);
            Toast.Warning("시간이 없는 줄이 있습니다", $"{missing}줄에 시간을 지정한 뒤 저장하세요. (자동으로 맞추기 또는 Space)");
            return;
        }

        // Keep the file's order; LRC readers sort by time anyway.
        var lrc = LrcWriter.Write(_header, Lines.Where(l => l.HasTime).Select(l => (l.Time!.Value, l.Text)));
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
