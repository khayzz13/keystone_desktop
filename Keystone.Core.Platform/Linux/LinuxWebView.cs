// LinuxWebView — WebKitGTK implementation of IWebView.
// P/Invoke into libwebkit2gtk-4.1.so (WebKit2GTK 4.1 API, the current stable).
// Same WebKit engine as macOS WKWebView — JS bridge works identically.

using System.Runtime.InteropServices;

namespace Keystone.Core.Platform.Linux;

public class LinuxWebView : IWebView
{
    private IntPtr _webView;           // WebKitWebView*
    private IntPtr _contentManager;    // WebKitUserContentManager*
    private readonly IntPtr _parentWindow;
    private readonly IntPtr _overlay;
    private bool _disposed;

    // Must prevent delegates from being GC'd
    private readonly List<GCHandle> _pinnedDelegates = new();
    private readonly Dictionary<string, ulong> _messageSignals = new();

    public Action? OnCrash { get; set; }

    public LinuxWebView(IntPtr parentWindow, IntPtr overlay)
    {
        _parentWindow = parentWindow;
        _overlay = overlay;

        _contentManager = WebKit.UserContentManagerNew();
        _webView = WebKit.WebViewNewWithUserContentManager(_contentManager);

        // Set expand so WebView fills the overlay
        Gtk.WidgetSetHexpand(_webView, true);
        Gtk.WidgetSetVexpand(_webView, true);

        // Add as overlay child (on top of the drawing area)
        Gtk.OverlayAddOverlay(_overlay, _webView);

        // Connect crash handler
        var crashCb = new GtkCallbacks.GCallback(OnWebProcessTerminated);
        var handle = GCHandle.Alloc(crashCb);
        _pinnedDelegates.Add(handle);
        GLib.SignalConnectData(_webView, "web-process-terminated",
            crashCb, IntPtr.Zero, IntPtr.Zero, 0);
    }

    private void OnWebProcessTerminated(IntPtr webView, IntPtr userData)
    {
        OnCrash?.Invoke();
    }

    public void LoadUrl(string url)
    {
        if (_webView != IntPtr.Zero)
            WebKit.WebViewLoadUri(_webView, url);
    }

    public void EvaluateJavaScript(string js)
    {
        if (_webView != IntPtr.Zero)
            WebKit.WebViewEvaluateJavaScript(_webView, js, -1,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, null, IntPtr.Zero);
    }

    public void EvaluateJavaScriptBool(string js, Action<bool> completion)
    {
        if (_webView == IntPtr.Zero) { completion(false); return; }

        GtkCallbacks.GAsyncReadyCallback callback = (source, result, _) =>
        {
            var jsResult = WebKit.WebViewEvaluateJavaScriptFinish(source, result, IntPtr.Zero);
            // WebKit2GTK returns a JSCValue — check if it's a boolean true
            var boolVal = jsResult != IntPtr.Zero && WebKit.JscValueToBoolean(jsResult);
            completion(boolVal);
        };

        // Pin the callback to prevent GC
        var handle = GCHandle.Alloc(callback);
        _pinnedDelegates.Add(handle);

        WebKit.WebViewEvaluateJavaScript(_webView, js, -1,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
    }

    public void InjectScriptOnLoad(string js)
    {
        if (_contentManager == IntPtr.Zero) return;
        var script = WebKit.UserScriptNew(
            js,
            0, // WEBKIT_USER_CONTENT_INJECT_ALL_FRAMES
            0, // WEBKIT_USER_SCRIPT_INJECT_AT_DOCUMENT_START
            IntPtr.Zero, IntPtr.Zero);
        WebKit.UserContentManagerAddScript(_contentManager, script);
    }

    public void AddMessageHandler(string name, Action<string> handler)
    {
        if (_contentManager == IntPtr.Zero) return;

        WebKit.UserContentManagerRegisterScriptMessageHandler(_contentManager, name);

        var signalName = $"script-message-received::{name}";
        GtkCallbacks.GCallback callback = (instance, userData) =>
        {
            // The second argument to the signal is a WebKitJavascriptResult / JSCValue
            // In WebKit2GTK 4.1+, it's a JSCValue directly
            var jsValue = userData; // In the signal, userData is actually the JSCValue
            if (jsValue != IntPtr.Zero)
            {
                var str = WebKit.JscValueToString(jsValue);
                if (str != null)
                    handler(str);
            }
        };

        var handle = GCHandle.Alloc(callback);
        _pinnedDelegates.Add(handle);

        var signalId = GLib.SignalConnectData(_contentManager, signalName,
            callback, IntPtr.Zero, IntPtr.Zero, 0);
        _messageSignals[name] = signalId;
    }

    public void RemoveMessageHandler(string name)
    {
        if (_contentManager == IntPtr.Zero) return;
        WebKit.UserContentManagerUnregisterScriptMessageHandler(_contentManager, name);
    }

    public void SetFrame(double x, double y, double w, double h)
    {
        // GTK overlay doesn't support positioned children directly.
        // Would need CSS margin/padding or a GtkFixed container for exact positioning.
        // For now, set size request.
        if (_webView != IntPtr.Zero)
            Gtk.WidgetSetSizeRequest(_webView, (int)w, (int)h);
    }

    public void SetTransparentBackground()
    {
        if (_webView != IntPtr.Zero)
        {
            var color = new WebKit.GdkRGBA { Red = 0, Green = 0, Blue = 0, Alpha = 0 };
            WebKit.WebViewSetBackgroundColor(_webView, ref color);
        }
    }

    public void RemoveFromParent()
    {
        // In GTK4, removing from overlay requires gtk_overlay_remove_overlay
        // or simply hiding the widget
        if (_webView != IntPtr.Zero)
            Gtk.WidgetSetVisible(_webView, false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Unpin all delegates
        foreach (var handle in _pinnedDelegates)
            if (handle.IsAllocated) handle.Free();
        _pinnedDelegates.Clear();

        if (_webView != IntPtr.Zero)
        {
            Gtk.WidgetSetVisible(_webView, false);
            _webView = IntPtr.Zero;
        }
        _contentManager = IntPtr.Zero;
    }
}

// === WebKitGTK P/Invoke bindings ===

internal static class WebKit
{
    private const string Lib = "libwebkit2gtk-4.1.so.0";
    private const string JscLib = "libjavascriptcoregtk-4.1.so.0";

    [StructLayout(LayoutKind.Sequential)]
    public struct GdkRGBA
    {
        public double Red, Green, Blue, Alpha;
    }

    [DllImport(Lib, EntryPoint = "webkit_user_content_manager_new")]
    public static extern IntPtr UserContentManagerNew();

    [DllImport(Lib, EntryPoint = "webkit_web_view_new_with_user_content_manager")]
    public static extern IntPtr WebViewNewWithUserContentManager(IntPtr contentManager);

    [DllImport(Lib, EntryPoint = "webkit_web_view_load_uri")]
    public static extern void WebViewLoadUri(IntPtr webView,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);

    [DllImport(Lib, EntryPoint = "webkit_web_view_evaluate_javascript")]
    public static extern void WebViewEvaluateJavaScript(IntPtr webView,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string script, int length,
        IntPtr worldName, IntPtr sourceUri, IntPtr cancellable,
        GtkCallbacks.GAsyncReadyCallback? callback, IntPtr userData);

    [DllImport(Lib, EntryPoint = "webkit_web_view_evaluate_javascript_finish")]
    public static extern IntPtr WebViewEvaluateJavaScriptFinish(IntPtr webView,
        IntPtr result, IntPtr error);

    [DllImport(Lib, EntryPoint = "webkit_web_view_set_background_color")]
    public static extern void WebViewSetBackgroundColor(IntPtr webView, ref GdkRGBA color);

    [DllImport(Lib, EntryPoint = "webkit_user_script_new")]
    public static extern IntPtr UserScriptNew(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string source,
        int injectedFrames, int injectionTime,
        IntPtr allowList, IntPtr blockList);

    [DllImport(Lib, EntryPoint = "webkit_user_content_manager_add_script")]
    public static extern void UserContentManagerAddScript(IntPtr manager, IntPtr script);

    [DllImport(Lib, EntryPoint = "webkit_user_content_manager_register_script_message_handler")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UserContentManagerRegisterScriptMessageHandler(
        IntPtr manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Lib, EntryPoint = "webkit_user_content_manager_unregister_script_message_handler")]
    public static extern void UserContentManagerUnregisterScriptMessageHandler(
        IntPtr manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    // JSCValue
    [DllImport(JscLib, EntryPoint = "jsc_value_to_string")]
    [return: MarshalAs(UnmanagedType.LPUTF8Str)]
    public static extern string? JscValueToString(IntPtr value);

    [DllImport(JscLib, EntryPoint = "jsc_value_to_boolean")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool JscValueToBoolean(IntPtr value);
}
