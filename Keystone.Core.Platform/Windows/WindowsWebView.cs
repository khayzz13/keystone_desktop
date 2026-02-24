// WindowsWebView — WebView2 implementation of IWebView.
// WebView2 init is async; InitializeAsync() must be awaited before use.
// WindowsNativeWindow.CreateWebView spins a Task, awaits init, then fires the callback.

using Microsoft.Web.WebView2.Core;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Keystone.Core.Platform.Windows;

public class WindowsWebView : IWebView
{
    private readonly IntPtr _parentHwnd;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private bool _disposed;

    // JS bridge: map name → handler for routing WebMessageReceived
    private readonly Dictionary<string, Action<string>> _messageHandlers = new();

    // webkit shim injected once, routes postMessage by name
    private const string WebkitShim = """
        if (!window.webkit) {
            window.webkit = { messageHandlers: new Proxy({}, {
                get: (_, name) => ({
                    postMessage: (body) =>
                        window.chrome.webview.postMessage(JSON.stringify({ name, body: String(body) }))
                })
            })};
        }
        """;

    public Action? OnCrash { get; set; }

    public WindowsWebView(IntPtr parentHwnd)
    {
        _parentHwnd = parentHwnd;
    }

    public async Task InitializeAsync()
    {
        var userDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Keystone", "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

        _controller = await env.CreateCoreWebView2ControllerAsync(_parentHwnd);
        _webView = _controller.CoreWebView2;

        _controller.DefaultBackgroundColor = Color.Transparent;

        // Inject the webkit shim on every page load
        await _webView.AddScriptToExecuteOnDocumentCreatedAsync(WebkitShim);

        // Route all incoming messages to the registered handler by name
        _webView.WebMessageReceived += OnWebMessageReceived;

        // Crash/process-failed recovery
        _webView.ProcessFailed += (_, args) =>
        {
            if (args.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited ||
                args.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited)
                OnCrash?.Invoke();
        };

        Console.WriteLine("[WindowsWebView] WebView2 initialized");
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var json = e.TryGetWebMessageAsString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var name = root.GetProperty("name").GetString() ?? "";
            var body = root.GetProperty("body").GetString() ?? "";

            if (_messageHandlers.TryGetValue(name, out var handler))
                handler(body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WindowsWebView] Message parse error: {ex.Message}");
        }
    }

    public void LoadUrl(string url)
    {
        _webView?.Navigate(url);
    }

    public void EvaluateJavaScript(string js)
    {
        if (_webView == null) return;
        _ = _webView.ExecuteScriptAsync(js);
    }

    public void EvaluateJavaScriptBool(string js, Action<bool> completion)
    {
        if (_webView == null) { completion(false); return; }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _webView.ExecuteScriptAsync(js);
                // ExecuteScriptAsync returns JSON — "true" / "false" / "null"
                completion(result == "true");
            }
            catch
            {
                completion(false);
            }
        });
    }

    public void InjectScriptOnLoad(string js)
    {
        if (_webView == null) return;
        _ = _webView.AddScriptToExecuteOnDocumentCreatedAsync(js);
    }

    public void AddMessageHandler(string name, Action<string> handler)
    {
        _messageHandlers[name] = handler;
        // No per-name registration needed — WebMessageReceived handles all messages,
        // routed by the "name" field in the JSON payload from the webkit shim.
    }

    public void RemoveMessageHandler(string name)
    {
        _messageHandlers.Remove(name);
    }

    public void SetFrame(double x, double y, double w, double h)
    {
        if (_controller == null) return;
        _controller.Bounds = new Rectangle((int)x, (int)y, (int)w, (int)h);
    }

    public void SetTransparentBackground()
    {
        if (_controller != null)
            _controller.DefaultBackgroundColor = Color.Transparent;
    }

    public void RemoveFromParent()
    {
        if (_controller != null)
            _controller.IsVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_webView != null)
        {
            _webView.WebMessageReceived -= OnWebMessageReceived;
            _webView = null;
        }

        _controller?.Close();
        _controller = null;
    }
}
