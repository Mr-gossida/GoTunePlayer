using System.Diagnostics;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TuneSync.App.ViewModels;

/// <summary>예약 종료: after a chosen time the music pauses (position saved) and Windows shuts
/// down, with Windows' own one-minute warning so it can still be called off. The countdown runs
/// in the app, so it shows on the timer button and can be cancelled at any point.</summary>
public sealed partial class SleepTimerViewModel : ObservableObject
{
    private static readonly TimeSpan WindowsWarning = TimeSpan.FromSeconds(60);

    private readonly PlaybackViewModel _playback;
    private readonly DispatcherTimer _tick;
    private DateTime _deadline;

    public SleepTimerViewModel(PlaybackViewModel playback)
    {
        _playback = playback;
        _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tick.Tick += (_, _) => Update();
    }

    /// <summary>A shutdown is scheduled (counting down, or already handed to Windows).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isActive;

    /// <summary>Handed to Windows: the PC shuts down within the minute unless cancelled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private bool _isShuttingDown;

    /// <summary>"1:23:45" / "12:05" left, shown next to the timer button.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTip))]
    private string _remainingText = "";

    public string ToolTip => !IsActive ? "예약 종료"
        : IsShuttingDown ? "곧 PC가 종료됩니다 - 눌러서 취소"
        : $"예약 종료까지 {RemainingText} 남음";

    public void Start(TimeSpan after)
    {
        CancelWindowsShutdown();
        _deadline = DateTime.Now + after;
        IsShuttingDown = false;
        IsActive = true;
        Update();
        _tick.Start();
        Toast.Info("예약 종료", $"{Describe(after)} 뒤에 음악을 멈추고 PC를 종료합니다.");
    }

    public void Cancel()
    {
        var wasShuttingDown = IsShuttingDown;
        _tick.Stop();
        CancelWindowsShutdown();
        IsActive = false;
        IsShuttingDown = false;
        RemainingText = "";
        Toast.Info("예약 종료 취소", wasShuttingDown ? "PC 종료를 취소했습니다." : "예약 종료를 취소했습니다.");
    }

    private void Update()
    {
        var left = _deadline - DateTime.Now;
        if (left <= TimeSpan.Zero && !IsShuttingDown)
        {
            ShutDownNow();
            return;
        }
        if (IsShuttingDown) left = left + WindowsWarning;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        RemainingText = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss") : left.ToString(@"m\:ss");
    }

    private void ShutDownNow()
    {
        // Music off and the spot remembered first, then Windows' shutdown with its warning.
        if (_playback.IsPlaying) _playback.TogglePlayPauseCommand.Execute(null);
        _playback.SaveSession();
        IsShuttingDown = true;
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe",
                $"/s /t {(int)WindowsWarning.TotalSeconds} /c \"TuneSync 예약 종료: 1분 뒤 PC가 종료됩니다.\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            Toast.Warning("1분 뒤 PC가 종료됩니다", "취소하려면 예약 종료 버튼을 누르세요.");
        }
        catch (Exception ex)
        {
            _tick.Stop();
            IsActive = false;
            IsShuttingDown = false;
            Toast.Error("PC를 종료하지 못했습니다", ex.Message);
        }
    }

    private void CancelWindowsShutdown()
    {
        if (!IsShuttingDown) return;
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000);
        }
        catch
        {
            // nothing scheduled with Windows (anymore) - fine
        }
    }

    public static string Describe(TimeSpan span) =>
        span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes}분"
        : span.Minutes == 0 ? $"{(int)span.TotalHours}시간"
        : $"{(int)span.TotalHours}시간 {span.Minutes}분";
}
