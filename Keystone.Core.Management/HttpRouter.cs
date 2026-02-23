// HttpRouter — C# HTTP-style route handlers over the existing invoke() bridge.
//
// The big idea
// ─────────────
// Keystone already has a fast two-way channel between the browser and C#:
//   - Browser → C#: invoke("channel", args)  (direct WKWebView postMessage, no Bun round-trip)
//   - C# → Browser: BunManager.Push(channel, data)  (WebSocket broadcast)
//
// HttpRouter sits on top of that. The browser-side SDK intercepts fetch() calls
// for paths under the prefix (default "/api/") and converts them into invoke()
// calls instead of real HTTP requests. C# receives them here, dispatches to the
// registered handler, and returns the response — all through the existing bridge.
//
// From the developer's perspective it looks exactly like writing an API server:
//
//   C# (in ICorePlugin.Initialize):
//     context.Http.Get("/api/notes", async req => {
//         var notes = await db.GetAllNotesAsync();
//         return HttpResponse.Json(notes);
//     });
//
//   TypeScript (in any web component):
//     const notes = await fetch("/api/notes").then(r => r.json());
//
// No second process. No port management. No CORS. No Kestrel.
// The handler runs inside the same C# runtime that owns the windows and database.
//
// Route parameters
// ─────────────────
// Patterns support :param segments:
//   context.Http.Get("/api/notes/:id", req => {
//       var id = req.Params["id"];
//       return HttpResponse.Json(db.GetNote(id));
//   });
//
// Streaming
// ─────────
// For streaming responses (live logs, long-running jobs, progress) return
// HttpResponse.Stream(). Each WriteAsync() call pushes a chunk to the browser
// via BunManager.Push(). The browser receives a normal ReadableStream from fetch().
//
//   context.Http.Get("/api/logs/stream", async (req, stream) => {
//       await foreach (var line in logTail.ReadAsync())
//           await stream.WriteAsync(line);
//   });
//
//   Browser:
//     const res = await fetch("/api/logs/stream");
//     const reader = res.body!.getReader();
//     while (true) {
//       const { done, value } = await reader.read();
//       if (done) break;
//       console.log(new TextDecoder().decode(value));
//     }

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Keystone.Core;
using Keystone.Core.Management.Bun;
using Keystone.Core.Plugins;

namespace Keystone.Core.Management;

// ── Stream writer implementation ──────────────────────────────────────────────

internal class HttpStreamWriter : IHttpStreamWriter
{
    private readonly string _streamChannel;

    internal HttpStreamWriter(string streamChannel)
    {
        _streamChannel = streamChannel;
    }

    public Task WriteAsync(object chunk)
    {
        BunManager.Instance.Push(_streamChannel, new { chunk });
        return Task.CompletedTask;
    }

    public Task WriteAsync(string text) => WriteAsync((object)text);
}

// ── Route matching ────────────────────────────────────────────────────────────

internal class Route
{
    public string Method { get; }
    public string Pattern { get; }
    private readonly string[] _segments;
    private readonly bool _hasParams;
    public Func<HttpRequest, Task<HttpResponse>> Handler { get; }

    public Route(string method, string pattern, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        Method = method.ToUpperInvariant();
        Pattern = pattern;
        Handler = handler;
        _segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        _hasParams = pattern.Contains(':');
    }

    public bool TryMatch(string method, string path, out Dictionary<string, string> routeParams)
    {
        routeParams = new Dictionary<string, string>();
        if (!string.Equals(Method, method, StringComparison.OrdinalIgnoreCase)) return false;

        if (!_hasParams)
            return string.Equals(Pattern, path, StringComparison.OrdinalIgnoreCase);

        var pathSegs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegs.Length != _segments.Length) return false;

        for (int i = 0; i < _segments.Length; i++)
        {
            if (_segments[i].StartsWith(':'))
                routeParams[_segments[i][1..]] = pathSegs[i];
            else if (!string.Equals(_segments[i], pathSegs[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }
}

// ── HttpRouter ────────────────────────────────────────────────────────────────

/// <summary>
/// Registers HTTP-style route handlers invoked when the browser's fetch() intercept fires.
/// Implements IHttpRouter — obtain via context.Http in ICorePlugin.Initialize().
///
/// Handlers run on a background thread and may freely use async/await.
/// For UI operations use context.RunOnMainThread().
///
/// Routes are matched in registration order. First match wins.
/// </summary>
public class HttpRouter : IHttpRouter
{
    private readonly List<Route> _routes = new();

    /// <summary>
    /// The invoke() channel name used for all HTTP bridge requests.
    /// Must match the channel name intercepted in bridge.ts.
    /// </summary>
    public const string InvokeChannel = "http:request";

    // ── IHttpRouter implementation ────────────────────────────────────────

    public IHttpRouter Get(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => Add("GET", path, handler);

    public IHttpRouter Post(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => Add("POST", path, handler);

    public IHttpRouter Put(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => Add("PUT", path, handler);

    public IHttpRouter Delete(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => Add("DELETE", path, handler);

    public IHttpRouter Patch(string path, Func<HttpRequest, Task<HttpResponse>> handler)
        => Add("PATCH", path, handler);

    // Sync convenience overloads
    public IHttpRouter Get(string path, Func<HttpRequest, HttpResponse> handler)
        => Add("GET", path, req => Task.FromResult(handler(req)));
    public IHttpRouter Post(string path, Func<HttpRequest, HttpResponse> handler)
        => Add("POST", path, req => Task.FromResult(handler(req)));
    public IHttpRouter Put(string path, Func<HttpRequest, HttpResponse> handler)
        => Add("PUT", path, req => Task.FromResult(handler(req)));
    public IHttpRouter Delete(string path, Func<HttpRequest, HttpResponse> handler)
        => Add("DELETE", path, req => Task.FromResult(handler(req)));

    private IHttpRouter Add(string method, string path, Func<HttpRequest, Task<HttpResponse>> handler)
    {
        _routes.Add(new Route(method, path, handler));
        return this;
    }

    // ── Dispatch ──────────────────────────────────────────────────────────

    /// <summary>
    /// Called by ManagedWindow when it receives an invoke("http:request", ...) message.
    /// Matches the request against registered routes, runs the handler, and returns
    /// a response object that ManagedWindow sends back via the standard invoke reply.
    /// </summary>
    public async Task<object?> DispatchAsync(JsonElement args, string windowId, string replyChannel)
    {
        var method = args.TryGetProperty("method", out var m) ? m.GetString() ?? "GET" : "GET";
        var path   = args.TryGetProperty("path",   out var p) ? p.GetString() ?? "/" : "/";
        var body   = args.TryGetProperty("body",   out var b) ? b : default;

        // Strip query string from path, parse into Query dict
        var query = new Dictionary<string, string>();
        var qIdx = path.IndexOf('?');
        if (qIdx >= 0)
        {
            var qs = path[(qIdx + 1)..];
            path = path[..qIdx];
            foreach (var part in qs.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0)
                    query[Uri.UnescapeDataString(part[..eq])] = Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }

        foreach (var route in _routes)
        {
            if (!route.TryMatch(method, path, out var routeParams)) continue;

            var req = new HttpRequest
            {
                Method   = method,
                Path     = path,
                Query    = query,
                Body     = body,
                WindowId = windowId,
                Params   = routeParams,
            };

            try
            {
                var response = await route.Handler(req);

                if (response.IsStream && response.StreamWriter != null)
                {
                    // Streaming: respond immediately with stream metadata, then push chunks
                    // to a dedicated channel. The bridge assembles them into a ReadableStream.
                    var streamChannel = $"{replyChannel}:stream";
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await response.StreamWriter(new HttpStreamWriter(streamChannel));
                        }
                        finally
                        {
                            BunManager.Instance.Push(streamChannel, new { done = true });
                        }
                    });

                    return new { status = response.Status, contentType = response.ContentType, stream = true, streamChannel };
                }

                return new { status = response.Status, contentType = response.ContentType, body = response.Body };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[HttpRouter] Handler error {method} {path}: {ex.Message}");
                return new { status = 500, contentType = "application/json", body = new { error = ex.Message } };
            }
        }

        return new { status = 404, contentType = "application/json", body = new { error = $"No route: {method} {path}" } };
    }
}
