using AppKit;
using Foundation;
using WebKit;

namespace Keystone.Core.Platform.MacOS;

public class MacOSWebView : IWebView
{
    private readonly WKWebView _wkWebView;
    private KeystoneMessageHandler? _messageHandler;
    private KeystoneWebViewDelegate? _delegate;

    public Action? OnCrash { get; set; }

    internal MacOSWebView(NSView contentView, WKProcessPool pool)
    {
        var config = new WKWebViewConfiguration
        {
            ProcessPool = pool,
            ApplicationNameForUserAgent = "Keystone"
        };

        _wkWebView = new WKWebView(contentView.Bounds, config);
        _wkWebView.WantsLayer = true;
        _wkWebView.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        contentView.AddSubview(_wkWebView);
    }

    public void LoadUrl(string url)
    {
        InvokeOnMain(() =>
        {
            var nsUrl = NSUrl.FromString(url);
            if (nsUrl != null)
                _wkWebView.LoadRequest(new NSUrlRequest(nsUrl));
        });
    }

    public void EvaluateJavaScript(string js)
        => InvokeOnMain(() => _wkWebView.EvaluateJavaScript(js, null));

    public void EvaluateJavaScriptBool(string js, Action<bool> completion)
    {
        InvokeOnMain(() =>
            _wkWebView.EvaluateJavaScript(js, (result, _) =>
                completion(result is NSNumber n && n.BoolValue)));
    }

    public void InjectScriptOnLoad(string js)
    {
        var script = new WKUserScript(
            new NSString(js),
            WKUserScriptInjectionTime.AtDocumentStart, true);
        _wkWebView.Configuration.UserContentController.AddUserScript(script);
    }

    public void AddMessageHandler(string name, Action<string> handler)
    {
        _messageHandler = new KeystoneMessageHandler(handler);
        _wkWebView.Configuration.UserContentController.AddScriptMessageHandler(_messageHandler, name);

        _delegate = new KeystoneWebViewDelegate(() => OnCrash?.Invoke());
        _wkWebView.NavigationDelegate = _delegate;
    }

    public void RemoveMessageHandler(string name)
        => _wkWebView.Configuration.UserContentController.RemoveScriptMessageHandler(name);

    public void SetFrame(double x, double y, double w, double h)
        => InvokeOnMain(() => _wkWebView.Frame = new CoreGraphics.CGRect(x, y, w, h));

    public void SetTransparentBackground()
        => _wkWebView.SetValueForKey(NSObject.FromObject(false), new NSString("drawsBackground"));

    public void RemoveFromParent()
        => InvokeOnMain(() => _wkWebView.RemoveFromSuperview());

    public void Dispose()
    {
        InvokeOnMain(() =>
        {
            if (_messageHandler != null)
                _wkWebView.Configuration.UserContentController.RemoveScriptMessageHandler("keystone");
            _wkWebView.NavigationDelegate = null;
            _wkWebView.RemoveFromSuperview();
            _wkWebView.Dispose();
        });
        _messageHandler = null;
        _delegate = null;
    }

    private static void InvokeOnMain(Action a)
        => NSApplication.SharedApplication.InvokeOnMainThread(a);

    // === Inner classes (moved from ManagedWindow.cs) ===

    internal sealed class KeystoneMessageHandler : WKScriptMessageHandler
    {
        private readonly Action<string> _onMessage;

        public KeystoneMessageHandler(Action<string> onMessage)
            => _onMessage = onMessage;

        public override void DidReceiveScriptMessage(
            WKUserContentController userContentController, WKScriptMessage message)
        {
            var body = message.Body?.ToString();
            if (body != null)
                _onMessage(body);
        }
    }

    internal sealed class KeystoneWebViewDelegate : WKNavigationDelegate
    {
        private readonly Action _onCrash;

        public KeystoneWebViewDelegate(Action onCrash)
            => _onCrash = onCrash;

        public override void ContentProcessDidTerminate(WKWebView webView)
        {
            Console.WriteLine("[KeystoneWebViewDelegate] WebKit content process terminated — reloading");
            _onCrash();
        }
    }
}
