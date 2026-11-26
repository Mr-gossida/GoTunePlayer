using CommunityToolkit.Mvvm.ComponentModel;
using TuneSync.Core.Models;
using TuneSync.Core.Services.LyricsSync;

namespace TuneSync.App.ViewModels;

/// <summary>Which of the selected songs a batch of 싱크 가사 만들기 would sync, which it leaves
/// out and why - from what the library already knows (no file is opened; each song's lyrics are
/// read again when its turn comes).</summary>
public sealed class BatchSyncPlan
{
    /// <summary>Plain lyrics: what the batch is for.</summary>
    public List<LibraryFileViewModel> Plain { get; } = [];

    /// <summary>Synced lyrics already: synced again only when asked to.</summary>
    public List<LibraryFileViewModel> Synced { get; } = [];

    public int NoLyrics { get; private set; }

    /// <summary>Lyrics edited in the editor panel and not saved yet: the batch would work from
    /// the file's lyrics and then overwrite the edit, so the song is left alone.</summary>
    public int Unsaved { get; private set; }

    /// <summary>A format whose tags can't hold lyrics (DSDIFF).</summary>
    public int Unsupported { get; private set; }

    public int Selected { get; private set; }

    public static BatchSyncPlan From(IEnumerable<LibraryFileViewModel> selection)
    {
        var plan = new BatchSyncPlan();
        foreach (var file in selection)
        {
            plan.Selected++;
            if (file.Path.EndsWith(".dff", StringComparison.OrdinalIgnoreCase)) plan.Unsupported++;
            else if (file.PendingLyrics is not null) plan.Unsaved++;
            else if (!file.Model.HasLyrics) plan.NoLyrics++;
            else if (file.Model.IsLyricsSynced) plan.Synced.Add(file);
            else plan.Plain.Add(file);
        }
        return plan;
    }

    public List<LibraryFileViewModel> Songs(bool includeSynced) =>
        includeSynced ? [.. Plain, .. Synced] : [.. Plain];
}

/// <summary>The confirmation before a batch starts: how many songs, how long it should take
/// (per song and in all, from this PC's measured speed), and that it's heavy work - the CPU and
/// the graphics near their limit for that long, a laptop's battery drained.</summary>
public sealed partial class BatchSyncConfirmViewModel : ObservableObject
{
    private readonly BatchSyncPlan _plan;
    private readonly AppSettings _settings;

    public BatchSyncConfirmViewModel(BatchSyncPlan plan, AppSettings settings, bool onBattery, bool needsModel)
    {
        _plan = plan;
        _settings = settings;
        OnBattery = onBattery;
        NeedsModel = needsModel;
        (_, IsMeasured) = LyricsSyncSpeed.Rate(settings);
        Update();
    }

    public bool OnBattery { get; }

    public bool NeedsModel { get; }

    /// <summary>The time is from songs synced on this PC (false: a rough figure until one is).</summary>
    public bool IsMeasured { get; }

    public bool HasSynced => _plan.Synced.Count > 0;

    public string IncludeSyncedText => $"이미 싱크 가사가 있는 {_plan.Synced.Count}곡도 다시 맞추기";

    [ObservableProperty]
    private bool _includeSynced;

    partial void OnIncludeSyncedChanged(bool value) => Update();

    [ObservableProperty]
    private int _count;

    [ObservableProperty]
    private string _heading = "";

    [ObservableProperty]
    private string _skipped = "";

    [ObservableProperty]
    private string _perSong = "";

    [ObservableProperty]
    private string _total = "";

    [ObservableProperty]
    private string _remoteNote = "";

    [ObservableProperty]
    private string _startText = "";

    public string EstimateNote => IsMeasured
        ? "이 PC에서 최근 처리한 속도와 곡 길이로 계산했습니다."
        : "이 PC에서는 처음이라 대략적인 값입니다. 첫 곡을 마치면 실제 속도로 다시 계산합니다.";

    private void Update()
    {
        var songs = _plan.Songs(IncludeSynced);
        Count = songs.Count;
        Heading = songs.Count == 0 ? "새로 만들 곡이 없습니다. 이미 싱크 가사가 있는 곡을 다시 맞추려면 아래를 체크하세요."
            : songs.Count == _plan.Selected ? $"선택한 {songs.Count}곡의 싱크 가사를 만듭니다"
            : $"선택한 {_plan.Selected}곡 중 {songs.Count}곡의 싱크 가사를 만듭니다";

        var skipped = new List<string>();
        if (_plan.NoLyrics > 0) skipped.Add($"가사 없음 {_plan.NoLyrics}곡");
        if (!IncludeSynced && _plan.Synced.Count > 0) skipped.Add($"이미 싱크 가사 {_plan.Synced.Count}곡");
        if (_plan.Unsaved > 0) skipped.Add($"저장하지 않은 가사 편집 {_plan.Unsaved}곡");
        if (_plan.Unsupported > 0) skipped.Add($"가사를 저장할 수 없는 형식(DFF) {_plan.Unsupported}곡");
        Skipped = skipped.Count == 0 ? "" : $"건너뜀 · {string.Join(" · ", skipped)}";

        var total = TimeSpan.FromTicks(songs.Sum(f => LyricsSyncSpeed.Estimate(_settings, f.Model.Duration).Ticks));
        PerSong = songs.Count == 0 ? "-" : LyricsSyncSpeed.Describe(total / songs.Count);
        Total = songs.Count == 0 ? "-" : LyricsSyncSpeed.Describe(total);
        var remote = songs.Count(f => f.IsRemote);
        RemoteNote = remote == 0 ? "" : $"네트워크에 있는 {remote}곡은 노래를 받는 시간이 더 걸릴 수 있습니다.";
        StartText = songs.Count == 0 ? "시작" : $"{songs.Count}곡 시작";
    }
}
