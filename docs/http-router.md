# HTTP Router

The HTTP router is an optional convenience layer. It lets you write C# handler functions that the browser calls with a normal `fetch()`. There is no HTTP server — requests go through the existing bridge between the browser and C#.

```typescript
// Browser
const notes = await fetch("/api/notes").then(r => r.json());
```

```csharp
// C#
context.Http.Get("/api/notes", async req => {
    var notes = await db.GetAllNotesAsync();
    return HttpResponse.Json(notes);
});
```

If you don't register any routes, the router is simply unused — there is no overhead. Apps that prefer the Electron-style model (`invoke()`/`RegisterInvokeHandler`) can ignore `context.Http` entirely and nothing changes.

---

## Two models, both supported

Keystone supports two ways to call C# from the browser. Use whichever fits your app.

### Electron-style: `invoke()` + `RegisterInvokeHandler`

The lower-level model. The same one Electron's `ipcRenderer.invoke` / `ipcMain.handle` uses. Register a named channel on the C# side, call it by name from JS.

**C#:**
```csharp
// In ICorePlugin.Initialize(), or wired up via OnBeforeRun
window.RegisterInvokeHandler("myapp:getNote", async args => {
    var id = args.GetProperty("id").GetString()!;
    return await _db.GetNoteAsync(id);
});
```

**Browser:**
```typescript
import { invoke } from "@keystone/sdk/bridge";
const note = await invoke<Note>("myapp:getNote", { id: "123" });
```

Good for: platform calls, one-off operations, anything where the channel name is more natural than a URL.

### HTTP-style: `context.Http` + `fetch()`

A higher-level model built on top of `invoke()`. Register routes with methods and paths; call them with standard `fetch()`. The browser doesn't know or care that there's no HTTP server.

**C#:**
```csharp
context.Http.Get("/api/notes/:id", async req => {
    var note = await _db.GetNoteAsync(req.Params["id"]);
    return note != null ? HttpResponse.Json(note) : HttpResponse.NotFound();
});
```

**Browser:**
```typescript
const note = await fetch("/api/notes/123").then(r => r.json());
```

Good for: CRUD data, REST-shaped APIs, apps where the frontend team is more comfortable with fetch than with named channels, streaming responses.

Both models go through the same underlying `WKWebView` → C# bridge. There is no performance difference.

---

## How it works

The bridge already has a fast two-way channel between the browser and C#:
- Browser → C#: `invoke()` via `WKWebView` postMessage (no Bun round-trip)
- C# → Browser: `BunManager.Push()` via WebSocket

When `keystone()` initializes, it patches `window.fetch` to intercept requests whose path starts with `/api/`. Instead of making a real HTTP request, the intercepted fetch calls `invoke("http:request", ...)`, which routes to `HttpRouter.DispatchAsync()` on the C# side, which matches the route and runs your handler.

---

## Registering routes

Register routes in `ICorePlugin.Initialize()` via `context.Http`. The method is fluent — chain as many routes as you need:

```csharp
public void Initialize(ICoreContext context)
{
    context.Http
        .Get("/api/notes", async req => {
            var notes = await _db.GetAllAsync();
            return HttpResponse.Json(notes);
        })
        .Post("/api/notes", async req => {
            var input = req.Body.Deserialize<CreateNoteRequest>()!;
            var id = await _db.InsertAsync(input);
            return HttpResponse.Json(new { id }, status: 201);
        })
        .Delete("/api/notes/:id", req => {
            _db.Delete(req.Params["id"]);
            return HttpResponse.NoContent();
        });
}
```

Routes are matched in registration order. First match wins.

---

## Route parameters

Segments prefixed with `:` are captured into `req.Params`:

```csharp
context.Http.Get("/api/projects/:projectId/tasks/:taskId", req => {
    var projectId = req.Params["projectId"];
    var taskId    = req.Params["taskId"];
    return HttpResponse.Json(_db.GetTask(projectId, taskId));
});
```

---

## The request object

```csharp
public class HttpRequest
{
    string Method   // "GET", "POST", etc.
    string Path     // "/api/notes" (query string stripped)
    IReadOnlyDictionary<string, string> Query   // ?limit=20 → Query["limit"] == "20"
    IReadOnlyDictionary<string, string> Params  // :param captures from route pattern
    JsonElement Body    // parsed JSON body from fetch()
    string WindowId     // which window originated this request
}
```

Reading a typed body:

```csharp
context.Http.Post("/api/notes", async req => {
    var input = req.Body.Deserialize<CreateNoteRequest>()!;
    // ...
});
```

Reading query parameters:

```csharp
context.Http.Get("/api/notes", req => {
    var limit = req.Query.TryGetValue("limit", out var l) ? int.Parse(l) : 20;
    return HttpResponse.Json(_db.GetNotes(limit));
});
```

---

## Response types

```csharp
HttpResponse.Json(object? body, int status = 200)   // JSON — most common
HttpResponse.Text(string body, int status = 200)    // plain text
HttpResponse.NoContent()                            // 204 — for DELETE / fire-and-forget
HttpResponse.NotFound(string? message = null)       // 404
HttpResponse.Error(string message, int status = 500)
HttpResponse.Stream(Func<IHttpStreamWriter, Task>)  // streaming — see below
```

---

## Streaming responses

For long-running operations, progress, or live data, return `HttpResponse.Stream()`. The writer callback receives an `IHttpStreamWriter` — each `WriteAsync()` call pushes a chunk to the browser over WebSocket. The browser receives a standard `ReadableStream` from fetch.

**C#:**
```csharp
context.Http.Get("/api/export", req =>
    HttpResponse.Stream(async stream => {
        await foreach (var row in _db.StreamAllRowsAsync())
            await stream.WriteAsync(JsonSerializer.Serialize(row) + "\n");
    })
);
```

**Browser:**
```typescript
const res = await fetch("/api/export");
const reader = res.body!.getReader();
const decoder = new TextDecoder();

while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    const line = decoder.decode(value);
    console.log(JSON.parse(line));
}
```

---

## Threading

Handlers run on a thread pool thread — they can freely `await` async operations. For AppKit APIs dispatch to the main thread:

```csharp
context.Http.Post("/api/open-in-window", async req => {
    var path = req.Body.GetProperty("path").GetString()!;
    context.RunOnMainThread(() => _windowManager.OpenFile(path));
    return HttpResponse.NoContent();
});
```

---

## Intercepted prefix

Only paths starting with `/api/` are intercepted. Everything else falls through to the real `fetch()` and hits the Bun HTTP server or the network as normal. The prefix is not configurable yet.

---

## Related

- [Native API Reference](./native-api.md) — built-in `invoke()` channels (`app`, `dialog`, `shell`, `window`)
- [C# App Layer](./csharp-app-layer.md) — `RegisterInvokeHandler`, lifecycle events, plugin structure
