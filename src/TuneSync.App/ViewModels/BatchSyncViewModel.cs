using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Shell;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TuneSync.Core.Models;
using TuneSync.Core.Services;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.App.ViewModels;

public enum BatchSyncItemState { Waiting, Preparing, Running, Done, Skipped, Failed, Undone }

/// <summary>One song of a batch: where it is, and once done how it went.</summary>
public sealed partial class BatchSyncItemViewModel(LibraryFileViewModel file) : ObservableObject
{
    public LibraryFileViewModel File { get; } = file;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsActive), nameof(IsDone), nameof(IsSkipped), nameof(IsFailed),
        nameof(IsUndone), nameof(NeedsCheck), nameof(CanReview))]
    private BatchSyncItemState _state;

    /// <summary>The line at the row's right: "대기", "2분 05초 · 확인 필요 3줄", why it failed…</summary>
    [ObservableProperty]
    private string _status = "대기";

    /// <summary>Lines whose time was estimated, not heard (worth a listen).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsCheck))]
    private int _checkCount;

    /// <summary>Opened in the sync editor and saved there since: the estimates were listened to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsCheck))]
    private bool _isReviewed;

    /// <summary>The full reason a song failed, for the tooltip.</summary>
    [ObservableProperty]
    private string? _detail;

    public bool IsWaiting => State == BatchSyncItemState.Waiting;
    public bool IsActive => State is BatchSyncItemState.Preparing or BatchSyncItemState.Running;
    public bool IsDone => State == BatchSyncItemState.Done;
    public bool IsSkipped => State == BatchSyncItemState.Skipped;
    public bool IsFailed => State == BatchSyncItemState.Failed;
    public bool IsUndone => State == BatchSyncItemState.Undone;
    public bool NeedsCheck => IsDone && CheckCount > 0 && !IsReviewed;
    public bool CanReview => IsDone;

    /// <summary>What the batch's save replaced (for 모두 되돌리기).</summary>
    public TagEditUndo? Undo { get; set; }

    /// <summary>How long the work took (the download before it not counted).</summary>
    public TimeSpan Took { get; set; }
}

public enum BatchSyncPhase { Ready, Downloading, Running, Stopping, Stopped, Finished }

/// <summary>"싱크 가사 일괄 만들기": syncs the chosen songs one after another with one engine (its
/// models read once), saving each to its file as soon as it's done - a batch can run for hours,
/// and nothing already done should hang on the app staying open. 모두 되돌리기 puts back what the
/// batch replaced while the window is open. Stopping ends the song in progress (it stays as it
/// was) and keeps the rest waiting, to be continued.</summary>
public sealed partial class BatchSyncViewModel : ObservableObject, IDisposable
{
    private readonly PlaybackViewModel _playback;
    private readonly LibraryViewModel _library;
    private readonly Network.NetworkLibrary _network;
    private readonly AppSettings _settings;
    private readonly TagEditorViewModel _editor;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch _songClock = new();
    private double _songDone; // how far the song in progress is, 0..1, by its steps (see Fraction)
    private LyricsSyncEngine? _engine;
    private CancellationTokenSource? _work;
    private Task _running = Task.CompletedTask;
    private bool _disposed;

    public BatchSyncViewModel(PlaybackViewModel playback, LibraryViewModel library, Network.NetworkLibrary network,
        AppSettings settings, TagEditorViewModel editor, IEnumerable<LibraryFileViewModel> songs, bool includeSynced)
    {
        _playback = playback;
        _library = library;
        _network = network;
        _settings = settings;
        _editor = editor;
        IncludeSynced = includeSynced;
        foreach (var song in songs) Items.Add(new BatchSyncItemViewModel(song));
        _clock.Tick += (_, _) => UpdateTotals();
        UpdateTotals();
    }

    /// <summary>Songs that already had synced lyrics are synced again (they were let in).</summary>
    public bool IncludeSynced { get; }

    public ObservableCollection<BatchSyncItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsStopped), nameof(IsFinished), nameof(IsDownloading),
        nameof(IsStopping), nameof(CanResume), nameof(CanUndoAll), nameof(TaskbarState), nameof(FooterHint))]
    private BatchSyncPhase _phase = BatchSyncPhase.Ready;

    /// <summary>Working (or stopping): the window asks before closing, the single-song sync waits.</summary>
    public bool IsRunning => Phase is BatchSyncPhase.Downloading or BatchSyncPhase.Running or BatchSyncPhase.Stopping;
    public bool IsDownloading => Phase == BatchSyncPhase.Downloading;
    public bool IsStopping => Phase == BatchSyncPhase.Stopping;
    public bool IsStopped => Phase == BatchSyncPhase.Stopped;
    public bool IsFinished => Phase == BatchSyncPhase.Finished;
    public bool CanResume => IsStopped && Items.Any(i => i.IsWaiting);
    public string ResumeText => $"남은 {Items.Count(i => i.IsWaiting)}곡 계속";
    public bool CanUndoAll => !IsRunning && Items.Any(i => i.IsDone && i.Undo is not null);

    // ---- the song in progress ----

    [ObservableProperty]
    private BatchSyncItemViewModel? _current;

    public ObservableCollection<SyncStepViewModel> Steps { get; } = [];

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string _stepPercent = "";

    [ObservableProperty]
    private double _stepProgress;

    [ObservableProperty]
    private string _processingDevice = "";

    /// <summary>Raised when a song starts, so the list can show it.</summary>
    public event Action<BatchSyncItemViewModel>? CurrentChanged;

    // ---- the whole batch ----

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private double _overallProgress;

    [ObservableProperty]
    private string _remainingText = "";

    [ObservableProperty]
    private string _finishAtText = "";

    /// <summary>The headline of the card when nothing runs: stopped or finished.</summary>
    [ObservableProperty]
    private string _outcomeTitle = "";

    [ObservableProperty]
    private string _outcomeText = "";

    /// <summary>The mark beside the outcome: a check when finished, a pause when stopped, an
    /// undo arrow once 모두 되돌리기 put everything back.</summary>
    [ObservableProperty]
    private Wpf.Ui.Controls.SymbolRegular _outcomeSymbol = Wpf.Ui.Controls.SymbolRegular.Checkmark24;

    /// <summary>The mark in a quiet grey (stopped, undone) rather than the accent (finished).</summary>
    [ObservableProperty]
    private bool _isOutcomeQuiet;

    public TaskbarItemProgressState TaskbarState => Phase switch
    {
        BatchSyncPhase.Running or BatchSyncPhase.Downloading => TaskbarItemProgressState.Normal,
        BatchSyncPhase.Stopping or BatchSyncPhase.Stopped => TaskbarItemProgressState.Paused,
        _ => TaskbarItemProgressState.None,
    };

    public string FooterHint => Phase switch
    {
        BatchSyncPhase.Running or BatchSyncPhase.Downloading => "작업하는 동안 PC가 절전 모드로 들어가지 않습니다. 화면은 꺼질 수 있습니다.",
        BatchSyncPhase.Stopping => "지금 곡을 마무리하는 중입니다…",
        _ when CanUndoAll => "끝난 곡은 저장되어 있습니다. 창을 닫으면 되돌릴 수 없습니다.",
        _ => "",
    };

    private int Waiting => Items.Count(i => i.IsWaiting || i.IsActive);

    // ---- running ----

    /// <summary>Starts (or, after a stop, continues with) the songs still waiting.</summary>
    public void Start()
    {
        if (IsRunning || _disposed || !Items.Any(i => i.IsWaiting)) return;
        _work = new CancellationTokenSource();
        _running = RunAsync(_work.Token);
    }

    [RelayCommand]
    private void Resume() => Start();

    [RelayCommand]
    private void Stop()
    {
        if (!IsRunning || IsStopping) return;
        Phase = BatchSyncPhase.Stopping;
        Status = "지금 곡을 멈추는 중…";
        _work?.Cancel();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        using var awake = new PowerRequest("싱크 가사 일괄 만들기");
        _clock.Start();
        try
        {
            if (!WhisperModelStore.IsDownloaded || !VocalModelStore.IsDownloaded)
            {
                Phase = BatchSyncPhase.Downloading;
                await DownloadModelsAsync(ct);
            }
            Phase = BatchSyncPhase.Running;
            _engine ??= new LyricsSyncEngine(LyricsSyncOptions.From(_settings));
            while (Items.FirstOrDefault(i => i.IsWaiting) is { } item)
            {
                ct.ThrowIfCancellationRequested();
                Prefetch(item);
                await SyncAsync(item, ct);
            }
            Finish(stopped: false);
        }
        catch (OperationCanceledException)
        {
            Finish(stopped: true);
        }
        catch (Exception ex)
        {
            // A model that couldn't be read (deleted, to be downloaded again) or failed to download.
            Toast.Error("싱크 가사 일괄 만들기를 멈췄습니다", ex.Message);
            Finish(stopped: true);
        }
        finally
        {
            _clock.Stop();
        }
    }

    /// <summary>One song: read its lyrics, sync, save. Throws when the batch can't go on (stopped,
    /// or the models gone).</summary>
    private async Task SyncAsync(BatchSyncItemViewModel item, CancellationToken ct)
    {
        Current = item;
        CurrentChanged?.Invoke(item);
        item.State = BatchSyncItemState.Preparing;
        item.Status = "준비 중…";
        Status = "가사를 읽는 중…";
        (StepProgress, StepPercent, ProcessingDevice) = (0, "", "");
        Steps.Clear();
        _songClock.Restart();
        _songDone = 0;
        try
        {
            var lyrics = await Task.Run(() => TagService.ReadLyrics(item.File.Path), ct) ?? "";
            var synced = LrcParser.Parse(lyrics);
            List<string> header = [];
            List<string> sung;
            if (synced.Count > 0)
            {
                if (!IncludeSynced) { Skip(item, "이미 싱크 가사가 있습니다"); return; }
                header = LrcWriter.HeaderLines(lyrics);
                sung = [.. synced.Where(l => l.Text.Length > 0).Select(l => l.Text)];
            }
            else sung = LrcWriter.PlainLines(lyrics);
            if (sung.Count == 0) { Skip(item, "가사가 없습니다"); return; }

            string audio = item.File.Path;
            if (item.File.IsRemote)
            {
                Status = "네트워크에서 노래를 받는 중…";
                item.Status = "노래를 받는 중…";
                audio = await _network.Cache.CompletedPathAsync(item.File.Path).WaitAsync(ct);
            }

            item.State = BatchSyncItemState.Running;
            item.Status = "진행 중";
            var names = SyncProgressText.StepNames(_engine!.Separates);
            for (var i = 0; i < names.Length; i++) Steps.Add(new SyncStepViewModel(i + 1, names[i]));
            SetStep(0);
            _songClock.Restart();
            _songDone = 0;
            var progress = new Progress<LyricsSyncEngine.SyncProgress>(p =>
            {
                if (Current != item || IsStopping) return;
                _songDone = Math.Max(_songDone, Fraction(p, Steps.Count == 3));
                var shown = SyncProgressText.Describe(p, _engine, Steps.Count);
                SetStep(shown.Step);
                if (p.Stage != LyricsSyncEngine.Stage.Aligning) ProcessingDevice = shown.Device;
                (Status, StepProgress, StepPercent) = (shown.Status, shown.Value, shown.Percent);
            });
            var result = await _engine.SyncAsync(audio, string.Join("\n", sung), progress, ct);
            item.Took = _songClock.Elapsed;
            if (LyricsSyncSpeed.Record(_settings, item.File.Model.Duration, item.Took)) AppSettingsStore.Save(_settings);

            Status = "저장하는 중…";
            var lrc = LrcWriter.Write(header, sung.Select((text, i) => (result[i].Time, text)));
            item.Undo = await SaveAsync(item.File, lrc);
            item.CheckCount = result.Count(r => !r.IsConfident);
            item.State = BatchSyncItemState.Done;
            item.Status = item.CheckCount == 0
                ? $"완료 · {Took(item.Took)}"
                : $"완료 · {Took(item.Took)} · 확인 필요 {item.CheckCount}줄";
            return;
        }
        catch (OperationCanceledException)
        {
            // Stopped: this song stays as it was, and waits with the rest.
            item.State = BatchSyncItemState.Waiting;
            item.Status = "대기";
            throw;
        }
        catch (Exception ex)
        {
            Fail(item, ex.Message);
            // The engine deleted a model it couldn't read (to download again): every song after
            // would fail the same way. Anything else is this song's (a file that won't decode).
            if (!WhisperModelStore.IsDownloaded || !VocalModelStore.IsDownloaded) throw;
            return;
        }
        finally
        {
            UpdateTotals();
        }
    }

    private async Task<TagEditUndo> SaveAsync(LibraryFileViewModel file, string lrc)
    {
        var resume = await _playback.ReleaseIfPlayingAsync([file.Path]);
        TagEditUndo undo;
        try
        {
            undo = await Task.Run(() => TagEditService.Write(file.Model, null, null, lrc));
        }
        finally
        {
            resume?.Invoke(null);
        }
        Refresh([file]);
        return undo;
    }

    private void Refresh(IReadOnlyList<LibraryFileViewModel> files)
    {
        _library.RefreshRows(files.Select(f => f.Model));
        if (_playback.CurrentFile is { } playing && files.Contains(playing)) _playback.ReloadLyrics();
        _editor.RefreshIfShowing(files);
    }

    private static void Skip(BatchSyncItemViewModel item, string why)
    {
        item.State = BatchSyncItemState.Skipped;
        item.Status = $"건너뜀 · {why}";
    }

    private static void Fail(BatchSyncItemViewModel item, string why)
    {
        item.State = BatchSyncItemState.Failed;
        // The reason on the row too (cut short; whole in the tooltip): a bare "실패" says nothing
        // to someone who doesn't think to hover.
        var line = why.Split('\n', 2)[0].Trim();
        item.Status = $"실패 · {(line.Length <= 40 ? line : line[..39] + "…")}";
        item.Detail = why;
    }

    /// <summary>The next network song starts downloading while this one is synced, so its turn
    /// doesn't begin with a wait.</summary>
    private void Prefetch(BatchSyncItemViewModel current)
    {
        var index = Items.IndexOf(current);
        var next = Items.Skip(index + 1).FirstOrDefault(i => i.IsWaiting);
        if (next is null || !next.File.IsRemote) return;
        _ = _network.Cache.CompletedPathAsync(next.File.Path)
            .ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); // its own turn reports a failure
    }

    private async Task DownloadModelsAsync(CancellationToken ct)
    {
        StepProgress = 0;
        if (!VocalModelStore.IsDownloaded)
            await VocalModelStore.DownloadAsync(new Progress<double>(p => ShowDownload("보컬 분리 모델", p, 64, VocalModelStore.DisplaySize)), ct);
        if (!WhisperModelStore.IsDownloaded)
            await WhisperModelStore.DownloadAsync(new Progress<double>(p => ShowDownload("음성 인식 모델", p, 190, WhisperModelStore.DisplaySize)), ct);
    }

    private void ShowDownload(string what, double p, int megabytes, string size)
    {
        Status = $"{what}을 받는 중… ({p * megabytes:0}/{size})";
        StepProgress = p;
        StepPercent = SyncProgressText.Percent(p);
    }

    private void SetStep(int current)
    {
        for (var i = 0; i < Steps.Count; i++)
            Steps[i].State = i < current ? SyncStepState.Done : i == current ? SyncStepState.Active : SyncStepState.Waiting;
    }

    private void Finish(bool stopped)
    {
        Current = null;
        Steps.Clear();
        (Status, StepPercent, StepProgress, ProcessingDevice) = ("", "", 0, "");
        Phase = stopped ? BatchSyncPhase.Stopped : BatchSyncPhase.Finished;
        UpdateTotals();
        var done = Items.Count(i => i.IsDone);
        var check = Items.Count(i => i.NeedsCheck);
        var failed = Items.Count(i => i.IsFailed);
        (OutcomeSymbol, IsOutcomeQuiet) = stopped
            ? (Wpf.Ui.Controls.SymbolRegular.Pause24, true)
            : (Wpf.Ui.Controls.SymbolRegular.Checkmark24, false);
        if (stopped)
        {
            OutcomeTitle = "중지했습니다";
            var kept = done == 0 ? "아직 끝난 곡이 없어 바뀐 파일은 없습니다." : $"끝난 {done}곡은 저장되어 있습니다.";
            OutcomeText = Waiting == 0 ? kept : $"{kept} 남은 {Waiting}곡은 이어서 할 수 있습니다.";
            return;
        }
        OutcomeTitle = failed == 0 ? "모두 마쳤습니다" : "마쳤습니다";
        OutcomeText = check > 0
            ? $"노란 점이 있는 {check}곡은 추정한 줄이 있습니다. '확인'을 눌러 들어보고 고칠 수 있습니다."
            : done > 0 ? "모든 곡의 싱크 가사를 저장했습니다." : "저장한 곡이 없습니다.";
        Toast.Success("싱크 가사 일괄 만들기를 마쳤습니다", ResultText);
    }

    // ---- the totals and the time left ----

    private void UpdateTotals()
    {
        var done = Items.Count(i => i.IsDone);
        var failed = Items.Count(i => i.IsFailed);
        var skipped = Items.Count(i => i.IsSkipped);
        var undone = Items.Count(i => i.IsUndone);
        var check = Items.Count(i => i.NeedsCheck);
        var counted = Items.Count - skipped;
        var finished = done + failed + undone;
        CountText = $"{finished} / {counted}곡";
        var parts = new List<string> { $"완료 {done}" };
        if (check > 0) parts.Add($"확인 필요 {check}");
        if (failed > 0) parts.Add($"실패 {failed}");
        if (skipped > 0) parts.Add($"건너뜀 {skipped}");
        if (undone > 0) parts.Add($"되돌림 {undone}");
        ResultText = string.Join(" · ", parts);

        // Each song's share is its expected time. The one in progress counts by how far its steps
        // are; what it has left is its expected time drawn toward what its pace so far says (a
        // song can take twice another of the same length: a third listen, stretches heard again).
        TimeSpan Expected(BatchSyncItemViewModel i) => LyricsSyncSpeed.Estimate(_settings, i.File.Model.Duration);
        var all = Items.Where(i => !i.IsSkipped).ToList();
        var total = all.Sum(i => Expected(i).TotalSeconds);
        var past = all.Where(i => i.IsDone || i.IsFailed || i.IsUndone).Sum(i => Expected(i).TotalSeconds);
        var left = all.Where(i => i.IsWaiting).Sum(i => Expected(i).TotalSeconds);
        if (Current is { IsActive: true } current && IsRunning)
        {
            var expected = Expected(current).TotalSeconds;
            var elapsed = _songClock.Elapsed.TotalSeconds;
            var behind = _songDone;
            past += expected * behind;
            var paced = behind >= 0.1 ? elapsed / behind : expected;
            var whole = expected * (1 - behind) + paced * behind;
            left += Math.Max(whole - elapsed, 3);
        }
        OverallProgress = total <= 0 ? (IsFinished ? 1 : 0) : Math.Clamp(past / total, 0, 1);
        if (IsFinished) OverallProgress = 1;

        if (Phase is BatchSyncPhase.Running or BatchSyncPhase.Downloading && left > 0)
        {
            var remaining = TimeSpan.FromSeconds(left);
            // Under a minute is said loosely: a song can still need a third listen.
            RemainingText = remaining < TimeSpan.FromMinutes(1) ? "1분 이내 남음" : $"{LyricsSyncSpeed.Describe(remaining)} 남음";
            FinishAtText = $"{(DateTime.Now + remaining).ToString("tt h:mm", CultureInfo.GetCultureInfo("ko-KR"))}쯤 끝날 예정";
        }
        else (RemainingText, FinishAtText) = ("", "");
        OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(ResumeText));
        OnPropertyChanged(nameof(CanUndoAll));
        OnPropertyChanged(nameof(FooterHint));
    }

    /// <summary>About how much of a song's time is behind it at this step. On a Core Ultra 5 225
    /// (tools/SyncBench and the batch window's own runs): separating took about a third, the first
    /// two listens a fifth together or less, a third listen (the song with the lyrics as the
    /// prompt) often as long as both, the stretches heard again the rest. A third listen that
    /// isn't needed is simply skipped over.</summary>
    private static double Fraction(LyricsSyncEngine.SyncProgress p, bool separates)
    {
        static double Between(double from, double to, double value) => from + (to - from) * Math.Clamp(value, 0, 1);
        return p.Stage switch
        {
            LyricsSyncEngine.Stage.Decoding => 0,
            LyricsSyncEngine.Stage.Separating => Between(0, 0.3, p.Value),
            LyricsSyncEngine.Stage.Listening when p.Relistening => separates ? Between(0.9, 0.98, p.Value) : Between(0.85, 0.97, p.Value),
            LyricsSyncEngine.Stage.Listening when separates => p.PassIndex switch
            {
                0 => Between(0.3, 0.45, p.Value),
                1 => Between(0.45, 0.6, p.Value),
                _ => Between(0.6, 0.9, p.Value),
            },
            LyricsSyncEngine.Stage.Listening => p.PassIndex == 0 ? Between(0, 0.45, p.Value) : Between(0.45, 0.85, p.Value),
            _ => 0.98,
        };
    }

    private static string Took(TimeSpan time) => time.TotalMinutes >= 1
        ? $"{(int)time.TotalMinutes}분 {time.Seconds:00}초"
        : $"{time.Seconds}초";

    // ---- after the work ----

    /// <summary>Puts back what the batch replaced in every song it saved.</summary>
    [RelayCommand]
    private async Task UndoAllAsync()
    {
        var saved = Items.Where(i => i.IsDone && i.Undo is not null).ToList();
        if (IsRunning || saved.Count == 0) return;
        // (KeepWords: the message box would otherwise break a word in two)
        if (!await Dialogs.ConfirmAsync("모두 되돌리기", KeepWords.Joined(
                    $"이 작업에서 저장한 {saved.Count}곡의 가사를 작업 전으로 되돌릴까요? 목록의 확인 버튼으로 고친 내용도 함께 되돌아갑니다."),
                $"{saved.Count}곡 되돌리기"))
            return;
        var resume = await _playback.ReleaseIfPlayingAsync(saved.Select(i => i.File.Path));
        var failed = 0;
        try
        {
            foreach (var item in saved)
            {
                try
                {
                    await Task.Run(() => TagEditService.Undo(item.Undo!));
                    item.Undo = null;
                    item.State = BatchSyncItemState.Undone;
                    item.Status = "되돌림";
                }
                catch { failed++; }
            }
        }
        finally
        {
            resume?.Invoke(null);
        }
        Refresh([.. saved.Select(i => i.File)]);
        UpdateTotals();
        (OutcomeSymbol, IsOutcomeQuiet) = (Wpf.Ui.Controls.SymbolRegular.ArrowUndo24, true);
        OutcomeTitle = "되돌렸습니다";
        OutcomeText = failed == 0 ? $"{saved.Count - failed}곡의 가사를 작업 전으로 되돌렸습니다."
            : $"{saved.Count - failed}곡을 되돌렸고, {failed}곡은 되돌리지 못해 저장한 상태로 남아 있습니다.";
        if (failed > 0) Toast.Warning("일부 곡을 되돌리지 못했습니다", $"{failed}곡은 저장한 상태로 남아 있습니다.");
    }

    /// <summary>Opens a finished song in the sync editor, to listen to the estimated lines.</summary>
    public async Task ReviewAsync(BatchSyncItemViewModel item, System.Windows.Window owner)
    {
        if (!item.IsDone) return;
        string lyrics;
        try
        {
            lyrics = await Task.Run(() => TagService.ReadLyrics(item.File.Path)) ?? "";
        }
        catch (Exception ex)
        {
            Toast.Error("가사를 읽지 못했습니다", ex.Message);
            return;
        }
        if (LyricsSyncWindow.Run(owner, item.File, lyrics) is not null)
        {
            item.IsReviewed = true;
            item.Status = $"완료 · {Took(item.Took)} · 확인함";
            Refresh([item.File]);
            UpdateTotals();
        }
    }

    /// <summary>Frees the models once the work has stopped (not while ONNX Runtime or whisper.cpp
    /// is still in the middle of a piece).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _work?.Cancel();
        _clock.Stop();
        var engine = _engine;
        _engine = null;
        if (engine is null) return;
        if (_running.IsCompleted) engine.Dispose();
        else _ = _running.ContinueWith(_ => engine.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}
