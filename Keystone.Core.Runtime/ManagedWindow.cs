using CoreAnimation;
using CoreGraphics;
using Keystone.Core;
using Keystone.Core.Graphics.Skia;
using Keystone.Core.Management;
using Keystone.Core.Management.Bun;
using Keystone.Core.Platform;
using Keystone.Core.Platform.MacOS;
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

    private INativeWindow? _nativeWindow;
    private CAMetalLayer? _metalLayer; // GPU path — macOS/Metal specific
    private SkiaPaintCache? _paintCache;

    // Per-window render thread + GPU context
    private WindowRenderThread? _renderThread;
    private ManualResetEventSlim? _vsyncSignal; // from DisplayLink, for cleanup
    private SceneRenderer? _sceneRenderer;

    private float _scale = 1.0f;
    private uint _width;
    private uint _height;
    private uint _lastDrawW, _lastDrawH; // last DrawableSize set on render thread
    private volatile bool _isLiveResizing;
    private volatile bool _purgeAfterResize;
    private bool _disposed;

    // Shared host WebView — single IWebView per window for all Bun component slots
    private IWebView? _hostWebView;
    private bool _hostCreating; // guard against double-creation from render thread
    private bool _hostReady;
    private Dictionary<string, (float x, float y, float w, float h)>? _hostSlots;
    private List<(string key, string scriptUrl, float x, float y, float w, float h)>? _pendingSlots;

    // Invoke handler registry — channel → async handler returning object? result
    // Registered by ApplicationRuntime for built-in APIs, and by apps for custom channels.
    private readonly Dictionary<string, Func<System.Text.Json.JsonElement, Task<object?>>> _invokeHandlers = new();

    /// <summary>
    /// Register a native handler for invoke() calls from web components.
    /// Called when JS does: keystone().invoke(channel, args)
    /// Handler receives the args JsonElement and returns a JSON-serializable result.
    /// </summary>
    public void RegisterInvokeHandler(string channel, Func<System.Text.Json.JsonElement, Task<object?>> handler)
        => _invokeHandlers[channel] = handler;

    /// <summary>
    /// Called for non-invoke direct messages from JS (postMessage without ks_invoke).
    /// Direct path — no Bun round-trip. msg is a JSON string.
    /// </summary>
    public Action<string>? OnDirectMessage { get; set; }

    /// <summary>
    /// Entry point for all WKScriptMessageHandler messages from JS.
    /// If the message has ks_invoke:true, dispatches to the registered handler and replies via Bun push.
    /// Otherwise falls through to OnDirectMessage.
    /// </summary>
    private void DispatchDirectMessage(string msg)
    {
        System.Text.Json.JsonDocument? doc = null;
        try
        {
            doc = System.Text.Json.JsonDocument.Parse(msg);
            var root = doc.RootElement;

            if (root.TryGetProperty("ks_invoke", out var ksInvoke) && ksInvoke.GetBoolean())
            {
                var id = root.GetProperty("id").GetInt32();
                var channel = root.GetProperty("channel").GetString() ?? "";
                var windowId = root.TryGetProperty("windowId", out var wid) ? wid.GetString() ?? Id : Id;
                var args = root.TryGetProperty("args", out var a) ? a : default;

                var replyChannel = $"window:{windowId}:__reply__:{id}";

                if (_invokeHandlers.TryGetValue(channel, out var handler))
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await handler(args);
                            BunManager.Instance.Push(replyChannel, new { result });
                        }
                        catch (Exception ex)
                        {
                            BunManager.Instance.Push(replyChannel, new { error = ex.Message });
                        }
                    });
                }
                else
                {
                    BunManager.Instance.Push(replyChannel, new { error = $"No handler registered for channel: {channel}" });
                }
                return;
            }
        }
        catch { }
        finally { doc?.Dispose(); }

        OnDirectMessage?.Invoke(msg);
    }

    /// <summary>
    /// Fired when the WKWebView content process terminates unexpectedly.
    /// Subscribe to show a UI indicator or log the event.
    /// If null (default), the window still auto-reloads per ProcessRecoveryConfig.
    /// </summary>
    public Action? OnWebViewCrash { get; set; }

    // Dedicated WebViews for external URLs (one per unique URL key)
    private Dictionary<string, IWebView>? _externalWebViews;
    private Dictionary<string, (float x, float y, float w, float h)>? _externalRects;

    // Expose for event coordinate transform
    public float ScaleFactor => _scale;
    public uint Width => _width;
    public uint Height => _height;
    private CursorType _currentCursor = CursorType.Default;
    private volatile bool _needsRedraw = true;
    private volatile bool _vsyncActive = true;
    private Action? _dataSubscription; // DataChannel callback — wakes render thread on data arrival

    // Identity
    public string Id { get; }
    public string WindowType { get; }
    public IntPtr Handle => _nativeWindow?.Handle ?? IntPtr.Zero;
    public INativeWindow? NativeWindow => _nativeWindow;

    public IWindowPlugin GetPlugin() { lock (_pluginLock) return _plugin; }
    public object? GetGpuContext() => _renderThread?.Gpu;

    /// <summary>Expected IOSurface bytes for this window's CAMetalLayer drawable pool.
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
    public Func<string, (string[] ids, string[] titles, string activeId)?> GetTabGroupInfo { get; set; }

    public ManagedWindow(string id, IWindowPlugin plugin, ActionRouter actionRouter, IPlatform platform)
    {
        Id = id;
        _plugin = plugin;
        _platform = platform;
        WindowType = plugin.WindowType;
        _actionRouter = actionRouter;
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

        // GPU path — macOS/Metal specific. Port for other platforms.
        if (nativeWindow.GetGpuSurface() is CAMetalLayer metalLayer)
        {
            _metalLayer = metalLayer;
            MacOSPlatform.ConfigureMetalLayer(metalLayer, SkiaWindow.Shared.Device, _scale);

            _paintCache = new SkiaPaintCache();

            // Create per-window GPU context + subscribe to VSync + create render thread
            var gpu = SkiaWindow.CreateWindowContext();
            var displayLink = ApplicationRuntime.Instance!.DisplayLink;
            _vsyncSignal = displayLink.Subscribe();
            _renderThread = new WindowRenderThread(this, gpu, _vsyncSignal);
        }

        // Set up window delegate for live resize
        _nativeWindow.SetDelegate(new NativeWindowDelegate(this));

        // Subscribe to data channels — RequestRedraw wakes the render thread when data arrives
        var deps = _plugin.Dependencies;
        if (deps != null && deps.Any())
        {
            _dataSubscription = RequestRedraw;
            DataChannel.Subscribe(deps, _dataSubscription);
        }

        // Start render thread
        _renderThread?.Start();

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
        _vsyncSignal?.Set();
    }

    /// <summary>Called by render thread: resubscribe to VSync if suspended.</summary>
    internal void ResumeVSync()
    {
        if (_vsyncActive) return;
        ApplicationRuntime.Instance?.DisplayLink.Resubscribe(_vsyncSignal!);
        _vsyncActive = true;
    }

    /// <summary>Called by render thread: unsubscribe from VSync when idle.
    /// Data subscriptions call RequestRedraw to wake the thread when new data arrives.</summary>
    internal void TrySuspendVSync()
    {
        if (!_vsyncActive) return;
        ApplicationRuntime.Instance?.DisplayLink.Unsubscribe(_vsyncSignal!);
        _vsyncActive = false;
    }

    public bool ShouldRender() => _needsRedraw;

    /// <summary>
    /// Render on the per-window thread using its own GpuContext.
    /// Called by WindowRenderThread.
    /// </summary>
    public void RenderOnThread(WindowGpuContext gpu)
    {
        if (_metalLayer == null || _paintCache == null) return;

        UpdateSize();
        if (_width == 0 || _height == 0) return;

        // During live resize, freeze DrawableSize — render at the pre-resize resolution
        // and let CoreAnimation scale. This prevents CAMetalLayer from allocating new
        // IOSurface-backed drawables at every intermediate size (~15MB each at 2x retina).
        // DrawableSize only updates when resize ENDS (single allocation).
        if (!_isLiveResizing && (_width != _lastDrawW || _height != _lastDrawH))
        {
            _metalLayer.DrawableSize = new CGSize(_width, _height);
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

        var canvas = gpu.BeginFrame(_metalLayer, (int)drawW, (int)drawH);
        if (canvas == null) return;

        try
        {
            canvas.Clear(SkiaSharp.SKColors.Black);

            lock (_pluginLock)
            {
                if (!_pendingReload)
                {
                    _frameState.WindowTitle = _plugin.WindowTitle;
                    _frameState.NeedsRedraw = false;

                    var scene = _plugin.BuildScene(_frameState);
                    if (scene != null)
                    {
                        _sceneRenderer ??= new SceneRenderer();
                        _sceneRenderer.Render(canvas, _paintCache, _frameState, scene);
                        _needsRedraw = _frameState.NeedsRedraw;
                    }
                    else
                    {
                        using var ctx = new RenderContext(canvas, _paintCache, _frameState);
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
        _frameState.MouseX = MouseX;
        _frameState.MouseY = MouseY;
        _frameState.MouseDown = MouseDown;
        _frameState.BindModeActive = GetBindModeActive?.Invoke() ?? false;
        _frameState.IsSelectedForBind = GetIsSelectedForBind?.Invoke(Id) ?? false;
        _frameState.AlwaysOnTop = AlwaysOnTop;

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

    // --- WebView Management (single shared host + dedicated externals) ---

    private void ProcessWebViewRequests()
    {
        var requests = _frameState.WebViewRequests;
        _frameState.WebViewRequests = null;

        if (_nativeWindow == null) return;

        // Separate into slots (Bun components → shared host) and externals (dedicated WebView)
        var currentSlotKeys = new HashSet<string>();

        if (requests != null)
        {
            foreach (var (key, url, rx, ry, rw, rh, isSlot) in requests)
            {
                if (isSlot)
                {
                    currentSlotKeys.Add(key);
                    ProcessSlotRequest(key, url, rx, ry, rw, rh);
                }
                else
                    ProcessExternalRequest(key, url, rx, ry, rw, rh);
            }
        }

        // Remove slots that disappeared this frame
        if (_hostSlots != null)
        {
            var removed = new List<string>();
            foreach (var key in _hostSlots.Keys)
            {
                if (!currentSlotKeys.Contains(key))
                    removed.Add(key);
            }
            foreach (var key in removed)
            {
                _hostSlots.Remove(key);
                if (_hostReady && _hostWebView != null)
                {
                    var k = key.Replace("'", "\\'");
                    var js = $"window.__removeSlot('{k}')";
                    _hostWebView.EvaluateJavaScript(js);
                }
            }
        }
    }

    private void ProcessSlotRequest(string key, string url, float rx, float ry, float rw, float rh)
    {
        _hostSlots ??= new();

        // Create shared host WebView if needed
        if (_hostWebView == null)
        {
            var port = BunManager.Instance.BunPort;
            if (port <= 0) return;

            // Queue this slot for when host is ready
            _pendingSlots ??= new();
            if (!_hostSlots.ContainsKey(key))
            {
                _hostSlots[key] = (rx, ry, rw, rh);
                _pendingSlots.Add((key, $"/web/{key}.js", rx, ry, rw, rh));
            }

            // Guard: only create once (render thread may call before main thread completes)
            if (_hostCreating) return;
            _hostCreating = true;

            _nativeWindow!.CreateWebView(webView =>
            {
                try
                {
                    webView.InjectScriptOnLoad($"window.__KEYSTONE_PORT__ = {port};");
                    webView.AddMessageHandler("keystone", msg => DispatchDirectMessage(msg));
                    webView.SetTransparentBackground();
                    webView.OnCrash = () => OnHostWebViewCrash();
                    webView.LoadUrl($"http://127.0.0.1:{port}/__host__");

                    _hostWebView = webView;
                    PollHostReady();

                    Console.WriteLine($"[ManagedWindow] Shared host WebView created for window {Id}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[ManagedWindow] Host WebView failed: {ex.Message}");
                }
            });
            return;
        }

        if (_hostSlots.TryGetValue(key, out var last))
        {
            // Already exists — reposition if changed
            if (Math.Abs(last.x - rx) > 0.5f || Math.Abs(last.y - ry) > 0.5f ||
                Math.Abs(last.w - rw) > 0.5f || Math.Abs(last.h - rh) > 0.5f)
            {
                _hostSlots[key] = (rx, ry, rw, rh);
                if (_hostReady)
                {
                    var k = key.Replace("'", "\\'");
                    var js = $"window.__moveSlot('{k}',{rx:F0},{ry:F0},{rw:F0},{rh:F0})";
                    _hostWebView!.EvaluateJavaScript(js);
                }
            }
        }
        else
        {
            // New slot
            _hostSlots[key] = (rx, ry, rw, rh);
            if (_hostReady)
            {
                AddSlotToHost(key, rx, ry, rw, rh);
            }
            else
            {
                _pendingSlots ??= new();
                _pendingSlots.Add((key, $"/web/{key}.js", rx, ry, rw, rh));
            }
        }
    }

    private void AddSlotToHost(string key, float x, float y, float w, float h)
    {
        if (_hostWebView == null) return;
        var k = key.Replace("'", "\\'");
        var wid = Id.Replace("'", "\\'");
        var scriptUrl = $"/web/{k}.js";
        var js = $"window.__addSlot('{k}','{scriptUrl}',{x:F0},{y:F0},{w:F0},{h:F0},'{wid}')";
        _hostWebView.EvaluateJavaScript(js);
    }

    /// <summary>
    /// Called by IWebView.OnCrash when the WebKit content process terminates.
    /// Fires OnWebViewCrash (app-layer hook), then reloads after the configured delay.
    /// </summary>
    private void OnHostWebViewCrash()
    {
        OnWebViewCrash?.Invoke();
        ApplicationRuntime.Instance?.RaiseWebViewCrash(Id);

        var cfg = ApplicationRuntime.Instance?.Config.ProcessRecovery;
        if (cfg?.WebViewAutoReload != false)
        {
            var delayMs = cfg?.WebViewReloadDelayMs ?? 200;
            _hostReady = false;
            _hostSlots?.Clear();
            _pendingSlots = null;

            Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                ApplicationRuntime.Instance?.RunOnMainThread(() =>
                {
                    if (_disposed || _hostWebView == null) return;
                    var port = BunManager.Instance.BunPort;
                    if (port <= 0) return;
                    _hostWebView.LoadUrl($"http://127.0.0.1:{port}/__host__");
                    PollHostReady();
                    Console.WriteLine($"[ManagedWindow] Reloading WebView for window {Id} after content process crash");
                });
            });
        }
    }

    /// <summary>Hot-swap a named slot's component in place without reloading the host page.</summary>
    public void HotSwapSlot(string key)
    {
        if (_hostWebView == null || !_hostReady) return;
        var k = key.Replace("'", "\\'");
        var js = $"window.__hotSwapSlot('{k}','/web/{k}.js')";
        _hostWebView.EvaluateJavaScript(js);
    }

    private void PollHostReady()
    {
        if (_disposed || _hostReady || _hostWebView == null) return;
        _hostWebView.EvaluateJavaScriptBool("window.__ready === true", ready =>
        {
            if (ready)
            {
                _hostReady = true;
                FlushPendingSlots();
            }
            else
            {
                Task.Run(async () =>
                {
                    await Task.Delay(50);
                    ApplicationRuntime.Instance?.RunOnMainThread(PollHostReady);
                });
            }
        });
    }

    private void FlushPendingSlots()
    {
        if (_pendingSlots == null) return;
        foreach (var (key, _, x, y, w, h) in _pendingSlots)
            AddSlotToHost(key, x, y, w, h);
        _pendingSlots = null;
        Console.WriteLine($"[ManagedWindow] Host ready, flushed {_hostSlots?.Count ?? 0} slots for window {Id}");
    }

    private void ProcessExternalRequest(string key, string url, float rx, float ry, float rw, float rh)
    {
        _externalWebViews ??= new();
        _externalRects ??= new();

        if (_externalRects.TryGetValue(key, out var last))
        {
            if (Math.Abs(last.x - rx) > 0.5f || Math.Abs(last.y - ry) > 0.5f ||
                Math.Abs(last.w - rw) > 0.5f || Math.Abs(last.h - rh) > 0.5f)
            {
                _externalRects[key] = (rx, ry, rw, rh);
                if (_externalWebViews.TryGetValue(key, out var wv))
                    wv.SetFrame(rx, ry, rw, rh);
            }
        }
        else
        {
            _externalRects[key] = (rx, ry, rw, rh);
            var capturedUrl = url;
            var capturedKey = key;
            var port = BunManager.Instance.BunPort;

            _nativeWindow!.CreateWebView(webView =>
            {
                try
                {
                    if (port > 0)
                        webView.InjectScriptOnLoad($"window.__KEYSTONE_PORT__ = {port};");
                    webView.SetFrame(rx, ry, rw, rh);
                    webView.LoadUrl(capturedUrl);
                    _externalWebViews![capturedKey] = webView;
                    Console.WriteLine($"[ManagedWindow] External WebView created: {capturedKey} in window {Id}");
                }
                catch (Exception ex)
                {
                    _externalRects?.Remove(capturedKey);
                    Console.WriteLine($"[ManagedWindow] External WebView failed: {ex.Message}");
                }
            });
        }
    }

    private void DisposeWebViews()
    {
        // Dispose shared host WebView
        if (_hostWebView != null)
        {
            _hostWebView.RemoveMessageHandler("keystone");
            _hostWebView.Dispose();
            _hostWebView = null;
            _hostReady = false;
        }
        _hostSlots?.Clear();
        _pendingSlots = null;

        // Dispose external WebViews
        if (_externalWebViews != null)
        {
            foreach (var (_, wv) in _externalWebViews)
                wv.Dispose();
            _externalWebViews.Clear();
        }
        _externalRects?.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_dataSubscription != null)
            DataChannel.Unsubscribe(_dataSubscription);
        _renderThread?.Dispose();
        DisposeWebViews();
        _sceneRenderer?.Dispose();
        if (_vsyncSignal != null)
        {
            ApplicationRuntime.Instance?.DisplayLink.Unsubscribe(_vsyncSignal);
            _vsyncSignal.Dispose();
        }
        _nativeWindow?.Dispose();
        (_plugin as IDisposable)?.Dispose();
        _paintCache?.Dispose();
    }

    // --- Window Delegate (nested, implements INativeWindowDelegate) ---

    private sealed class NativeWindowDelegate : INativeWindowDelegate
    {
        private readonly ManagedWindow _w;
        internal NativeWindowDelegate(ManagedWindow w) => _w = w;
        public void OnResizeStarted() => _w._isLiveResizing = true;
        public void OnResized(double w, double h) { _w.UpdateSize(); _w.RequestRedraw(); }
        public void OnResizeEnded()
        {
            _w._isLiveResizing = false;
            _w._purgeAfterResize = true;
            _w.RefreshMousePosition();
            _w.RequestRedraw();
        }
        public void OnClosed() { }
        public void OnMoved(double x, double y) { }
    }
}
