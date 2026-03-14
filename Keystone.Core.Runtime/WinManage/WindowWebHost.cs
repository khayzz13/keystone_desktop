/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

#if MACOS
using Keystone.Core.Platform.MacOS;
#endif
using Keystone.Core.Platform;

namespace Keystone.Core.Runtime;

/// <summary>
/// Manages WebView lifecycle: shared host WebView for Bun component slots,
/// dedicated WebViews for external URLs, crash recovery with exponential backoff.
/// All WebView operations run on the main thread (WebKit requirement).
/// </summary>
internal sealed class WindowWebHost : IDisposable
{
    readonly string _windowId;
    readonly Func<int> _getBunPort;
    readonly Func<string?> _getSessionToken;
    readonly Func<int, string> _getBaseUrl;
    readonly Action<Action> _runOnMainThread;
    readonly Action<string> _raiseWebViewCrash;
    readonly ProcessRecoveryConfig? _processRecovery;
    readonly Action<string> _dispatchMessage;
    readonly bool _enableInspector;

    // Host WebView state
    IWebView? _hostWebView;
    bool _hostCreating;
    bool _hostReady;
    Dictionary<string, (float x, float y, float w, float h)>? _hostSlots;
    List<(string key, string scriptUrl, float x, float y, float w, float h)>? _pendingSlots;

    // External WebViews
    Dictionary<string, IWebView>? _externalWebViews;
    Dictionary<string, (float x, float y, float w, float h)>? _externalRects;

    // Crash recovery
    int _crashCount;
    DateTime _lastCrash;
    bool _disposed;

    public Action? OnCrash { get; set; }

    public WindowWebHost(
        string windowId,
        Func<int> getBunPort,
        Func<string?> getSessionToken,
        Func<int, string> getBaseUrl,
        Action<Action> runOnMainThread,
        Action<string> raiseWebViewCrash,
        ProcessRecoveryConfig? processRecovery,
        Action<string> dispatchMessage,
        bool enableInspector)
    {
        _windowId = windowId;
        _getBunPort = getBunPort;
        _getSessionToken = getSessionToken;
        _getBaseUrl = getBaseUrl;
        _runOnMainThread = runOnMainThread;
        _raiseWebViewCrash = raiseWebViewCrash;
        _processRecovery = processRecovery;
        _dispatchMessage = dispatchMessage;
        _enableInspector = enableInspector;
    }

    string BridgeBootScript(int port)
    {
        var token = _getSessionToken() ?? "";
        return $"window.__KEYSTONE_PORT__ = {port}; window.__KEYSTONE_SESSION_TOKEN__ = '{token}';";
    }

    // --- Host WebView reference (needed by ManagedWindow for direct JS eval) ---

    public IWebView? HostWebView => _hostWebView;

    // --- Process slot/external requests from FrameState (called from render thread end-of-frame) ---

    public void ProcessRequests(
        List<(string key, string url, float x, float y, float w, float h, bool isSlot)> requests,
        INativeWindow nativeWindow)
    {
        var currentSlotKeys = new HashSet<string>();

        foreach (var (key, url, rx, ry, rw, rh, isSlot) in requests)
        {
            if (isSlot)
            {
                currentSlotKeys.Add(key);
                ProcessSlotRequest(key, url, rx, ry, rw, rh, nativeWindow);
            }
            else
                ProcessExternalRequest(key, url, rx, ry, rw, rh, nativeWindow);
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
                    _hostWebView.EvaluateJavaScript($"window.__removeSlot('{k}')");
                }
            }
        }
    }

    void ProcessSlotRequest(string key, string url, float rx, float ry, float rw, float rh, INativeWindow nativeWindow)
    {
        _hostSlots ??= new();

        if (_hostWebView == null)
        {
            var port = _getBunPort();
            if (port <= 0) return;

            _pendingSlots ??= new();
            if (!_hostSlots.ContainsKey(key))
            {
                _hostSlots[key] = (rx, ry, rw, rh);
                _pendingSlots.Add((key, $"/web/{key}.js", rx, ry, rw, rh));
            }

            if (_hostCreating) return;
            _hostCreating = true;

            nativeWindow.CreateWebView(webView =>
            {
                try
                {
                    webView.InjectScriptOnLoad(BridgeBootScript(port));
                    webView.AddMessageHandler("keystone", _dispatchMessage);
                    webView.SetTransparentBackground();
                    if (_enableInspector) webView.SetInspectable(true);
                    webView.OnCrash = () => OnHostCrash();
                    webView.LoadUrl($"{_getBaseUrl(port)}/__host__");

                    _hostWebView = webView;
                    PollHostReady();

                    Console.WriteLine($"[WindowWebHost] Shared host WebView created for window {_windowId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WindowWebHost] Host WebView failed: {ex.Message}");
                }
            });
            return;
        }

        if (_hostSlots.TryGetValue(key, out var last))
        {
            if (Math.Abs(last.x - rx) > 0.5f || Math.Abs(last.y - ry) > 0.5f ||
                Math.Abs(last.w - rw) > 0.5f || Math.Abs(last.h - rh) > 0.5f)
            {
                _hostSlots[key] = (rx, ry, rw, rh);
                if (_hostReady)
                {
                    var k = key.Replace("'", "\\'");
                    _hostWebView!.EvaluateJavaScript($"window.__moveSlot('{k}',{rx:F0},{ry:F0},{rw:F0},{rh:F0})");
                }
            }
        }
        else
        {
            _hostSlots[key] = (rx, ry, rw, rh);
            if (_hostReady)
                AddSlotToHost(key, rx, ry, rw, rh);
            else
            {
                _pendingSlots ??= new();
                _pendingSlots.Add((key, $"/web/{key}.js", rx, ry, rw, rh));
            }
        }
    }

    void AddSlotToHost(string key, float x, float y, float w, float h)
    {
        if (_hostWebView == null) return;
        var k = key.Replace("'", "\\'");
        var wid = _windowId.Replace("'", "\\'");
        var scriptUrl = $"/web/{k}.js";
        _hostWebView.EvaluateJavaScript($"window.__addSlot('{k}','{scriptUrl}',{x:F0},{y:F0},{w:F0},{h:F0},'{wid}')");
    }

    void ProcessExternalRequest(string key, string url, float rx, float ry, float rw, float rh, INativeWindow nativeWindow)
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
            var port = _getBunPort();

            nativeWindow.CreateWebView(webView =>
            {
                try
                {
                    if (port > 0)
                        webView.InjectScriptOnLoad(BridgeBootScript(port));
                    webView.SetFrame(rx, ry, rw, rh);
                    webView.LoadUrl(capturedUrl);
                    _externalWebViews![capturedKey] = webView;
                    Console.WriteLine($"[WindowWebHost] External WebView created: {capturedKey} in window {_windowId}");
                }
                catch (Exception ex)
                {
                    _externalRects?.Remove(capturedKey);
                    Console.WriteLine($"[WindowWebHost] External WebView failed: {ex.Message}");
                }
            });
        }
    }

    // --- Full-window WebView paths ---

    public void LoadWebComponent(string component, int port, INativeWindow nativeWindow)
    {
        nativeWindow.CreateWebView(wv =>
        {
            try
            {
                wv.InjectScriptOnLoad(BridgeBootScript(port));
                wv.AddMessageHandler("keystone", _dispatchMessage);
                wv.SetTransparentBackground();
                if (_enableInspector) wv.SetInspectable(true);
                wv.OnCrash = () => LoadWebComponent(component, port, nativeWindow);
                wv.LoadUrl($"{_getBaseUrl(port)}/{component}?windowId={_windowId}");
                _hostWebView = wv;
                Console.WriteLine($"[WindowWebHost] Web-only WebView loaded: {component} id={_windowId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WindowWebHost] Web-only WebView failed: {ex.Message}");
            }
        });
    }

    public void LoadExternalUrl(string url, int port, INativeWindow nativeWindow)
    {
        nativeWindow.CreateWebView(wv =>
        {
            try
            {
                wv.InjectScriptOnLoad(BridgeBootScript(port));
                wv.AddMessageHandler("keystone", _dispatchMessage);
                wv.SetTransparentBackground();
                if (_enableInspector) wv.SetInspectable(true);
                wv.OnCrash = () => LoadExternalUrl(url, port, nativeWindow);
                wv.LoadUrl(url);
                _hostWebView = wv;
                Console.WriteLine($"[WindowWebHost] External URL WebView loaded: {url} id={_windowId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WindowWebHost] External URL WebView failed: {ex.Message}");
            }
        });
    }

    // --- Crash recovery ---

    void OnHostCrash()
    {
        OnCrash?.Invoke();
        _raiseWebViewCrash(_windowId);

        if (_processRecovery?.WebViewAutoReload != false)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastCrash).TotalSeconds > 60)
                _crashCount = 0;
            _lastCrash = now;
            _crashCount++;

            if (_crashCount > 5)
            {
                Console.Error.WriteLine($"[WindowWebHost] Window {_windowId}: WebView crashed {_crashCount} times in rapid succession — stopping auto-reload");
                return;
            }

            var baseDelay = _processRecovery?.WebViewReloadDelayMs ?? 200;
            var delayMs = Math.Min(baseDelay * (1 << (_crashCount - 1)), 30_000);
            _hostReady = false;
            _hostSlots?.Clear();
            _pendingSlots = null;

            Console.WriteLine($"[WindowWebHost] Window {_windowId}: WebView crash #{_crashCount} — reloading in {delayMs}ms");

            Task.Run(async () =>
            {
                await Task.Delay(delayMs);
                _runOnMainThread(() =>
                {
                    if (_disposed || _hostWebView == null) return;
                    var port = _getBunPort();
                    if (port <= 0) return;
                    _hostWebView.LoadUrl($"{_getBaseUrl(port)}/__host__");
                    PollHostReady();
                });
            });
        }
    }

    // --- Host readiness polling ---

    void PollHostReady()
    {
        if (_disposed || _hostReady || _hostWebView == null) return;
        _hostWebView.EvaluateJavaScriptBool("window.__ready === true", ready =>
        {
            if (ready)
            {
                _hostReady = true;
                _crashCount = 0;
                FlushPendingSlots();
            }
            else
            {
                Task.Run(async () =>
                {
                    await Task.Delay(50);
                    _runOnMainThread(PollHostReady);
                });
            }
        });
    }

    void FlushPendingSlots()
    {
        if (_pendingSlots == null) return;
        foreach (var (key, _, x, y, w, h) in _pendingSlots)
            AddSlotToHost(key, x, y, w, h);
        _pendingSlots = null;
        Console.WriteLine($"[WindowWebHost] Host ready, flushed {_hostSlots?.Count ?? 0} slots for window {_windowId}");
    }

    // --- Slot hot-swap ---

    public void HotSwapSlot(string key)
    {
        if (_hostWebView == null || !_hostReady) return;
        var k = key.Replace("'", "\\'");
        _hostWebView.EvaluateJavaScript($"window.__hotSwapSlot('{k}','/web/{k}.js')");
    }

    // --- JS evaluation pass-throughs ---

    public void EvaluateJavaScript(string js) => _hostWebView?.EvaluateJavaScript(js);

    public void EvaluateJavaScriptWithResult(string js, Action<string?> completion)
    {
        if (_hostWebView == null) { completion(null); return; }
        _hostWebView.EvaluateJavaScript(js, completion);
    }

    public void SetInspectable(bool enabled) => _hostWebView?.SetInspectable(enabled);

    public Task<string?> GetServiceWorkerStatus()
    {
        var tcs = new TaskCompletionSource<string?>();
        if (_hostWebView == null) { tcs.SetResult(null); return tcs.Task; }
        _hostWebView.EvaluateJavaScript("""
            (async () => {
                const reg = await navigator.serviceWorker.getRegistration();
                return JSON.stringify({
                    active: !!reg?.active,
                    waiting: !!reg?.waiting,
                    installing: !!reg?.installing,
                    scope: reg?.scope ?? null,
                    scriptURL: reg?.active?.scriptURL ?? null,
                });
            })()
        """, result => tcs.SetResult(result));
        return tcs.Task;
    }

    public void UnregisterServiceWorkers()
    {
        _hostWebView?.EvaluateJavaScript(
            "navigator.serviceWorker.getRegistrations().then(regs => regs.forEach(r => r.unregister()))");
    }

    public void ClearServiceWorkerCaches()
    {
        _hostWebView?.EvaluateJavaScript(
            "caches.keys().then(ks => ks.forEach(k => caches.delete(k)))");
    }

    public void SetNavigationPolicy(Func<string, bool>? policy)
    {
#if MACOS
        if (_hostWebView is MacOSWebView macWv)
            macWv.SetNavigationPolicy(policy);
#endif
    }

    // --- Disposal ---

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_hostWebView != null)
        {
            _hostWebView.RemoveMessageHandler("keystone");
            _hostWebView.Dispose();
            _hostWebView = null;
            _hostReady = false;
        }
        _hostSlots?.Clear();
        _pendingSlots = null;

        if (_externalWebViews != null)
        {
            foreach (var (_, wv) in _externalWebViews)
                wv.Dispose();
            _externalWebViews.Clear();
        }
        _externalRects?.Clear();
        OnCrash = null;
    }
}
