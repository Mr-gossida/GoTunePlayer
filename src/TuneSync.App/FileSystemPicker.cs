using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using TuneSync.Core.Services;

namespace TuneSync.App;

/// <summary>File-system pickers built on the classic Explorer-style dialog, which (unlike the
/// modern one) shows folders and files together. Plain GetOpenFileName is silently upgraded by
/// Windows to the modern picker, which never returns folders; attaching a hook keeps the classic
/// dialog, and subclassing its OK button lets us read the real shell selection (folders included)
/// instead of letting the dialog navigate into the selected folder.</summary>
public static class FileSystemPicker
{
    private static readonly OfnHookProc HookDelegate = HookProc;
    private static readonly WndProc SubclassDelegate = SubclassProc;
    private static IntPtr _originalWndProc;
    private static List<string>? _interceptedSelection;
    private static string _okButtonText = "";
    private static bool _emptySelectionPicksCurrentFolder;

    /// <summary>AlSong-style: any mix of folders and audio files, several at once.</summary>
    public static List<string>? PickFoldersAndFiles(string title) =>
        Show(title, "추가(&A)", multiSelect: true, emptySelectionPicksCurrentFolder: false, initialFolder: null);

    /// <summary>One folder. Pressing 선택 with nothing highlighted picks the folder currently
    /// open in the dialog; a highlighted file picks the folder it's in.</summary>
    public static string? PickFolder(string title, string? initialFolder = null)
    {
        var picked = Show(title, "선택(&S)", multiSelect: false, emptySelectionPicksCurrentFolder: true, initialFolder);
        if (picked is not { Count: > 0 }) return null;

        var path = picked[0];
        return System.IO.Directory.Exists(path) ? path : System.IO.Path.GetDirectoryName(path);
    }

    private static List<string>? Show(string title, string okButtonText, bool multiSelect,
                                      bool emptySelectionPicksCurrentFolder, string? initialFolder)
    {
        _okButtonText = okButtonText;
        _emptySelectionPicksCurrentFolder = emptySelectionPicksCurrentFolder;

        var pattern = string.Join(';', TagService.SupportedExtensions.Select(ext => "*" + ext));
        var filter = $"음악 파일\0{pattern}\0모든 파일\0*.*\0\0";
        var owner = Application.Current?.MainWindow is { } main ? new WindowInteropHelper(main).Handle : IntPtr.Zero;
        var startIn = initialFolder is not null && System.IO.Directory.Exists(initialFolder) ? initialFolder : null;

        var bufferChars = 1 << 16;
        while (true)
        {
            _interceptedSelection = null;
            var buffer = Marshal.AllocHGlobal(bufferChars * sizeof(char));
            try
            {
                Marshal.WriteInt16(buffer, 0, 0);

                var ofn = new OPENFILENAME
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAME>(),
                    hwndOwner = owner,
                    lpstrFilter = filter,
                    lpstrFile = buffer,
                    nMaxFile = bufferChars,
                    lpstrTitle = title,
                    lpstrInitialDir = startIn,
                    lpfnHook = Marshal.GetFunctionPointerForDelegate(HookDelegate),
                    Flags = (multiSelect ? OFN_ALLOWMULTISELECT : 0) | OFN_EXPLORER | OFN_ENABLEHOOK | OFN_ENABLESIZING
                            | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY | OFN_NOCHANGEDIR,
                };

                var ok = GetOpenFileNameW(ref ofn);
                if (_interceptedSelection is { Count: > 0 } selection)
                    return selection;
                if (ok)
                    return ParseMultiSelectBuffer(buffer);

                if (CommDlgExtendedError() == FNERR_BUFFERTOOSMALL)
                {
                    bufferChars *= 2;
                    continue;
                }
                return null; // cancelled, or a failure we can't recover from
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                _interceptedSelection = null;
            }
        }
    }

    private static IntPtr HookProc(IntPtr hdlg, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != WM_NOTIFY) return IntPtr.Zero;

        var code = Marshal.ReadInt32(lParam, 2 * IntPtr.Size);
        if (code == CDN_INITDONE)
        {
            var dialog = GetParent(hdlg);
            SendMessageW(dialog, CDM_SETCONTROLTEXT, (IntPtr)IDOK, _okButtonText);
            _originalWndProc = SetWindowLongPtrW(dialog, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(SubclassDelegate));
        }
        return IntPtr.Zero;
    }

    private static IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_COMMAND && (wParam.ToInt64() & 0xFFFF) == IDOK)
        {
            var selection = ReadShellSelection(hwnd);
            if (selection.Count == 0 && _emptySelectionPicksCurrentFolder && ReadDialogText(hwnd, CDM_GETSPEC).Length == 0)
            {
                // Nothing highlighted and nothing typed: the user means "this folder". (If they
                // typed a path, fall through so the dialog navigates there as usual.)
                var current = ReadDialogText(hwnd, CDM_GETFOLDERPATH);
                if (current.Length > 0) selection.Add(current);
            }

            if (selection.Count > 0)
            {
                _interceptedSelection = selection;
                return CallWindowProcW(_originalWndProc, hwnd, WM_COMMAND, (IntPtr)IDCANCEL, IntPtr.Zero);
            }
        }
        else if (msg == WM_NCDESTROY)
        {
            SetWindowLongPtrW(hwnd, GWLP_WNDPROC, _originalWndProc);
        }
        return CallWindowProcW(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    /// <summary>Full file-system paths of everything currently selected in the dialog's file
    /// list - folders and files alike. Empty when nothing is selected (or anything goes wrong),
    /// in which case the dialog's own OK handling runs as usual.</summary>
    private static List<string> ReadShellSelection(IntPtr dialog)
    {
        var result = new List<string>();
        var dataObject = IntPtr.Zero;
        try
        {
            var browserPtr = SendMessageW(dialog, WM_GETISHELLBROWSER, IntPtr.Zero, IntPtr.Zero);
            if (browserPtr == IntPtr.Zero) return result;

            var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPtr);
            browser.QueryActiveShellView(out var view);

            var iidDataObject = IID_IDataObject;
            if (view.GetItemObject(SVGIO_SELECTION, ref iidDataObject, out dataObject) < 0 || dataObject == IntPtr.Zero)
                return result;

            var iidItemArray = IID_IShellItemArray;
            if (SHCreateShellItemArrayFromDataObject(dataObject, ref iidItemArray, out var items) < 0)
                return result;

            items.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                items.GetItemAt(i, out var item);
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out var pathPtr) < 0) continue;
                try
                {
                    var path = Marshal.PtrToStringUni(pathPtr);
                    if (!string.IsNullOrEmpty(path)) result.Add(path);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pathPtr);
                }
            }
        }
        catch (COMException)
        {
            result.Clear();
        }
        catch (InvalidCastException)
        {
            result.Clear();
        }
        finally
        {
            if (dataObject != IntPtr.Zero) Marshal.Release(dataObject);
        }
        return result;
    }

    /// <summary>Reads a string the dialog reports via a CDM_* message (current folder path, or the
    /// text in the file-name box).</summary>
    private static string ReadDialogText(IntPtr dialog, uint message)
    {
        const int capacity = 32768;
        var buffer = Marshal.AllocHGlobal(capacity * sizeof(char));
        try
        {
            Marshal.WriteInt16(buffer, 0, 0);
            var length = SendMessageW(dialog, message, (IntPtr)capacity, buffer).ToInt64();
            return length > 0 ? Marshal.PtrToStringUni(buffer) ?? "" : "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static List<string> ParseMultiSelectBuffer(IntPtr buffer)
    {
        var offset = 0;
        string ReadNext()
        {
            var s = Marshal.PtrToStringUni(buffer + offset * sizeof(char)) ?? string.Empty;
            offset += s.Length + 1;
            return s;
        }

        var first = ReadNext();
        var second = ReadNext();

        if (second.Length == 0)
            return first.Length == 0 ? [] : [first]; // single item selected: 'first' is its full path

        var result = new List<string> { System.IO.Path.Combine(first, second) };
        string name;
        while ((name = ReadNext()).Length > 0)
            result.Add(System.IO.Path.Combine(first, name));
        return result;
    }

    private delegate IntPtr OfnHookProc(IntPtr hdlg, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAME ofn);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr newLong);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr prevWndProc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll")]
    private static extern int SHCreateShellItemArrayFromDataObject(IntPtr dataObject, ref Guid riid, out IShellItemArray items);

    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_ENABLEHOOK = 0x00000020;
    private const int OFN_ALLOWMULTISELECT = 0x00000200;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_EXPLORER = 0x00080000;
    private const int OFN_ENABLESIZING = 0x00800000;
    private const int FNERR_BUFFERTOOSMALL = 0x3003;

    private const uint WM_NOTIFY = 0x004E;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_GETISHELLBROWSER = 0x0400 + 7;
    private const uint CDM_GETSPEC = 0x0400 + 100;
    private const uint CDM_GETFOLDERPATH = 0x0400 + 100 + 2;
    private const uint CDM_SETCONTROLTEXT = 0x0400 + 100 + 4;
    private const int CDN_INITDONE = -601;
    private const int IDOK = 1;
    private const int IDCANCEL = 2;
    private const int GWLP_WNDPROC = -4;
    private const uint SVGIO_SELECTION = 0x1;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    private static readonly Guid IID_IDataObject = new("0000010e-0000-0000-C000-000000000046");
    private static readonly Guid IID_IShellItemArray = new("b63ea76d-1f85-456f-a19c-48159efa858b");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OPENFILENAME
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string? lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    // Vtable order below must match shobjidl.h exactly; unused slots are declared only to keep
    // the later methods at the right offsets.
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(int fEnterMode);
        void InsertMenusSB(IntPtr hmenuShared, IntPtr lpMenuWidths);
        void SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        void RemoveMenusSB(IntPtr hmenuShared);
        void SetStatusTextSB(IntPtr pszStatusText);
        void EnableModelessSB(int fEnable);
        void TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        void BrowseObject(IntPtr pidl, uint wFlags);
        void GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        void GetControlWindow(uint id, out IntPtr phwnd);
        void SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        void QueryActiveShellView(out IShellView ppshv);
        void OnViewWindowActive(IShellView pshv);
        void SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport, Guid("000214E3-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellView
    {
        void GetWindow(out IntPtr phwnd);
        void ContextSensitiveHelp(int fEnterMode);
        [PreserveSig] int TranslateAccelerator(IntPtr pmsg);
        void EnableModeless(int fEnable);
        void UIActivate(uint uState);
        void Refresh();
        void CreateViewWindow(IShellView psvPrevious, IntPtr pfs, IShellBrowser psb, IntPtr prcView, out IntPtr phWnd);
        void DestroyViewWindow();
        void GetCurrentInfo(IntPtr pfs);
        void AddPropertySheetPages(uint dwReserved, IntPtr pfn, IntPtr lparam);
        void SaveViewState();
        void SelectItem(IntPtr pidlItem, uint uFlags);
        [PreserveSig] int GetItemObject(uint uItem, ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [ComImport, Guid("b63ea76d-1f85-456f-a19c-48159efa858b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemArray
    {
        void BindToHandler(IntPtr pbc, ref Guid rbhid, ref Guid riid, out IntPtr ppvOut);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(IntPtr keyType, ref Guid riid, out IntPtr ppv);
        void GetAttributes(int attribFlags, uint sfgaoMask, out uint psfgaoAttribs);
        void GetCount(out uint pdwNumItems);
        void GetItemAt(uint dwIndex, out IShellItem ppsi);
        void EnumItems(out IntPtr ppenumShellItems);
    }
}
