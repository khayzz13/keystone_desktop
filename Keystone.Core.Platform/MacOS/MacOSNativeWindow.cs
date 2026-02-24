using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;
using WebKit;

namespace Keystone.Core.Platform.MacOS;

public class MacOSNativeWindow : INativeWindow
{
    private readonly NSWindow _nsWindow;
    private readonly NSView _contentView;
    private readonly CAMetalLayer _metalLayer;
    private KeystoneWindowDelegate? _delegate;

    // Shared across all windows — preserves current behavior
    private static WKProcessPool? _sharedPool;

    // Cached values for thread-safe reads
    private double _cachedScale;
    private (double w, double h) _cachedBounds;

    internal MacOSNativeWindow(NSWindow nsWindow)
    {
        _nsWindow = nsWindow;
        _contentView = nsWindow.ContentView!;
        _metalLayer = CreateMetalLayer(_contentView);

        // Initialize cached values on main thread
        _cachedScale = (double)_nsWindow.BackingScaleFactor;
        _cachedBounds = ((double)_contentView.Bounds.Width, (double)_contentView.Bounds.Height);
    }

    public IntPtr Handle => _nsWindow.Handle;

    public string Title
    {
        get => _nsWindow.Title;
        set => _nsWindow.Title = value;
    }

    public double ScaleFactor
    {
        get
        {
            if (NSThread.IsMain)
                _cachedScale = (double)_nsWindow.BackingScaleFactor;
            return _cachedScale;
        }
    }

    public (double w, double h) ContentBounds
    {
        get
        {
            if (NSThread.IsMain)
            {
                var b = _contentView.Bounds;
                _cachedBounds = ((double)b.Width, (double)b.Height);
            }
            return _cachedBounds;
        }
    }

    public (double x, double y, double w, double h) Frame
    {
        get
        {
            var f = _nsWindow.Frame;
            return ((double)f.X, (double)f.Y, (double)f.Width, (double)f.Height);
        }
    }

    public (double x, double y) MouseLocationInWindow
    {
        get
        {
            var p = _nsWindow.MouseLocationOutsideOfEventStream;
            return ((double)p.X, (double)p.Y);
        }
    }

    public void SetFrame(double x, double y, double w, double h, bool animate = false)
        => _nsWindow.SetFrame(new CGRect(x, y, w, h), true, animate);

    public void SetFloating(bool floating)
    {
        _nsWindow.CollectionBehavior = floating
            ? NSWindowCollectionBehavior.Stationary | NSWindowCollectionBehavior.FullScreenAuxiliary
            : 0;
        _nsWindow.Level = floating ? NSWindowLevel.Floating : NSWindowLevel.Normal;
    }

    public void StartDrag()
    {
        var currentEvent = NSApplication.SharedApplication.CurrentEvent;
        if (currentEvent != null)
            _nsWindow.PerformWindowDrag(currentEvent);
    }

    public void Show() => _nsWindow.MakeKeyAndOrderFront(null);
    public void Hide() => _nsWindow.OrderOut(null);
    public void BringToFront() => _nsWindow.OrderFront(null);
    public void Minimize() => _nsWindow.Miniaturize(null);
    public void Deminiaturize() => _nsWindow.Deminiaturize(null);
    public void Zoom() => _nsWindow.Zoom(null);
    public void Close() => _nsWindow.Close();

    public void SetDelegate(INativeWindowDelegate del)
    {
        _delegate = new KeystoneWindowDelegate(del);
        _nsWindow.WeakDelegate = _delegate;
    }

    public void CreateWebView(Action<IWebView> callback)
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            _sharedPool ??= new WKProcessPool();
            var wv = new MacOSWebView(_contentView, _sharedPool);
            callback(wv);
        });
    }

    public object? GetGpuSurface() => _metalLayer;

    public void Dispose()
    {
        _delegate = null;
    }

    // --- Internal for dock menu ---
    internal NSWindow NSWindowObject => _nsWindow;

    // --- Metal layer creation (moved from NativeAppKit.MakeLayerBacked) ---

    private static CAMetalLayer CreateMetalLayer(NSView view)
    {
        view.WantsLayer = true;

        // Create a Metal subview instead of making the parent view layer-hosting.
        // Layer-hosting views (view.Layer = custom) don't support subviews —
        // WKWebView needs to be a sibling subview of the Metal view.
        var metalView = new NSView(view.Bounds);
        metalView.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        metalView.WantsLayer = true;
        var metalLayer = new CAMetalLayer();
        metalView.Layer = metalLayer;
        view.AddSubview(metalView);

        return metalLayer;
    }

    // === Window Delegate (moved from NativeAppKit.cs) ===

    private class KeystoneWindowDelegate : NSObject, INSWindowDelegate
    {
        private readonly INativeWindowDelegate _del;

        public KeystoneWindowDelegate(INativeWindowDelegate del) => _del = del;

        [Export("windowWillStartLiveResize:")]
        public void WillStartLiveResize(NSNotification notification) => _del.OnResizeStarted();

        [Export("windowDidEndLiveResize:")]
        public void DidEndLiveResize(NSNotification notification) => _del.OnResizeEnded();

        [Export("windowDidResize:")]
        public void DidResize(NSNotification notification)
        {
            // Get new size from window
            var nsWindow = notification.Object as NSWindow;
            if (nsWindow != null)
            {
                var b = nsWindow.ContentView!.Bounds;
                _del.OnResized((double)b.Width, (double)b.Height);
            }
        }

        [Export("windowDidMove:")]
        public void DidMove(NSNotification notification)
        {
            var nsWindow = notification.Object as NSWindow;
            if (nsWindow != null)
            {
                var f = nsWindow.Frame;
                _del.OnMoved((double)f.X, (double)f.Y);
            }
        }
    }
}
