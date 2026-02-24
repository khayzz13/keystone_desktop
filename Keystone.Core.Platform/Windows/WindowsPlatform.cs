// WindowsPlatform — Win32 implementation of IPlatform.
// Pure P/Invoke into user32.dll, shell32.dll, shcore.dll, comdlg32.dll.
// All window operations must happen on the thread that called Initialize().

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Keystone.Core;
using Keystone.Core.Rendering;

namespace Keystone.Core.Platform.Windows;

public class WindowsPlatform : IPlatform
{
    internal static readonly string WindowClassName = "KeystoneWindow";
    private Action<string>? _onMenuAction;
    private Func<IEnumerable<(string id, string title)>>? _windowListProvider;
    private readonly List<IntPtr> _allWindows = new();

    // Keep the wndproc delegate alive — static so it's never GC'd
    private static readonly Win32.WndProcDelegate _defaultWndProc = DefaultWndProc;

    public void Initialize()
    {
        // Per-monitor DPI awareness — must be called before any window creation
        Win32.SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        // Register window class
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            style = 0x0002 | 0x0001, // CS_HREDRAW | CS_VREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_defaultWndProc),
            hInstance = Win32.GetModuleHandle(null),
            hCursor = Win32.LoadCursor(IntPtr.Zero, 32512), // IDC_ARROW
            hbrBackground = IntPtr.Zero,
            lpszClassName = WindowClassName,
        };

        var atom = Win32.RegisterClassEx(ref wc);
        if (atom == 0)
        {
            // May already be registered (e.g. re-init) — ignore ERROR_CLASS_ALREADY_EXISTS
            int err = Marshal.GetLastWin32Error();
            if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
                throw new InvalidOperationException($"RegisterClassEx failed: {err}");
        }

        Console.WriteLine("[WindowsPlatform] Initialized");
    }

    private static IntPtr DefaultWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        => Win32.DefWindowProc(hwnd, msg, wParam, lParam);

    public void Quit()
    {
        Win32.PostQuitMessage(0);
    }

    public void PumpRunLoop(double seconds = 0.01)
    {
        var sw = Stopwatch.StartNew();
        var timeoutMs = (long)(seconds * 1000);
        while (Win32.PeekMessage(out var msg, IntPtr.Zero, 0, 0, 0x0001 /* PM_REMOVE */))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
        }
    }

    public (double x, double y, double w, double h) GetMainScreenFrame()
    {
        var pt = new Win32.POINT { X = 0, Y = 0 };
        var hMonitor = Win32.MonitorFromPoint(pt, 0x00000001 /* MONITOR_DEFAULTTOPRIMARY */);
        var mi = new Win32.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<Win32.MONITORINFOEX>() };
        if (Win32.GetMonitorInfo(hMonitor, ref mi))
        {
            var r = mi.rcWork;
            return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        }
        return (0, 0, 1920, 1080);
    }

    public void SetCursor(CursorType cursor)
    {
        int id = cursor switch
        {
            CursorType.Pointer    => 32649, // IDC_HAND
            CursorType.Text       => 32513, // IDC_IBEAM
            CursorType.ResizeEW   => 32644, // IDC_SIZEWE
            CursorType.ResizeNS   => 32645, // IDC_SIZENS
            CursorType.Move       => 32646, // IDC_SIZEALL
            CursorType.NotAllowed => 32648, // IDC_NO
            CursorType.Grab       => 32649, // IDC_HAND (closest)
            CursorType.Grabbing   => 32649, // IDC_HAND
            _                     => 32512, // IDC_ARROW
        };
        Win32.SetCursor(Win32.LoadCursor(IntPtr.Zero, id));
    }

    public (double x, double y) GetMouseLocation()
    {
        if (Win32.GetCursorPos(out var pt))
            return (pt.X, pt.Y);
        return (0, 0);
    }

    public bool IsMouseButtonDown()
        => (Win32.GetAsyncKeyState(0x01 /* VK_LBUTTON */) & 0x8000) != 0;

    public void BringAllWindowsToFront()
    {
        foreach (var hwnd in _allWindows)
            Win32.SetForegroundWindow(hwnd);
    }

    internal void TrackWindow(IntPtr hwnd) => _allWindows.Add(hwnd);
    internal void UntrackWindow(IntPtr hwnd) => _allWindows.Remove(hwnd);

    public INativeWindow CreateWindow(WindowConfig config)
    {
        var win = new WindowsNativeWindow(this, config, isOverlay: false);
        return win;
    }

    public INativeWindow CreateOverlayWindow(WindowConfig config)
    {
        var win = new WindowsNativeWindow(this, config, isOverlay: true);
        return win;
    }

    public Task<string[]?> ShowOpenDialogAsync(OpenDialogOptions opts)
    {
        var buffer = new char[32768];
        var ofn = new Win32.OPENFILENAME
        {
            lStructSize = (uint)Marshal.SizeOf<Win32.OPENFILENAME>(),
            hwndOwner = IntPtr.Zero,
            lpstrFile = buffer,
            nMaxFile = (uint)buffer.Length,
            lpstrTitle = opts.Title,
            Flags = 0x00001000 | 0x00000800 | 0x00000004, // OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY
        };

        if (opts.Multiple)
            ofn.Flags |= 0x00000200; // OFN_ALLOWMULTISELECT | OFN_EXPLORER

        if (opts.FileExtensions is { Length: > 0 })
        {
            var filter = string.Join("\0", opts.FileExtensions.Select(e => $"*.{e}")) + "\0\0";
            ofn.lpstrFilter = filter;
        }

        if (Win32.GetOpenFileName(ref ofn))
        {
            var path = new string(buffer).TrimEnd('\0');
            return Task.FromResult<string[]?>(new[] { path });
        }
        return Task.FromResult<string[]?>(null);
    }

    public Task<string?> ShowSaveDialogAsync(SaveDialogOptions opts)
    {
        var buffer = new char[32768];
        if (opts.DefaultName != null)
            opts.DefaultName.CopyTo(0, buffer, 0, Math.Min(opts.DefaultName.Length, buffer.Length - 1));

        var ofn = new Win32.OPENFILENAME
        {
            lStructSize = (uint)Marshal.SizeOf<Win32.OPENFILENAME>(),
            hwndOwner = IntPtr.Zero,
            lpstrFile = buffer,
            nMaxFile = (uint)buffer.Length,
            lpstrTitle = opts.Title,
            Flags = 0x00000002, // OFN_OVERWRITEPROMPT
        };

        if (Win32.GetSaveFileName(ref ofn))
        {
            var path = new string(buffer).TrimEnd('\0');
            return Task.FromResult<string?>(path);
        }
        return Task.FromResult<string?>(null);
    }

    public Task<int> ShowMessageBoxAsync(MessageBoxOptions opts)
    {
        uint flags = 0;
        if (opts.Buttons is { Length: >= 2 })
            flags = 0x00000001; // MB_OKCANCEL

        var result = Win32.MessageBox(IntPtr.Zero, opts.Message, opts.Title, flags);
        // IDOK=1, IDCANCEL=2 → map to 0-based button index
        return Task.FromResult(result - 1);
    }

    public void OpenExternal(string url)
    {
        Win32.ShellExecute(IntPtr.Zero, "open", url, null, null, 1);
    }

    public void OpenPath(string path)
    {
        Win32.ShellExecute(IntPtr.Zero, "open", path, null, null, 1);
    }

    public void InitializeMenu(Action<string> onMenuAction, KeystoneConfig? config = null)
    {
        _onMenuAction = onMenuAction;
        // Windows has no app-level menu bar — menus are per-window.
        // Custom in-app menu via the WebView UI handles this on Windows.
        Console.WriteLine("[WindowsPlatform] InitializeMenu: no native app menu bar on Windows");
    }

    public void AddMenuItem(string menu, string title, string action, string shortcut = "")
    {
        Console.WriteLine($"[WindowsPlatform] AddMenuItem not implemented: {menu}/{title}");
    }

    public void AddToolScripts(string[] scriptNames)
    {
        Console.WriteLine($"[WindowsPlatform] AddToolScripts: {scriptNames.Length} scripts");
    }

    public void SetWindowListProvider(Func<IEnumerable<(string id, string title)>> provider)
    {
        _windowListProvider = provider;
    }
}

// === P/Invoke bindings ===

internal static class Win32
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct OPENFILENAME
    {
        public uint lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        [MarshalAs(UnmanagedType.LPArray)] public char[]? lpstrFile;
        public uint nMaxFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrFileTitle;
        public uint nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpTemplateName;
        public IntPtr pvReserved;
        public uint dwReserved;
        public uint FlagsEx;
    }

    // user32
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    public static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hwnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    public static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);

    [DllImport("user32.dll")]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    public static extern bool PeekMessage(out MSG lpMsg, IntPtr hwnd,
        uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int nIndex, IntPtr newLong);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc,
        IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool SetWindowText(IntPtr hwnd, string lpString);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int MessageBox(IntPtr hwnd, string lpText, string lpCaption, uint uType);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetModuleHandle([MarshalAs(UnmanagedType.LPWStr)] string? lpModuleName);

    // shcore
    [DllImport("shcore.dll")]
    public static extern int SetProcessDpiAwarenessContext(IntPtr value);

    // comdlg32
    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetOpenFileName(ref OPENFILENAME lpofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetSaveFileName(ref OPENFILENAME lpofn);

    // shell32
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr ShellExecute(IntPtr hwnd,
        string lpOperation, string lpFile,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpParameters,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpDirectory,
        int nShowCmd);
}
