/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

using System.Collections.Concurrent;
using Foundation;
using WebKit;

namespace Keystone.Core.Platform.MacOS;

/// <summary>
/// WKURLSchemeHandler that intercepts all requests for the app's custom URL scheme.
/// Default behavior proxies to the Bun HTTP server on loopback.
/// Apps can set OnIntercept to override individual requests.
/// </summary>
public class KeystoneSchemeHandler : NSObject, IWKUrlSchemeHandler
{
    /// <summary>The registered scheme name (e.g. "com-myapp-desktop").</summary>
    public string Scheme { get; }
    /// <summary>The full origin (e.g. "com-myapp-desktop://app").</summary>
    public string Origin { get; }

    private int _bunPort;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly ConcurrentDictionary<nuint, CancellationTokenSource> _active = new();

    public KeystoneSchemeHandler(string schemeName)
    {
        Scheme = schemeName;
        Origin = $"{schemeName}://app";
    }

    /// <summary>
    /// App-level intercept hook. Return non-null to override the default proxy.
    /// Receives (url, httpMethod) → SchemeResponse or null to proxy.
    /// </summary>
    public Func<string, string, SchemeResponse?>? OnIntercept { get; set; }

    public void SetBunPort(int port) => _bunPort = port;

    [Export("webView:startURLSchemeTask:")]
    public void StartUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        var request = urlSchemeTask.Request;
        var url = request.Url?.AbsoluteString ?? "";
        var method = request.HttpMethod ?? "GET";

        // Extract path from {scheme}://app/path → /path
        var path = url.StartsWith(Origin) ? url[Origin.Length..] : "/";
        if (string.IsNullOrEmpty(path)) path = "/";

        // Check app-level intercept
        var intercepted = OnIntercept?.Invoke(url, method);
        if (intercepted != null)
        {
            Respond(urlSchemeTask, intercepted);
            return;
        }

        // Default: proxy to Bun HTTP server
        var cts = new CancellationTokenSource();
        _active[(nuint)urlSchemeTask.Handle.Handle] = cts;
        ProxyToBun(urlSchemeTask, path, method, cts.Token);
    }

    [Export("webView:stopURLSchemeTask:")]
    public void StopUrlSchemeTask(WKWebView webView, IWKUrlSchemeTask urlSchemeTask)
    {
        if (_active.TryRemove((nuint)urlSchemeTask.Handle.Handle, out var cts))
            cts.Cancel();
    }

    private async void ProxyToBun(IWKUrlSchemeTask task, string path, string method, CancellationToken ct)
    {
        try
        {
            var targetUrl = $"http://127.0.0.1:{_bunPort}{path}";
            var request = new HttpRequestMessage(new HttpMethod(method), targetUrl);
            var response = await _http.SendAsync(request, ct);

            if (ct.IsCancellationRequested) return;

            var data = await response.Content.ReadAsByteArrayAsync(ct);
            if (ct.IsCancellationRequested) return;

            var contentType = response.Content.Headers.ContentType?.ToString() ?? GuessMimeType(path);
            var statusCode = (int)response.StatusCode;

            var headers = new NSMutableDictionary();
            headers.SetValueForKey(new NSString(contentType), new NSString("Content-Type"));
            headers.SetValueForKey(new NSString(data.Length.ToString()), new NSString("Content-Length"));
            // Allow cross-origin access from custom scheme to itself
            headers.SetValueForKey(new NSString("*"), new NSString("Access-Control-Allow-Origin"));

            var nsUrl = NSUrl.FromString($"{Origin}{path}") ?? NSUrl.FromString(Origin)!;
            var urlResponse = new NSHttpUrlResponse(nsUrl, statusCode, "HTTP/1.1", headers);

            task.DidReceiveResponse(urlResponse);
            task.DidReceiveData(NSData.FromArray(data));
            task.DidFinish();
        }
        catch (OperationCanceledException) { /* stopped by WebKit */ }
        catch (Exception ex)
        {
            try
            {
                var error = new NSError(NSError.NSUrlErrorDomain, -1,
                    NSDictionary.FromObjectAndKey(
                        new NSString(ex.Message),
                        NSError.LocalizedDescriptionKey));
                task.DidFailWithError(error);
            }
            catch { /* task may have been invalidated */ }
        }
        finally
        {
            _active.TryRemove((nuint)task.Handle.Handle, out _);
        }
    }

    private void Respond(IWKUrlSchemeTask task, SchemeResponse response)
    {
        var headers = new NSMutableDictionary();
        headers.SetValueForKey(new NSString(response.MimeType), new NSString("Content-Type"));
        headers.SetValueForKey(new NSString(response.Data.Length.ToString()), new NSString("Content-Length"));
        if (response.Headers != null)
            foreach (var (k, v) in response.Headers)
                headers.SetValueForKey(new NSString(v), new NSString(k));

        var nsUrl = NSUrl.FromString(Origin)!;
        var urlResponse = new NSHttpUrlResponse(nsUrl, response.StatusCode, "HTTP/1.1", headers);

        task.DidReceiveResponse(urlResponse);
        task.DidReceiveData(NSData.FromArray(response.Data));
        task.DidFinish();
    }

    private static string GuessMimeType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html",
            ".js" or ".mjs" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".ico" => "image/x-icon",
            ".wasm" => "application/wasm",
            _ => "application/octet-stream",
        };
    }
}

/// <summary>
/// Response returned by an app-level request interceptor.
/// </summary>
public record SchemeResponse(int StatusCode, string MimeType, byte[] Data, Dictionary<string, string>? Headers = null)
{
    public static SchemeResponse Html(string html, int statusCode = 200)
        => new(statusCode, "text/html", System.Text.Encoding.UTF8.GetBytes(html));

    public static SchemeResponse Json(string json, int statusCode = 200)
        => new(statusCode, "application/json", System.Text.Encoding.UTF8.GetBytes(json));

    public static SchemeResponse Redirect(string url)
        => new(302, "text/plain", Array.Empty<byte>(), new() { ["Location"] = url });

    public static SchemeResponse Blocked()
        => new(403, "text/plain", System.Text.Encoding.UTF8.GetBytes("Blocked by request interceptor"));
}
