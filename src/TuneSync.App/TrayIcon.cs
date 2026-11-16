using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TuneSync.App;

/// <summary>The app's icon in the notification area (system tray), straight on Shell_NotifyIcon:
/// WPF has none of its own, and WinForms' NotifyIcon would pull all of WinForms in just for this
/// and give a menu that doesn't look like the rest of the app. The menu itself is a WPF
/// ContextMenu that the owner opens on <see cref="MenuRequested"/>.</summary>
internal sealed class TrayIcon : IDisposable
{
    private const int CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint IconId = 1;

    private readonly HwndSource _window;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly System.Drawing.Icon _smallIcon;
    private readonly System.Drawing.Icon _largeIcon;
    private string _toolTip = "GoTune Player";
    private bool _added;

    /// <summary>The icon was clicked (or chosen with the keyboard).</summary>
    public event Action? Activated;

    /// <summary>The icon was right-clicked (or Shift+F10 / the menu key on it).</summary>
    public event Action? MenuRequested;

    public TrayIcon()
    {
        // A hidden top-level window gets the icon's messages. (Not a message-only window: those
        // don't receive the TaskbarCreated broadcast that tells us Explorer restarted.)
        _window = new HwndSource(new HwndSourceParameters("GoTuneTray")
        {
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80, // WS_EX_TOOLWINDOW: never on the taskbar or in Alt+Tab
            Width = 0,
            Height = 0,
        });
        _window.AddHook(WndProc);

        var info = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TuneSync.ico"))!;
        using var stream = info.Stream;
        var bytes = new System.IO.MemoryStream();
        stream.CopyTo(bytes);
        // Sizes for the current DPI, so the icon is drawn from the right frame instead of scaled.
        _smallIcon = LoadIcon(bytes, GetSystemMetrics(49 /* SM_CXSMICON */));
        _largeIcon = LoadIcon(bytes, GetSystemMetrics(11 /* SM_CXICON */));
    }

    private static System.Drawing.Icon LoadIcon(System.IO.MemoryStream bytes, int size)
    {
        bytes.Position = 0;
        return new System.Drawing.Icon(bytes, size, size);
    }

    public bool IsVisible
    {
        get => _added;
        set
        {
            if (value == _added) return;
            if (value) Add();
            else Remove();
        }
    }

    /// <summary>Shown when the pointer rests on the icon (Windows cuts it at 127 characters).</summary>
    public string ToolTip
    {
        get => _toolTip;
        set
        {
            value = value.Length > 127 ? value[..126] + "…" : value;
            if (value == _toolTip) return;
            _toolTip = value;
            if (_added) Notify(NimModify, NewData(NifTip | NifShowTip));
        }
    }

    /// <summary>A Windows notification from the icon (on Windows 10/11 a toast in the corner).</summary>
    public void ShowNotification(string title, string text)
    {
        if (!_added) return;
        var data = NewData(NifInfo);
        data.szInfoTitle = title.Length > 63 ? title[..63] : title;
        data.szInfo = text.Length > 255 ? text[..255] : text;
        data.dwInfoFlags = NiifUser | NiifLargeIcon;
        data.hBalloonIcon = _largeIcon.Handle;
        Notify(NimModify, data);
    }

    private void Add()
    {
        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        _added = Notify(NimAdd, data);
        if (!_added) return;
        // Version 4: one NIN_SELECT per click and WM_CONTEXTMENU for the menu, with the
        // standard tooltip only when NIF_SHOWTIP is set.
        data.uTimeoutOrVersion = 4;
        Notify(NimSetVersion, data);
    }

    private void Remove()
    {
        Notify(NimDelete, NewData(0));
        _added = false;
    }

    private NotifyIconData NewData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _smallIcon.Handle,
        szTip = _toolTip,
        szInfo = "",
        szInfoTitle = "",
    };

    private static bool Notify(uint message, NotifyIconData data) => Shell_NotifyIcon(message, ref data);

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == CallbackMessage)
        {
            switch ((int)(lParam.ToInt64() & 0xFFFF))
            {
                case NinSelect:
                case NinKeySelect:
                    Activated?.Invoke();
                    break;
                case WmContextMenu:
                    MenuRequested?.Invoke();
                    break;
            }
            handled = true;
        }
        else if (msg == _taskbarCreated && _added)
        {
            // Explorer restarted and forgot every icon: put ours back.
            _added = false;
            Add();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        // Without this the icon lingers until the pointer passes over it.
        if (_added) Remove();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _smallIcon.Dispose();
        _largeIcon.Dispose();
    }

    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NimSetVersion = 4;
    private const uint NifMessage = 0x1, NifIcon = 0x2, NifTip = 0x4, NifInfo = 0x10, NifShowTip = 0x80;
    private const uint NiifUser = 0x4, NiifLargeIcon = 0x20;
    private const int NinSelect = 0x400, NinKeySelect = 0x401, WmContextMenu = 0x7B;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NotifyIconData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
}
