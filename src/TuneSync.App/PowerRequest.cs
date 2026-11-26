using System.Runtime.InteropServices;

namespace TuneSync.App;

/// <summary>Keeps Windows from going to sleep while long work runs (a batch of 싱크 가사 만들기),
/// the screen still allowed to turn off. A power request of its own, so it doesn't touch the
/// player's "keep the screen on" (that one sets the UI thread's execution state); `powercfg
/// /requests` shows its reason.</summary>
public sealed class PowerRequest : IDisposable
{
    private IntPtr _handle;

    public PowerRequest(string reason)
    {
        var context = new ReasonContext
        {
            Version = 0, // POWER_REQUEST_CONTEXT_VERSION
            Flags = 1, // POWER_REQUEST_CONTEXT_SIMPLE_STRING
            SimpleReasonString = reason,
        };
        _handle = PowerCreateRequest(ref context);
        if (_handle == InvalidHandle || _handle == IntPtr.Zero)
        {
            _handle = IntPtr.Zero;
            return;
        }
        // Best effort: without it the PC may sleep, which pauses the work rather than breaking it.
        PowerSetRequest(_handle, SystemRequired);
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        PowerClearRequest(_handle, SystemRequired);
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    /// <summary>The PC has a battery and runs on it now (unplugged).</summary>
    public static bool OnBattery
    {
        get
        {
            if (!GetSystemPowerStatus(out var status)) return false;
            // ACLineStatus 0 = offline; BatteryFlag 128 = no system battery
            return status.ACLineStatus == 0 && status.BatteryFlag != 128;
        }
    }

    private const int SystemRequired = 1; // PowerRequestSystemRequired
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ReasonContext
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref ReasonContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr handle, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr handle, int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
