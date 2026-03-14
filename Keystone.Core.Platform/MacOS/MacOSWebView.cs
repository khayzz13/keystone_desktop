/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using AppKit;
using Foundation;
using WebKit;

namespace Keystone.Core.Platform.MacOS;

public class MacOSWebView : IWebView
{
    private readonly WKWebView _wkWebView;
    private readonly string? _schemeName;
    private KeystoneMessageHandler? _messageHandler;
    private KeystoneWebViewDelegate? _delegate;

    public Action? OnCrash { get; set; }

    internal MacOSWebView(NSView contentView, KeystoneSchemeHandler? schemeHandler = null)
    {
        var config = new WKWebViewConfiguration
        {
            ApplicationNameForUserAgent = "Keystone"
        };

        // Register custom URL scheme handler — all {scheme}:// loads flow through C#
        if (schemeHandler != null)
        {
            config.SetUrlSchemeHandler(schemeHandler, schemeHandler.Scheme);
            _schemeName = schemeHandler.Scheme;
        }

        _wkWebView = new WKWebView(contentView.Bounds, config);
        _wkWebView.WantsLayer = true;
        _wkWebView.AutoresizingMask = NSViewResizingMask.WidthSizable | NSViewResizingMask.HeightSizable;
        contentView.AddSubview(_wkWebView);

        // Intercept right-click — suppress default browser menu, post metadata to C#
        InjectScriptOnLoad("""
            document.addEventListener('contextmenu', e => {
              const info = {
                type: '__contextmenu__',
                linkUrl: e.target.closest('a')?.href || null,
                imageUrl: e.target.closest('img')?.src || null,
                selectedText: window.getSelection()?.toString() || null,
                isEditable: e.target.isContentEditable ||
                  e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA',
                x: e.clientX, y: e.clientY,
              };
              window.webkit.messageHandlers.keystone.postMessage(JSON.stringify(info));
              e.preventDefault();
            }, true);
            """);
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

    public void EvaluateJavaScript(string js, Action<string?> completion)
    {
        InvokeOnMain(() =>
            _wkWebView.EvaluateJavaScript(js, (result, _) =>
                completion(result?.ToString())));
    }

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

        _delegate = new KeystoneWebViewDelegate(() => OnCrash?.Invoke(), _schemeName);
        _wkWebView.NavigationDelegate = _delegate;
    }

    public void RemoveMessageHandler(string name)
        => _wkWebView.Configuration.UserContentController.RemoveScriptMessageHandler(name);

    public void SetFrame(double x, double y, double w, double h)
        => InvokeOnMain(() => _wkWebView.Frame = new CoreGraphics.CGRect(x, y, w, h));

    public void SetTransparentBackground()
        => _wkWebView.SetValueForKey(NSObject.FromObject(false), new NSString("drawsBackground"));

    public void SetInspectable(bool inspectable)
        => InvokeOnMain(() => _wkWebView.Inspectable = inspectable);

    /// <summary>Set a navigation policy callback. Return false to block navigation to a URL.</summary>
    public void SetNavigationPolicy(Func<string, bool>? policy)
    {
        if (_delegate != null)
            _delegate.NavigationPolicy = policy;
    }

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

    // === Inner classes ===

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
        private readonly string? _schemePrefix; // e.g. "com-myapp-desktop://"

        /// <summary>Optional navigation policy. Return false to block navigation.</summary>
        public Func<string, bool>? NavigationPolicy { get; set; }

        public KeystoneWebViewDelegate(Action onCrash, string? schemeName = null)
        {
            _onCrash = onCrash;
            _schemePrefix = schemeName != null ? $"{schemeName}://" : null;
        }

        public override void ContentProcessDidTerminate(WKWebView webView)
        {
            Console.WriteLine("[KeystoneWebViewDelegate] WebKit content process terminated — reloading");
            _onCrash();
        }

        public override void DecidePolicy(WKWebView webView, WKNavigationAction navigationAction,
            Action<WKNavigationActionPolicy> decisionHandler)
        {
            var url = navigationAction.Request.Url?.AbsoluteString ?? "";

            // Always allow custom scheme and localhost navigation
            if (url.StartsWith("http://127.0.0.1") ||
                (_schemePrefix != null && url.StartsWith(_schemePrefix)))
            {
                decisionHandler(WKNavigationActionPolicy.Allow);
                return;
            }

            // Check app-defined policy
            if (NavigationPolicy != null && !NavigationPolicy(url))
            {
                decisionHandler(WKNavigationActionPolicy.Cancel);
                return;
            }

            decisionHandler(WKNavigationActionPolicy.Allow);
        }
    }
}
