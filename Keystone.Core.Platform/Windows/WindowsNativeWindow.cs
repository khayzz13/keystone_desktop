// WindowsNativeWindow — Win32 HWND wrapper implementing INativeWindow.
// Uses a subclassed WndProc to intercept resize/move/close events.

using System.Runtime.InteropServices;
using System.Text;

namespace Keystone.Core.Platform.Windows;

public class WindowsNativeWindow : INativeWindow
{
    private IntPtr _hwnd;
    private readonly WindowsPlatform _platform;
    private INativeWindowDelegate? _delegate;
    private bool _disposed;

    // Cached for render thread reads
    private double _cachedScale;
    private double _cachedW, _cachedH;

    // Subclassed WndProc — must be kept alive as a field to prevent GC
    private readonly Win32.WndProcDelegate _wndProc;
    private IntPtr _prevWndProc;

    // Window style constants
    private const uint WS_POPUP       = 0x80000000;
    private const uint WS_VISIBLE     = 0x10000000;
    private const uint WS_EX_LAYERED  = 0x00080000;
    private const uint WS_EX_NOREDIRECTBITMAP = 0x00200000;
    private const uint WS_EX_TOPMOST  = 0x00000008;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;

    // WM_ message constants
    private const uint WM_SIZE         = 0x0005;
    private const uint WM_MOVE         = 0x0003;
    private const uint WM_CLOSE        = 0x0010;
    private const uint WM_DESTROY      = 0x0002;
    private const uint WM_ENTERSIZEMOVE = 0x0231;
    private const uint WM_EXITSIZEMOVE  = 0x0232;
    private const uint WM_NCLBUTTONDOWN = 0x00A1;

    // SetWindowPos flags
    private const uint SWP_NOSIZE   = 0x0001;
    private const uint SWP_NOMOVE   = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private static readonly IntPtr HWND_TOPMOST   = new IntPtr(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

    public WindowsNativeWindow(WindowsPlatform platform, WindowConfig config, bool isOverlay)
    {
        _platform = platform;

        uint exStyle = WS_EX_LAYERED | WS_EX_NOREDIRECTBITMAP;
        if (config.Floating || isOverlay)
            exStyle |= WS_EX_TOPMOST;

        _hwnd = Win32.CreateWindowEx(
            exStyle,
            WindowsPlatform.WindowClassName,
            "",
            WS_POPUP,
            (int)config.X, (int)config.Y,
            (int)config.Width, (int)config.Height,
            IntPtr.Zero, IntPtr.Zero,
            Win32.GetModuleHandle(null),
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        // Cache initial state
        var dpi = Win32.GetDpiForWindow(_hwnd);
        _cachedScale = dpi > 0 ? dpi / 96.0 : 1.0;
        _cachedW = config.Width;
        _cachedH = config.Height;

        // Subclass WndProc to receive window messages
        _wndProc = WndProc;
        _prevWndProc = Win32.SetWindowLongPtr(_hwnd, -4 /* GWLP_WNDPROC */,
            Marshal.GetFunctionPointerForDelegate(_wndProc));

        _platform.TrackWindow(_hwnd);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ENTERSIZEMOVE:
                _delegate?.OnResizeStarted();
                break;

            case WM_SIZE:
                UpdateCachedSize();
                _delegate?.OnResized(_cachedW, _cachedH);
                break;

            case WM_EXITSIZEMOVE:
                UpdateCachedSize();
                _delegate?.OnResizeEnded();
                break;

            case WM_MOVE:
                int mx = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                int my = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                _delegate?.OnMoved(mx, my);
                break;

            case WM_DESTROY:
                _delegate?.OnClosed();
                _platform.UntrackWindow(hwnd);
                break;
        }

        return Win32.CallWindowProc(_prevWndProc, hwnd, msg, wParam, lParam);
    }

    private void UpdateCachedSize()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (Win32.GetClientRect(_hwnd, out var cr))
        {
            var dpi = Win32.GetDpiForWindow(_hwnd);
            _cachedScale = dpi > 0 ? dpi / 96.0 : 1.0;
            _cachedW = cr.Right - cr.Left;
            _cachedH = cr.Bottom - cr.Top;
        }
    }

    // --- INativeWindow ---

    public IntPtr Handle => _hwnd;

    public string Title
    {
        get
        {
            var sb = new StringBuilder(512);
            Win32.GetWindowText(_hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        set => Win32.SetWindowText(_hwnd, value);
    }

    public double ScaleFactor => _cachedScale > 0 ? _cachedScale : 1.0;

    public (double w, double h) ContentBounds => (_cachedW, _cachedH);

    public (double x, double y, double w, double h) Frame
    {
        get
        {
            if (Win32.GetWindowRect(_hwnd, out var r))
                return (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return (0, 0, _cachedW, _cachedH);
        }
    }

    public (double x, double y) MouseLocationInWindow
    {
        get
        {
            if (Win32.GetCursorPos(out var pt))
            {
                Win32.ScreenToClient(_hwnd, ref pt);
                return (pt.X, pt.Y);
            }
            return (0, 0);
        }
    }

    public void SetFrame(double x, double y, double w, double h, bool animate = false)
    {
        Win32.SetWindowPos(_hwnd, IntPtr.Zero,
            (int)x, (int)y, (int)w, (int)h,
            SWP_NOZORDER | SWP_NOACTIVATE);
        _cachedW = w;
        _cachedH = h;
    }

    public void SetFloating(bool floating)
    {
        var insertAfter = floating ? HWND_TOPMOST : HWND_NOTOPMOST;
        Win32.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE);
    }

    public void StartDrag()
    {
        Win32.ReleaseCapture();
        Win32.PostMessage(_hwnd, WM_NCLBUTTONDOWN, new IntPtr(2 /* HTCAPTION */), IntPtr.Zero);
    }

    public void Show()
    {
        Win32.ShowWindow(_hwnd, 5 /* SW_SHOW */);
        Win32.SetForegroundWindow(_hwnd);
    }

    public void Hide() => Win32.ShowWindow(_hwnd, 0 /* SW_HIDE */);

    public void BringToFront() => Win32.SetForegroundWindow(_hwnd);

    public void Minimize() => Win32.ShowWindow(_hwnd, 6 /* SW_MINIMIZE */);

    public void Deminiaturize() => Win32.ShowWindow(_hwnd, 9 /* SW_RESTORE */);

    public void Zoom() => Win32.ShowWindow(_hwnd, 3 /* SW_MAXIMIZE */);

    public void Close() => Win32.PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

    public void SetDelegate(INativeWindowDelegate del) => _delegate = del;

    public void CreateWebView(Action<IWebView> callback)
    {
        var hwnd = _hwnd;
        _ = Task.Run(async () =>
        {
            var wv = new WindowsWebView(hwnd);
            await wv.InitializeAsync();
            callback(wv);
        });
    }

    public object? GetGpuSurface()
    {
        // Return the HWND — D3DGpuContext creates the DXGI swap chain from it.
        // Returns null (IntPtr.Zero) if disposed.
        return _hwnd == IntPtr.Zero ? null : _hwnd;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
