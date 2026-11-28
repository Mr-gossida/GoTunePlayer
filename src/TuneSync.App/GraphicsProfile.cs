using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using TuneSync.Core.Models;

namespace TuneSync.App;

/// <summary>설정 › 화면 효과 › 그래픽 품질, worked out into numbers the moving visuals use: how often
/// the shared clock ticks (<see cref="FrameTicker"/>), how often and how finely the light behind focus
/// mode and the screen saver is drawn, and what the lightest level leaves out. Every visual reads it
/// when it starts and again on <see cref="Changed"/>.</summary>
internal static class GraphicsProfile
{
    /// <summary>The level in use: High, Standard or Light (never Auto).</summary>
    public static GraphicsQuality Level { get; private set; } = GraphicsQuality.Standard;

    public static event Action? Changed;

    /// <summary>What Auto means on this PC (worked out once).</summary>
    public static GraphicsQuality Detected => _detected ??= Detect();

    private static GraphicsQuality? _detected;

    /// <summary>Whether the app started on the light level: then every WPF animation was capped
    /// at 30 frames a second, which can only be set before the first one is made.</summary>
    public static bool AnimationsCapped { get; private set; }

    /// <summary>At startup, before any window: the chosen level, and on the light one the cap on
    /// all animations (hover fades, the lyrics' line changes...).</summary>
    public static void Start(GraphicsQuality chosen)
    {
        Level = Resolve(chosen);
        if (Level != GraphicsQuality.Light) return;
        AnimationsCapped = true;
        // Only a root timeline's rate counts: the storyboards of triggers and the animations
        // started from code. An explicit rate (the light's 15, the equalizer icon's 24) still wins.
        foreach (var type in new[] { typeof(Storyboard), typeof(DoubleAnimation), typeof(ColorAnimation), typeof(ThicknessAnimation),
                                     typeof(PointAnimation), typeof(DoubleAnimationUsingKeyFrames), typeof(ObjectAnimationUsingKeyFrames) })
            Timeline.DesiredFrameRateProperty.OverrideMetadata(type, new FrameworkPropertyMetadata((int?)30));
    }

    /// <summary>A change in the settings: open visuals follow at once (the animation cap only
    /// after a restart).</summary>
    public static void Apply(GraphicsQuality chosen)
    {
        var level = Resolve(chosen);
        if (level == Level) return;
        Level = level;
        Changed?.Invoke();
    }

    private static GraphicsQuality Resolve(GraphicsQuality chosen) => chosen == GraphicsQuality.Auto ? Detected : chosen;

    /// <summary>The shared clock runs on the screen's own frames (high), not a timer.</summary>
    public static bool ScreenFrames => Level == GraphicsQuality.High;

    /// <summary>The shared clock's timer, when it isn't on the screen's frames: about 30 or 20 a
    /// second. (Windows' timer moves in 15.6 ms steps: 50 ms came out at 62.5, 16 a second; 45 is
    /// 46.9, 21 a second.)</summary>
    public static TimeSpan TickInterval => Level == GraphicsQuality.Light ? TimeSpan.FromMilliseconds(45) : TimeSpan.FromMilliseconds(30);

    /// <summary>Steps a second of the light behind focus mode and the screen saver (halved by its
    /// 절전 setting). Each step redraws the whole screen.</summary>
    public static double LightStepsPerSecond => Level switch
    {
        GraphicsQuality.High => 30,
        GraphicsQuality.Light => 8,
        _ => 15,
    };

    /// <summary>The resolution the light (and the soft backdrop under it) is worked out at, then stretched.</summary>
    public static double LightScale => Level switch
    {
        GraphicsQuality.High => 0.5,
        GraphicsQuality.Light => 0.25,
        _ => 0.33,
    };

    /// <summary>Animations that move the whole picture (the screen saver's drift): unlimited on
    /// high, otherwise 30 a second.</summary>
    public static int? WholeScreenFrameRate => Level == GraphicsQuality.High ? null : 30;

    /// <summary>The wide soft shadows of focus mode and the screen saver (cover, lyrics, clock).</summary>
    public static bool BigShadows => Level != GraphicsQuality.Light;

    /// <summary>Motion that must follow the screen's own frames to look steady (the 오리 seek bar,
    /// the lyrics' glide); on the light level it takes the shared clock's steps instead.</summary>
    public static bool SmoothMotion => Level != GraphicsQuality.Light;

    // ---- Auto ----

    /// <summary>Light without full hardware rendering, or on a graphics card with little memory of
    /// its own (integrated graphics share the PC's memory); otherwise standard. Never high: that
    /// costs more for more smoothness, the user's call.</summary>
    private static GraphicsQuality Detect()
    {
        try
        {
            if ((RenderCapability.Tier >> 16) < 2) return GraphicsQuality.Light;
            if (DedicatedVideoMemory() is { } bytes && bytes < 1L << 30) return GraphicsQuality.Light;
        }
        catch
        {
            // no answer from the graphics stack: the usual level
        }
        return GraphicsQuality.Standard;
    }

    /// <summary>The video memory of the first hardware graphics adapter (the one with the main
    /// screen, which WPF draws on), or null if DXGI can't say.</summary>
    private static long? DedicatedVideoMemory()
    {
        if (CreateDXGIFactory1(typeof(IDXGIFactory1).GUID, out var factoryObject) != 0 || factoryObject is not IDXGIFactory1 factory) return null;
        try
        {
            for (uint i = 0; factory.EnumAdapters1(i, out var adapter) == 0; i++)
            {
                try
                {
                    if (adapter.GetDesc1(out var desc) != 0) continue;
                    if ((desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE
                    return (long)desc.DedicatedVideoMemory;
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1([MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIFactory
        void EnumAdapters();
        void MakeWindowAssociation();
        void GetWindowAssociation();
        void CreateSwapChain();
        void CreateSoftwareAdapter();
        // IDXGIFactory1
        [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        [PreserveSig] int IsCurrent();
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // IDXGIObject
        void SetPrivateData();
        void SetPrivateDataInterface();
        void GetPrivateData();
        void GetParent();
        // IDXGIAdapter
        void EnumOutputs();
        void GetDesc();
        void CheckInterfaceSupport();
        // IDXGIAdapter1
        [PreserveSig] int GetDesc1(out AdapterDesc1 desc);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
}
