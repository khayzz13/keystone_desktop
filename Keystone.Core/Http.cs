// HTTP bridge types — shared between Keystone.Core (interfaces) and Keystone.Core.Management (implementation).
//
// These types represent the request/response model for the HTTP-over-invoke() bridge.
// See HttpRouter.cs in Keystone.Core.Management for the full architecture description.

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Keystone.Core;

/// <summary>
/// An incoming HTTP-style request routed from the browser via the invoke() bridge.
/// Constructed by HttpRouter when the browser's fetch() intercept fires.
/// </summary>
public class HttpRequest
{
    /// <summary>HTTP method: GET, POST, PUT, DELETE, PATCH.</summary>
    public string Method { get; init; } = "GET";

    /// <summary>The request path, e.g. "/api/notes" or "/api/notes/42".</summary>
    public string Path { get; init; } = "/";

    /// <summary>
    /// Query string parameters parsed from the URL.
    /// e.g. fetch("/api/notes?limit=20") → Query["limit"] == "20"
    /// </summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Request body as a JsonElement.
    /// For GET requests this is typically undefined.
    /// For POST/PUT it's the JSON body passed to fetch().
    ///
    /// Example — reading a typed body:
    ///   var note = req.Body.Deserialize&lt;CreateNoteRequest&gt;();
    /// </summary>
    public JsonElement Body { get; init; }

    /// <summary>The window ID that originated this request. Useful for window-scoped replies.</summary>
    public string WindowId { get; init; } = "";

    /// <summary>
    /// Route parameters extracted from the path pattern.
    /// For a route "/api/notes/:id" matched against "/api/notes/42", Params["id"] == "42".
    /// </summary>
    public IReadOnlyDictionary<string, string> Params { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// The response your handler returns to the browser.
/// Use the static factory methods rather than constructing directly.
/// </summary>
public class HttpResponse
{
    public int Status { get; init; } = 200;
    public string ContentType { get; init; } = "application/json";
    public object? Body { get; init; }
    public bool IsStream { get; init; }

    /// <summary>
    /// The async function that writes stream chunks.
    /// Only set when IsStream == true (created via HttpResponse.Stream()).
    /// </summary>
    public Func<IHttpStreamWriter, Task>? StreamWriter { get; init; }

    // ── Factory methods ───────────────────────────────────────────────────

    /// <summary>
    /// Respond with a JSON-serializable object. Status defaults to 200.
    ///
    ///   return HttpResponse.Json(new { id = 1, title = "My note" });
    ///   return HttpResponse.Json(notesList, 201); // 201 Created
    /// </summary>
    public static HttpResponse Json(object? body, int status = 200) => new()
    {
        Status = status,
        ContentType = "application/json",
        Body = body,
    };

    /// <summary>Plain text response.</summary>
    public static HttpResponse Text(string body, int status = 200) => new()
    {
        Status = status,
        ContentType = "text/plain",
        Body = body,
    };

    /// <summary>Empty 204 No Content — use for DELETE or mutations with no return value.</summary>
    public static HttpResponse NoContent() => new() { Status = 204 };

    /// <summary>404 Not Found.</summary>
    public static HttpResponse NotFound(string? message = null) => new()
    {
        Status = 404,
        ContentType = "application/json",
        Body = new { error = message ?? "Not found" },
    };

    /// <summary>
    /// Error response. Status defaults to 500.
    ///   return HttpResponse.Error("Database unavailable", 503);
    /// </summary>
    public static HttpResponse Error(string message, int status = 500) => new()
    {
        Status = status,
        ContentType = "application/json",
        Body = new { error = message },
    };

    /// <summary>
    /// Streaming response. The handler receives an IHttpStreamWriter and pushes
    /// chunks asynchronously. The browser receives a ReadableStream from fetch().
    ///
    /// Example — stream a log file tail:
    ///   return HttpResponse.Stream(async stream => {
    ///       await foreach (var line in log.ReadAsync())
    ///           await stream.WriteAsync(line);
    ///   });
    ///
    /// Browser:
    ///   const res = await fetch("/api/logs/stream");
    ///   const reader = res.body!.getReader();
    ///   while (true) {
    ///       const { done, value } = await reader.read();
    ///       if (done) break;
    ///       console.log(new TextDecoder().decode(value));
    ///   }
    /// </summary>
    public static HttpResponse Stream(Func<IHttpStreamWriter, Task> writer) => new()
    {
        Status = 200,
        ContentType = "application/octet-stream",
        IsStream = true,
        StreamWriter = writer,
    };
}

/// <summary>
/// Write chunks to the browser from a streaming HTTP handler.
/// Obtained from the HttpResponse.Stream() callback parameter.
/// </summary>
public interface IHttpStreamWriter
{
    /// <summary>Push a chunk of data. The browser reads it from the ReadableStream.</summary>
    Task WriteAsync(object chunk);

    /// <summary>Push a text chunk.</summary>
    Task WriteAsync(string text);
}
