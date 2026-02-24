// LinuxNativeWindow — GTK4 window wrapper implementing INativeWindow.
// Wraps a GtkApplicationWindow with an overlay layout (drawing area + WebView container).

using System.Runtime.InteropServices;

namespace Keystone.Core.Platform.Linux;

public class LinuxNativeWindow : INativeWindow
{
    private IntPtr _window;     // GtkApplicationWindow*
    private IntPtr _overlay;    // GtkOverlay* (container for GPU surface + WebViews)
    private IntPtr _drawArea;   // GtkDrawingArea* (Vulkan render target)
    private IntPtr _surface;    // GdkSurface* (cached for Vulkan surface creation)
    private INativeWindowDelegate? _delegate;
    private bool _disposed;
    private readonly bool _isOverlay;

    // Cached for render thread reads
    private double _cachedScale;
    private double _cachedW, _cachedH;

    // Signal handler IDs for cleanup
    private ulong _resizeSignalId;
    private ulong _closeSignalId;

    // Must prevent delegates from being GC'd while connected as signal handlers
    private GtkCallbacks.GtkResizeCallback? _resizeCallback;
    private GtkCallbacks.GtkCloseRequestCallback? _closeCallback;

    public LinuxNativeWindow(IntPtr app, WindowConfig config, bool isOverlay)
    {
        _isOverlay = isOverlay;

        // Create application window
        _window = Gtk.ApplicationWindowNew(app);
        Gtk.WindowSetDefaultSize(_window, (int)config.Width, (int)config.Height);

        // Title bar style
        if (config.TitleBarStyle == "hidden")
        {
            Gtk.WindowSetDecorated(_window, false);
        }

        if (config.Floating || isOverlay)
        {
            Gtk.WindowSetKeepAbove(_window, true);
        }

        // Layout: overlay container with drawing area as base + WebViews on top
        _overlay = Gtk.OverlayNew();
        _drawArea = Gtk.DrawingAreaNew();
        Gtk.WidgetSetHexpand(_drawArea, true);
        Gtk.WidgetSetVexpand(_drawArea, true);
        Gtk.WindowSetChild(_window, _overlay);
        // Drawing area is the base child of the overlay
        // (set as child of overlay via gtk_overlay — base child is set via gtk_window_set_child pattern)

        // Cache initial state
        _cachedScale = Gtk.WidgetGetScaleFactor(_window);
        _cachedW = config.Width;
        _cachedH = config.Height;

        // Connect resize signal
        _resizeCallback = OnResize;
        _resizeSignalId = GLib.SignalConnectData(_window, "notify::default-width",
            (inst, _) => { UpdateCachedSize(); _delegate?.OnResized(_cachedW, _cachedH); },
            IntPtr.Zero, IntPtr.Zero, 0);

        // Connect close-request signal
        _closeCallback = OnCloseRequest;
        // close-request uses a boolean return, connect separately
        GLib.SignalConnectData(_window, "close-request",
            (inst, _) => { _delegate?.OnClosed(); },
            IntPtr.Zero, IntPtr.Zero, 0);
    }

    private void OnResize(IntPtr widget, int width, int height, IntPtr userData)
    {
        UpdateCachedSize();
        _delegate?.OnResized(_cachedW, _cachedH);
    }

    private bool OnCloseRequest(IntPtr widget, IntPtr userData)
    {
        _delegate?.OnClosed();
        return false; // allow default close behavior
    }

    private void UpdateCachedSize()
    {
        _cachedW = Gtk.WidgetGetWidth(_window);
        _cachedH = Gtk.WidgetGetHeight(_window);
        _cachedScale = Gtk.WidgetGetScaleFactor(_window);
    }

    // --- INativeWindow ---

    public IntPtr Handle
    {
        get
        {
            if (_surface == IntPtr.Zero)
            {
                var native = Gtk.WidgetGetNative(_window);
                if (native != IntPtr.Zero)
                    _surface = Gtk.NativeGetSurface(native);
            }
            return _surface;
        }
    }

    public string Title
    {
        get => Gtk.WindowGetTitle(_window) ?? "";
        set => Gtk.WindowSetTitle(_window, value);
    }

    public double ScaleFactor => _cachedScale > 0 ? _cachedScale : Gtk.WidgetGetScaleFactor(_window);

    public (double w, double h) ContentBounds => (_cachedW, _cachedH);

    public (double x, double y, double w, double h) Frame
    {
        get
        {
            // GTK4 on Wayland doesn't expose absolute window position.
            // Return content bounds with (0,0) position.
            return (0, 0, _cachedW, _cachedH);
        }
    }

    public (double x, double y) MouseLocationInWindow
    {
        get
        {
            // GTK4 doesn't have a simple query-pointer-in-widget API.
            // Mouse position is tracked via event controllers instead.
            // Return last known position (would be updated by event system).
            return (0, 0);
        }
    }

    public void SetFrame(double x, double y, double w, double h, bool animate = false)
    {
        Gtk.WindowSetDefaultSize(_window, (int)w, (int)h);
        // GTK4/Wayland doesn't support setting window position programmatically
        _cachedW = w;
        _cachedH = h;
    }

    public void SetFloating(bool floating) => Gtk.WindowSetKeepAbove(_window, floating);

    public void StartDrag()
    {
        // GTK4: Would need GdkToplevel.BeginMove() with a GdkEvent
        // This requires an active gesture/event context
    }

    public void Show()
    {
        Gtk.WidgetSetVisible(_window, true);
        Gtk.WindowPresent(_window);
    }

    public void Hide() => Gtk.WidgetSetVisible(_window, false);

    public void BringToFront() => Gtk.WindowPresent(_window);

    public void Minimize() => Gtk.WindowMinimize(_window);

    public void Deminiaturize() => Gtk.WindowUnminimize(_window);

    public void Zoom() => Gtk.WindowMaximize(_window);

    public void Close() => Gtk.WindowClose(_window);

    public void SetDelegate(INativeWindowDelegate del) => _delegate = del;

    public void CreateWebView(Action<IWebView> callback)
    {
        var webView = new LinuxWebView(_window, _overlay);
        callback(webView);
    }

    public object? GetGpuSurface()
    {
        // Return the GdkSurface handle for Vulkan surface creation.
        // The Vulkan backend will use this to create VkSurfaceKHR.
        var native = Gtk.WidgetGetNative(_window);
        if (native == IntPtr.Zero) return null;
        var surface = Gtk.NativeGetSurface(native);
        if (surface == IntPtr.Zero) return null;
        _surface = surface;
        return surface;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_window != IntPtr.Zero)
        {
            Gtk.WindowDestroy(_window);
            _window = IntPtr.Zero;
        }
    }
}
