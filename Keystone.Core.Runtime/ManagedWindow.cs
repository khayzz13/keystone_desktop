/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Diagnostics;
using Keystone.Core;
using Keystone.Core.Management.Bun;
using Keystone.Core.Platform;
using Keystone.Core.Plugins;
using Keystone.Core.Rendering;

namespace Keystone.Core.Runtime;

/// <summary>
/// Unified window that owns plugin, renderer, frame timing, and event handling.
/// Rendering happens on a per-window thread (WindowRenderThread).
/// Input handling stays on the main thread (HitTest → ActionRouter).
/// </summary>
public class ManagedWindow : IDisposable
{
    private static uint _nextWindowId = 1;

    private volatile IWindowPlugin _plugin;
    private readonly object _pluginLock = new();
    private readonly ActionRouter _actionRouter;
    private readonly IPlatform _platform;
    private readonly FrameState _frameState;
    private readonly uint _windowId;
    private volatile bool _pendingReload;
    private readonly Stopwatch _frameClock = Stopwatch.StartNew();
    private ulong _lastFrameMs;

    private INativeWindow? _nativeWindow;
    private object? _gpuSurface; // Platform GPU surface (CAMetalLayer on macOS, VkSurfaceKHR on Linux)
    private SceneRenderer? _sceneRenderer;

    // GPU lifecycle — context, VSync, render thread, paint cache
    private WindowGpuHost? _gpuHost;

    private float _scale = 1.0f;
    private uint _width;
    private uint _height;
    private uint _lastDrawW, _lastDrawH; // last DrawableSize set on render thread
    private volatile bool _isLiveResizing;
    private volatile bool _purgeAfterResize;
    private bool _disposed;

    /// <summary>Build the base URL for WebView loading — uses custom scheme if enabled, else loopback HTTP.</summary>
    private string GetBaseUrl(int port)
    {
        var config = ApplicationRuntime.Instance?.Config;
        if (config?.CustomScheme == true)
            return $"{config.ResolvedSchemeName}://app";
        return $"http://127.0.0.1:{port}";
    }

    // WebView hosting — slots, externals, crash recovery
    private WindowWebHost? _webHost;

    // Invoke routing — handler registration, dispatch, cancellation
    private readonly WindowInvokeRouter _invokeRouter;

    public void RegisterInvokeHandler(string channel, Func<System.Text.Json.JsonElement, Task<object?>> handler)
        => _invokeRouter.RegisterHandler(channel, handler);

    public void RegisterInvokeHandler(string channel, Func<System.Text.Json.JsonElement, CancellationToken, Task<object?>> handler)
        => _invokeRouter.RegisterHandler(channel, handler);

    public void RegisterMainThreadInvokeHandler(string channel, Func<System.Text.Json.JsonElement, object?> handler)
        => _invokeRouter.RegisterMainThreadHandler(channel, handler);

    public Action<string>? OnDirectMessage { get => _invokeRouter.OnDirectMessage; set => _invokeRouter.OnDirectMessage = value; }

    /// <summary>
    /// Fired when the WKWebView content process terminates unexpectedly.
    /// Subscribe to show a UI indicator or log the event.
    /// If null (default), the window still auto-reloads per ProcessRecoveryConfig.
    /// </summary>
    public Action? OnWebViewCrash
    {
        get => _webHost?.OnCrash;
        set { if (_webHost != null) _webHost.OnCrash = value; }
    }

    // Expose for event coordinate transform
    public float ScaleFactor => _scale;
    public uint Width => _width;
    public uint Height => _height;
    private CursorType _currentCursor = CursorType.Default;
    private volatile bool _needsRedraw = true;
    private IDisposable? _dataSubscription; // ChannelManager render-wake — wakes render thread on data arrival

    // Identity
    public string Id { get; }
    public string WindowType { get; }
    public IntPtr Handle => _nativeWindow?.Handle ?? IntPtr.Zero;
    public INativeWindow? NativeWindow => _nativeWindow;
    public string? ParentWindowId { get; set; }

    // Observable state (updated by delegate callbacks, read from any thread)
    public bool IsFocused { get; private set; }
    public bool IsMinimized { get; private set; }
    public bool IsFullscreen { get; private set; }

    public IWindowPlugin GetPlugin() { lock (_pluginLock) return _plugin; }
    public object? GetGpuContext() => _gpuHost?.GpuContext;

    /// <summary>Expected drawable pool bytes for this window's GPU surface.
    /// 3 drawables × width × height × 4 bytes (BGRA).</summary>
    public long ExpectedIOSurfaceBytes =>
        _lastDrawW == 0 || _lastDrawH == 0 ? 0 : (long)_lastDrawW * _lastDrawH * 4 * 3;

    /// <summary>Request aggressive GPU resource purge on next render frame.</summary>
    public void RequestGpuPurge() => _purgeAfterResize = true;

    public void SwapPlugin(IWindowPlugin newPlugin)
    {
        lock (_pluginLock)
        {
            if (_plugin is IStatefulPlugin oldStateful && newPlugin is IStatefulPlugin newStateful)
                newStateful.RestoreState(oldStateful.SerializeState());
            (_plugin as IDisposable)?.Dispose();
            _sceneRenderer?.Dispose();
            _sceneRenderer = null;
            _plugin = newPlugin;
            _pendingReload = false;
        }
        RequestRedraw();
        Console.WriteLine($"[ManagedWindow] Plugin swapped for {Id}");
    }

    public void SetPendingReload(bool pending) => _pendingReload = pending;

    // Layout
    public WindowLayoutMode LayoutMode { get; set; } = WindowLayoutMode.Standalone;
    public string? ContainerId { get; set; }
    public string? GroupId { get; set; }

    // Always-on-top state
    public bool AlwaysOnTop { get; set; } = false;

    // Native window controls present (traffic lights on macOS, GTK decorations on Linux)
    public bool HasNativeControls { get; set; }

    // Overlay anchor position
    public float OverlayAnchorX { get; set; }

    // Overlay callbacks
    public Action<IOverlayContent, double, double, double, double>? OnShowOverlay { get; set; }
    public Action? OnCloseOverlay { get; set; }

    // Input state (transformed coordinates — written by main thread, read by render thread)
    public float MouseX { get; private set; }
    public float MouseY { get; private set; }
    public bool MouseDown { get; private set; }

    // Tab drag state
    private string? _draggingTabId;
    private string? _pendingTabSelectId;
    private float _tabDragStartX;
    private float _tabDragStartY;
    public Action<string, string, float>? OnTabDraggedOut { get; set; }

    // Global state callbacks
    public Func<bool>? GetBindModeActive { get; set; }
    public Func<string, bool>? GetIsSelectedForBind { get; set; }
    public Func<string, (string[] ids, string[] titles, string activeId)?>? GetTabGroupInfo { get; set; }

    public ManagedWindow(string id, IWindowPlugin plugin, ActionRouter actionRouter, IPlatform platform)
    {
        Id = id;
        _plugin = plugin;
        _platform = platform;
        WindowType = plugin.WindowType;
        _actionRouter = actionRouter;
        _invokeRouter = new WindowInvokeRouter(id,
            (ch, data) => BunManager.Instance.Push(ch, data),
            js => EvaluateJavaScript(js),
            action => actionRouter.Execute(action, id));
        _windowId = _nextWindowId++;
        _frameState = new FrameState { WindowId = _windowId, WindowType = plugin.WindowType };
        if (plugin is WindowPluginBase wpb)
        {
            wpb.WindowId = _windowId;
            wpb.ShowOverlay = (content, w, h) =>
            {
                if (_nativeWindow == null) return;
                var (fx, fy, fw, fh) = _nativeWindow.Frame;
                var screenX = fx + OverlayAnchorX / _scale;
                var screenY = fy;
                OnShowOverlay?.Invoke(content, screenX, screenY, w, h);
            };
            wpb.CloseOverlay = () => OnCloseOverlay?.Invoke();
        }
    }

    public void OnCreated(INativeWindow nativeWindow)
    {
        _nativeWindow = nativeWindow;

        UpdateSize();

        // WebView hosting
        _webHost = new WindowWebHost(
            Id,
            () => BunManager.Instance.BunPort,
            () => BunManager.Instance.SessionToken,
            GetBaseUrl,
            action => ApplicationRuntime.Instance?.RunOnMainThread(action),
            windowId => ApplicationRuntime.Instance?.RaiseWebViewCrash(windowId),
            ApplicationRuntime.Instance?.Config.ProcessRecovery,
            _invokeRouter.Dispatch,
            Environment.GetEnvironmentVariable("KEYSTONE_INSPECTABLE") == "1");

        // GPU path — platform-specific surface + context creation
        _gpuSurface = nativeWindow.GetGpuSurface();
        if (_gpuSurface != null)
        {
            _gpuHost = new WindowGpuHost(ApplicationRuntime.Instance!.DisplayLink);
            _gpuHost.Initialize(_gpuSurface, _scale, this);
        }

        // Set up window delegate for live resize
        _nativeWindow.SetDelegate(new NativeWindowDelegate(this));

        // Subscribe to data channels — RequestRedraw wakes the render thread when data arrives
        var deps = _plugin.Dependencies;
        if (deps != null && deps.Any())
            _dataSubscription = ChannelManager.Instance.Subscribe(deps, RequestRedraw);

        // Start render thread
        _gpuHost?.Start();

        Console.WriteLine($"[ManagedWindow] Created {WindowType} id={Id} size={_width}x{_height} scale={_scale}");
    }


    /// <summary>
    /// Read size from platform (main thread only) and update cached fields.
    /// Render thread skips platform reads — uses cached values from last main-thread update.
    /// </summary>
    private void UpdateSize()
    {
        if (_nativeWindow == null) return;

        _scale = (float)_nativeWindow.ScaleFactor;
        var (bw, bh) = _nativeWindow.ContentBounds;
        _width = (uint)(bw * _scale);
        _height = (uint)(bh * _scale);

        _frameState.Width = _width;
        _frameState.Height = _height;
        _frameState.ScaleFactor = _scale;
    }

    // --- Frame Timing / VSync Suspension ---

    /// <summary>Request a redraw. Wakes the render thread if it's suspended.</summary>
    public void RequestRedraw()
    {
        _needsRedraw = true;
        _gpuHost?.WakeRenderThread();
    }

    /// <summary>Called by render thread: resubscribe to VSync if suspended.</summary>
    internal void ResumeVSync() => _gpuHost?.ResumeVSync();

    /// <summary>Called by render thread: unsubscribe from VSync when idle.</summary>
    internal void TrySuspendVSync() => _gpuHost?.TrySuspendVSync();

    public bool ShouldRender() => _needsRedraw;

    public void EvaluateJavaScript(string js) => _webHost?.EvaluateJavaScript(js);
    public void EvaluateJavaScriptWithResult(string js, Action<string?> completion)
    {
        if (_webHost == null) { completion(null); return; }
        _webHost.EvaluateJavaScriptWithResult(js, completion);
    }
    public void SetWebViewInspectable(bool enabled) => _webHost?.SetInspectable(enabled);
    public Task<string?> GetServiceWorkerStatus() => _webHost?.GetServiceWorkerStatus() ?? Task.FromResult<string?>(null);
    public void UnregisterServiceWorkers() => _webHost?.UnregisterServiceWorkers();
    public void ClearServiceWorkerCaches() => _webHost?.ClearServiceWorkerCaches();
    public void SetNavigationPolicy(Func<string, bool>? policy) => _webHost?.SetNavigationPolicy(policy);

    /// <summary>
    /// Render on the per-window thread using its own GpuContext.
    /// Called by WindowRenderThread.
    /// </summary>
    public void RenderOnThread(IWindowGpuContext gpu)
    {
        var paintCache = _gpuHost?.PaintCache;
        if (_gpuSurface == null || paintCache == null) return;

        UpdateSize();
        if (_width == 0 || _height == 0) return;

        // During live resize, freeze drawable size — render at the pre-resize resolution
        // and let the compositor scale. Drawable size only updates when resize ENDS.
        if (!_isLiveResizing && (_width != _lastDrawW || _height != _lastDrawH))
        {
            gpu.SetDrawableSize(_gpuSurface, _width, _height);
            _lastDrawW = _width;
            _lastDrawH = _height;
        }

        // Render at the actual drawable dimensions — during resize this is the old size
        var drawW = _lastDrawW; var drawH = _lastDrawH;
        if (drawW == 0 || drawH == 0) return;
        _frameState.Width = drawW;
        _frameState.Height = drawH;

        SyncFrameState();
        _frameState.GpuContext = gpu;

        SceneNode? scene = null;
        var hasRetainedScene = false;

        lock (_pluginLock)
        {
            if (!_pendingReload)
            {
                _frameState.WindowTitle = _plugin.WindowTitle;
                _frameState.NeedsRedraw = false;

                scene = _plugin.BuildScene(_frameState);
                hasRetainedScene = scene != null;
            }
        }

        var canvas = gpu.BeginFrame(_gpuSurface, (int)drawW, (int)drawH);
        if (canvas == null) return;

        try
        {
            canvas.Clear(SkiaSharp.SKColors.Black);

            lock (_pluginLock)
            {
                if (!_pendingReload)
                {
                    if (hasRetainedScene && scene != null)
                    {
                        _sceneRenderer ??= new SceneRenderer();
                        _sceneRenderer.Render(canvas, paintCache, _frameState, scene);
                        _needsRedraw = _frameState.NeedsRedraw;
                    }
                    else
                    {
                        using var ctx = new RenderContext(canvas, paintCache, _frameState);
                        _plugin.Render(ctx);
                        _needsRedraw = (ctx.Flags & RenderContext.FLAG_NEEDS_REDRAW) != 0;
                    }
                }
            }

            OverlayAnchorX = _frameState.OverlayAnchorX;
        }
        finally
        {
            gpu.FinishAndPresent();
        }

        // After resize ends (or when requested by MemWatch), aggressively purge
        // all stale IOSurface-backed textures from the GRContext cache.
        if (_purgeAfterResize)
        {
            _purgeAfterResize = false;
            gpu.ForceFullPurge();
        }

        // Process WebView requests from FlexRenderer
        ProcessWebViewRequests();

        // Reset transient state
        _frameState.MouseClicked = false;
        _frameState.RightClick = false;
        _frameState.MouseScroll = 0;
    }

    private void SyncFrameState()
    {
        // Frame timing
        ulong nowMs = (ulong)_frameClock.ElapsedMilliseconds;
        _frameState.DeltaMs = (uint)(nowMs - _lastFrameMs);
        _frameState.TimeMs = nowMs;
        _frameState.FrameCount++;
        _lastFrameMs = nowMs;

        _frameState.MouseX = MouseX;
        _frameState.MouseY = MouseY;
        _frameState.MouseDown = MouseDown;
        _frameState.BindModeActive = GetBindModeActive?.Invoke() ?? false;
        _frameState.IsSelectedForBind = GetIsSelectedForBind?.Invoke(Id) ?? false;
        _frameState.AlwaysOnTop = AlwaysOnTop;
        _frameState.HasNativeControls = HasNativeControls;

        _frameState.IsInTabGroup = LayoutMode == WindowLayoutMode.TabGroup;
        _frameState.TabGroupId = GroupId;
        if (_frameState.IsInTabGroup && GetTabGroupInfo != null)
        {
            var tabInfo = GetTabGroupInfo(GroupId!);
            if (tabInfo.HasValue)
            {
                _frameState.TabIds = tabInfo.Value.ids;
                _frameState.TabTitles = tabInfo.Value.titles;
                _frameState.ActiveTabId = tabInfo.Value.activeId;
            }
        }
        else
        {
            _frameState.TabIds = null;
            _frameState.TabTitles = null;
            _frameState.ActiveTabId = null;
        }
    }

    // --- Coordinate Transform ---

    private (float x, float y) TransformMouse(double rawX, double rawY)
    {
        var px = (float)(rawX * _scale);
        var py = (float)(_height - rawY * _scale);
        return (px, py);
    }

    private void RefreshMousePosition()
    {
        if (_nativeWindow == null) return;
        var (posX, posY) = _nativeWindow.MouseLocationInWindow;
        var (px, py) = TransformMouse(posX, posY);
        MouseX = px;
        MouseY = py;
        _frameState.MouseX = MouseX;
        _frameState.MouseY = MouseY;
    }

    // --- Event Handling (main thread) ---

    public void OnMouseDown(double rawX, double rawY)
    {
        var (px, py) = TransformMouse(rawX, rawY);
        MouseX = px;
        MouseY = py;
        _draggingTabId = null;
        _pendingTabSelectId = null;
        MouseDown = true;
        _frameState.MouseClicked = true;
        RequestRedraw();

        HitTestResult? result;
        lock (_pluginLock) { result = _plugin.HitTest(px, py, _width, _height); }

        if (result?.Action != null)
        {
            if (result.Action.StartsWith("tab_select:"))
            {
                var tabId = result.Action[11..];
                _draggingTabId = tabId;
                _pendingTabSelectId = tabId;
                _tabDragStartX = px;
                _tabDragStartY = py;
                return;
            }
            if (ActionRouter.IsGlobalAction(result.Action))
                _actionRouter.Execute(result.Action, Id);
        }
    }

    public void OnMouseUp(double rawX, double rawY)
    {
        var (px, py) = TransformMouse(rawX, rawY);
        MouseX = px;
        MouseY = py;
        MouseDown = false;

        if (_pendingTabSelectId != null)
        {
            _actionRouter.Execute($"tab_select:{_pendingTabSelectId}", Id);
            _pendingTabSelectId = null;
        }

        _draggingTabId = null;
        RequestRedraw();
    }

    public void OnRightClick(double rawX, double rawY)
    {
        var (px, py) = TransformMouse(rawX, rawY);
        MouseX = px;
        MouseY = py;
        _frameState.RightClick = true;
        RequestRedraw();
    }

    public void OnMouseMove(double rawX, double rawY)
    {
        var (px, py) = TransformMouse(rawX, rawY);
        MouseX = px;
        MouseY = py;
        RequestRedraw();

        // Tab drag-out detection
        if (_draggingTabId != null)
        {
            const float dragThreshold = 14f;
            var dx = px - _tabDragStartX;
            var dy = py - _tabDragStartY;
            if (dx * dx + dy * dy >= dragThreshold * dragThreshold)
            {
                var tabId = _draggingTabId;
                var offsetX = _tabDragStartX / _scale;
                _draggingTabId = null;
                _pendingTabSelectId = null;
                OnTabDraggedOut?.Invoke(Id, tabId, offsetX);
                return;
            }
        }

        HitTestResult? result;
        lock (_pluginLock) { result = _plugin.HitTest(px, py, _width, _height); }
        var newCursor = result?.Cursor ?? CursorType.Default;
        if (newCursor != _currentCursor)
        {
            _currentCursor = newCursor;
            _platform.SetCursor(newCursor);
        }
    }

    public void OnScroll(double deltaX, double deltaY)
    {
        _frameState.MouseScroll = (float)deltaY;
        RequestRedraw();
        var plugin = _plugin; // snapshot — volatile read prevents ARM64 torn read
        plugin.OnScroll?.Invoke((float)deltaX, (float)deltaY, MouseX, MouseY, _width, _height);
    }

    public void OnKeyDown(ushort keyCode, KeyModifiers modifiers)
    {
        RequestRedraw();
        var plugin = _plugin;
        plugin.OnKeyDown?.Invoke(keyCode, modifiers);
    }

    public void OnKeyUp(ushort keyCode, KeyModifiers modifiers)
    {
        RequestRedraw();
        var plugin = _plugin;
        plugin.OnKeyUp?.Invoke(keyCode, modifiers);
    }

    // --- WebView Management (delegated to WindowWebHost) ---

    private void ProcessWebViewRequests()
    {
        var requests = _frameState.WebViewRequests;
        _frameState.WebViewRequests = null;
        if (requests != null && _nativeWindow != null)
            _webHost?.ProcessRequests(requests, _nativeWindow);
    }

    public void LoadWebComponent(string component, int port)
        => _webHost!.LoadWebComponent(component, port, _nativeWindow!);

    public void LoadExternalUrl(string url, int port)
        => _webHost!.LoadExternalUrl(url, port, _nativeWindow!);

    public void HotSwapSlot(string key) => _webHost?.HotSwapSlot(key);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _dataSubscription?.Dispose();
        _gpuHost?.Dispose();
        _webHost?.Dispose();
        _sceneRenderer?.Dispose();
        _nativeWindow?.Dispose();
        (_plugin as IDisposable)?.Dispose();
        _invokeRouter.Dispose();
        OnShowOverlay = null;
        OnCloseOverlay = null;
        OnTabDraggedOut = null;
    }

    // --- Window Event Push ---

    private void PushWindowEvent(string type, object? data = null)
    {
        var payload = data != null
            ? new { type, data }
            : (object)new { type };
        BunManager.Instance.Push($"window:{Id}:event", payload);
    }

    // --- Window Delegate (nested, implements INativeWindowDelegate) ---

    private sealed class NativeWindowDelegate : INativeWindowDelegate
    {
        private readonly ManagedWindow _w;
        internal NativeWindowDelegate(ManagedWindow w) => _w = w;
        public void OnResizeStarted() => _w._isLiveResizing = true;
        public void OnResized(double w, double h)
        {
            _w.UpdateSize(); _w.RequestRedraw();
            _w.PushWindowEvent("resized", new { width = w, height = h });
        }
        public void OnResizeEnded()
        {
            _w._isLiveResizing = false;
            _w._purgeAfterResize = true;
            _w.RefreshMousePosition();
            _w.RequestRedraw();
        }
        public void OnClosed()
        {
            ApplicationRuntime.Instance?.RunOnMainThread(() =>
                ApplicationRuntime.Instance?.WindowManager.UnregisterWindow(_w.Id));
        }
        public void OnMoved(double x, double y) => _w.PushWindowEvent("moved", new { x, y });
        public void OnFocused()     { _w.IsFocused = true;     _w.PushWindowEvent("focus"); }
        public void OnBlurred()     { _w.IsFocused = false;    _w.PushWindowEvent("blur"); }
        public void OnMiniaturized()   { _w.IsMinimized = true;  _w.PushWindowEvent("minimize"); }
        public void OnDeminiaturized() { _w.IsMinimized = false; _w.PushWindowEvent("restore"); }
        public void OnEnteredFullscreen() { _w.IsFullscreen = true;  _w.PushWindowEvent("enter-full-screen"); _w.RequestRedraw(); }
        public void OnExitedFullscreen()  { _w.IsFullscreen = false; _w.PushWindowEvent("leave-full-screen"); _w.RequestRedraw(); }
    }
}
