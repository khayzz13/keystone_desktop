/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using AppKit;
using CoreAnimation;
using CoreGraphics;
using Foundation;
using ObjCRuntime;

namespace Keystone.Core.Platform.MacOS;

public class MacOSNativeWindow : INativeWindow
{
    private readonly NSWindow _nsWindow;
    private readonly NSView _contentView;
    private readonly CAMetalLayer? _metalLayer;
    private NSView? _metalView;
    private KeystoneWindowDelegate? _delegate;

    /// <summary>Optional scheme handler — passed to WebViews created by this window.</summary>
    public KeystoneSchemeHandler? SchemeHandler { get; set; }

    // Cached values for thread-safe reads
    private double _cachedScale;
    private (double w, double h) _cachedBounds;

    internal MacOSNativeWindow(NSWindow nsWindow, bool renderless = false)
    {
        _nsWindow = nsWindow;
        _contentView = nsWindow.ContentView!;
        _metalLayer = renderless ? null : CreateMetalLayer(_contentView);

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

    // ── Window semantics ──────────────────────────────────────────────────

    public void SetMinSize(double w, double h)
        => _nsWindow.MinSize = new CGSize(w, h);

    public void SetMaxSize(double w, double h)
        => _nsWindow.MaxSize = new CGSize(w, h);

    public void SetAspectRatio(double ratio)
    {
        if (ratio <= 0)
            _nsWindow.ResizeIncrements = new CGSize(1, 1); // clear
        else
            _nsWindow.ContentAspectRatio = new CGSize(ratio, 1);
    }

    public void SetOpacity(double opacity)
    {
        _nsWindow.IsOpaque = opacity >= 1.0;
        _nsWindow.AlphaValue = (nfloat)Math.Clamp(opacity, 0.0, 1.0);
    }

    public void EnterFullscreen()
    {
        if (!IsFullscreen)
            _nsWindow.ToggleFullScreen(null);
    }

    public void ExitFullscreen()
    {
        if (IsFullscreen)
            _nsWindow.ToggleFullScreen(null);
    }

    public bool IsFullscreen =>
        (_nsWindow.StyleMask & NSWindowStyle.FullScreenWindow) != 0;

    public bool IsMinimized => _nsWindow.IsMiniaturized;

    public bool IsFocused => _nsWindow.IsKeyWindow;

    public void SetContentProtection(bool enabled)
        => _nsWindow.SharingType = enabled ? NSWindowSharingType.None : NSWindowSharingType.ReadOnly;

    public void SetIgnoreMouseEvents(bool ignore)
        => _nsWindow.IgnoresMouseEvents = ignore;

    public void SetResizable(bool resizable)
    {
        if (resizable)
            _nsWindow.StyleMask |= NSWindowStyle.Resizable;
        else
            _nsWindow.StyleMask &= ~NSWindowStyle.Resizable;
    }

    public void SetParent(INativeWindow? parent)
    {
        if (_nsWindow.ParentWindow != null)
            _nsWindow.ParentWindow.RemoveChildWindow(_nsWindow);

        if (parent is MacOSNativeWindow macParent)
            macParent._nsWindow.AddChildWindow(_nsWindow, NSWindowOrderingMode.Above);
    }

    public object? GetContentView() => _contentView;

    // ─────────────────────────────────────────────────────────────────────

    public void SetDelegate(INativeWindowDelegate del)
    {
        _delegate = new KeystoneWindowDelegate(del);
        _nsWindow.WeakDelegate = _delegate;
    }

    public void CreateWebView(Action<IWebView> callback)
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            var wv = new MacOSWebView(_contentView, SchemeHandler);
            callback(wv);
        });
    }

    public object? GetGpuSurface() => _metalLayer;

    public void Dispose()
    {
        _nsWindow.WeakDelegate = null;
        _delegate = null;

        if (_metalView != null)
        {
            if (_metalLayer != null)
                _metalLayer.DrawableSize = new CGSize(0, 0);
            _metalView.RemoveFromSuperview();
            _metalView = null;
        }
    }

    // --- Internal for dock menu ---
    internal NSWindow NSWindowObject => _nsWindow;

    // --- Metal layer creation (moved from NativeAppKit.MakeLayerBacked) ---

    private CAMetalLayer CreateMetalLayer(NSView view)
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
        _metalView = metalView;

        return metalLayer;
    }

    // === Window Delegate ===

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
            var nsWindow = notification.Object as NSWindow;
            if (nsWindow != null)
            {
                var b = nsWindow.ContentView!.Bounds;
                _del.OnResized((double)b.Width, (double)b.Height);
            }
        }

        [Export("windowWillClose:")]
        public void WillClose(NSNotification notification) => _del.OnClosed();

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

        [Export("windowDidBecomeKey:")]
        public void DidBecomeKey(NSNotification notification) => _del.OnFocused();

        [Export("windowDidResignKey:")]
        public void DidResignKey(NSNotification notification) => _del.OnBlurred();

        [Export("windowDidMiniaturize:")]
        public void DidMiniaturize(NSNotification notification) => _del.OnMiniaturized();

        [Export("windowDidDeminiaturize:")]
        public void DidDeminiaturize(NSNotification notification) => _del.OnDeminiaturized();

        [Export("windowDidEnterFullScreen:")]
        public void DidEnterFullScreen(NSNotification notification) => _del.OnEnteredFullscreen();

        [Export("windowDidExitFullScreen:")]
        public void DidExitFullScreen(NSNotification notification) => _del.OnExitedFullscreen();
    }
}
