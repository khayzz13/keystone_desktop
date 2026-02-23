# Process Model

Keystone runs as three or more independent OS processes. Understanding this is the foundation for everything else — it determines how your code is structured, where state lives, and what boundaries you communicate across.

---

## The Processes

```
┌─────────────────────────────────────────────┐
│           C# Host Process                   │
│  .NET 10 / AppKit / Metal / SkiaSharp       │
│                                             │
│  ┌─────────────┐   ┌────────────────────┐   │
│  │  App Layer  │   │   Plugin System    │   │
│  │  (ICorePlugin)   │   (DyLib hot-reload)  │   │
│  └─────────────┘   └────────────────────┘   │
│                                             │
│  ┌─────────────────────────────────────┐    │
│  │         Window Manager              │    │
│  │  ManagedWindow × N (per window)     │    │
│  │  Metal rendering, event routing     │    │
│  └─────────────────────────────────────┘    │
│                         ↕ stdin/stdout      │
└──────────────┬──────────────────────────────┘
               │
       ┌───────┴───────────────────┐
       ▼                           ▼
┌──────────────────────┐  ┌──────────────────────┐
│   Bun Main (host.ts) │  │  Bun Worker × N      │
│   Services + Web     │  │  (worker-host.ts)     │
│   component server   │  │  Services only        │
│        ↕ WebSocket   │  │  Optional WebSocket   │
└──────────────────────┘  └──────────────────────┘
       │                           │
       ▼                    (direct WS when
┌──────────────────────┐     browserAccess=true)
│  WebKit Content      │
│  Process (per window)│
│  Apple-managed       │
└──────────────────────┘
```

### C# Host Process

The main process. Owns the NSApplication run loop, creates and manages all native windows (AppKit `NSWindow`), drives the Metal/Skia rendering pipeline, and routes all input events. It also owns the lifecycle of all other processes.

This is the closest equivalent to Electron's **main process** — written in C# with direct access to the full native macOS API surface.

Everything in `Keystone.Core.Runtime` runs here. Your C# app layer (`ICorePlugin`) and hot-reloadable plugins (`IWindowPlugin`, `IServicePlugin`, `ILogicPlugin`, `ILibraryPlugin`) also run in this process.

### Bun Main Subprocess

A separate OS-level process (different PID) managed by `BunProcess`. The C# host spawns it on startup and communicates via `stdin`/`stdout` NDJSON and WebSocket.

Bun serves two things:
1. **Services** — TypeScript modules that respond to `query()` calls and push data to named channels via `subscribe()`.
2. **Web component bundle server** — An HTTP server that builds and serves your TypeScript UI components on demand, with HMR.

There is no Electron equivalent at exactly this level. The closest analogy is Electron's main process, but Bun is separate from C# rather than sharing a process with it.

### Bun Workers (0–N)

Additional Bun subprocesses, each running `worker-host.ts`. Workers have their own services directory and their own event loop. They communicate with C# via the same stdin/stdout NDJSON protocol as main Bun. Optionally, workers with `browserAccess: true` start a WebSocket server for direct high-throughput connections from other workers, main Bun, or browsers.

Workers are the equivalent of Electron's `UtilityProcess` (for parallelism) and VS Code's extension host (for extension isolation).

See [Workers](./workers.md) for full documentation.

### WebKit Content Process

When a window needs to show a web component, a `WKWebView` is created and Apple's WebKit spawns a sandboxed content process automatically. This is the GPU process that actually renders the HTML/CSS/JS.

This is equivalent to Electron's **renderer process** — but Apple manages it entirely. One shared content process services all `WKWebView` slots in a window (via the shared `WKProcessPool`).

---

## How Processes Communicate

The three processes form a full communication triangle — every pair can talk to each other in both directions. No leg is missing.

```
           Browser (WKWebView)
          ↗ invoke / fetch        ↘ WebSocket push
         ↙ WebSocket push          ↗ invokeBun / query / send
    C#  ←———————— stdout ———————————  Bun
        ————————— stdin  ————————————→
```

---

### Browser → C# (invoke)

The fastest path from TypeScript to C#. Uses `WKScriptMessageHandler` — a direct in-process postMessage call from WebKit to the C# message handler. Zero Bun round-trip.

```typescript
import { invoke, dialog } from "@keystone/sdk/bridge";

// Built-in handlers: dialog:*, app:*, shell:*, window:*
const paths = await dialog.openFile({ multiple: true, filters: [".png", ".jpg"] });

// Custom C# handlers registered with RegisterInvokeHandler
const result = await invoke("myapp:processFile", { path: "/tmp/foo.png" });
```

```csharp
window.RegisterInvokeHandler("myapp:processFile", async args => {
    var path = args.GetProperty("path").GetString()!;
    return await ProcessAsync(path);
});
```

Also available: `fetch("/api/...")` — intercepted by `bridge.ts` and routed through `HttpRouter` on the C# side. Same underlying invoke mechanism, REST-shaped API. See [HTTP Router](./http-router.md).

---

### C# → Browser (push)

C# can push data to any named WebSocket channel at any time. The browser subscribes and receives updates without polling.

```csharp
// Push to a specific window
BunManager.Instance.Push($"window:{windowId}:notification", new { title = "Done" });

// Push to all subscribers of a shared channel
BunManager.Instance.Push("prices:btc", new { usd = 62_000 });
```

```typescript
import { subscribe } from "@keystone/sdk/bridge";

const unsub = subscribe("prices:btc", (data) => {
    priceEl.textContent = `$${data.usd.toLocaleString()}`;
});
```

Used internally for theme updates, HMR signals, invoke replies, and streaming response chunks.

---

### Browser → Bun (invokeBun / query / send)

For data that lives in Bun — live feeds, file watches, SQLite queries, background computation. Uses the WebSocket connection in `bridge.ts`.

**Named invoke** — the Electron `ipcRenderer.invoke()` equivalent, but targeting Bun:

```typescript
import { invokeBun } from "@keystone/sdk/bridge";

const notes = await invokeBun<Note[]>("notes:getAll");
const note  = await invokeBun<Note>("notes:create", { title: "Hello", body: "World" });
```

Register handlers in the service with `.handle()`:

```typescript
// services/notes.ts
import { defineService } from "@keystone/sdk/service";

export default defineService("notes")
    .handle("notes:getAll", async (_, svc) => svc.store.get("notes") ?? [])
    .handle("notes:create", async ({ title, body }, svc) => {
        const note = { id: crypto.randomUUID(), title, body };
        svc.store.set("notes", [...(svc.store.get("notes") ?? []), note]);
        return note;
    })
    .build();
```

**Whole-service query** — when a service has a single `onQuery` handler:

```typescript
import { query } from "@keystone/sdk/bridge";

const files = await query("file-scanner", { dir: "/tmp", extensions: [".log"] });
```

**Fire-and-forget** — raw typed message, services register handlers with `ctx.onWebMessage`:

```typescript
ks.send("chat:send", { text: "Hello", room: "general" });
```

---

### Bun → Browser (push)

Services push to named channels; all subscribed browser windows receive the update in real time.

```typescript
// bun/services/metrics.ts
import { defineService } from "@keystone/sdk/service";

export default defineService("metrics")
    .every(1000, async ({ push }) => {
        push("metrics:cpu", { percent: await getCpuPercent() });
    })
    .build();
```

```typescript
// Browser
subscribe("metrics:cpu", (data) => {
    gauge.textContent = `${data.percent}%`;
});
```

---

### C# → Bun (stdin NDJSON)

C# writes newline-delimited JSON to Bun's stdin. Used to forward actions, trigger service queries, eval scripts, and shut down gracefully.

```csharp
// Forward an action to all Bun service onAction handlers
BunManager.Instance.SendAction("export:pdf");

// Query a Bun service from C# (async, with reply)
var result = await BunManager.Instance.QueryAsync("file-scanner", new { dir = "/tmp" });
```

Message types over stdin: `query`, `action`, `eval`, `push`, `health`, `shutdown`.

---

### Bun → C# (stdout NDJSON)

Bun writes NDJSON to its own stdout; the C# host reads it on a background thread. Used for query results, service-initiated pushes that C# should relay to windows, actions dispatched from browser JS, and lifecycle signals.

```typescript
// bun/services/my-service.ts — trigger an action visible to C#
ctx.action("export:complete");
```

```csharp
// C# receives it via ActionRouter
context.OnUnhandledAction = (action, source) => {
    if (action == "export:complete") ShowExportDoneToast();
};
```

Message types over stdout: `result`, `error`, `service_push`, `action_from_web`, `hmr`, `ready`.

---

### C# ↔ Worker (stdin/stdout NDJSON)

Same protocol as C# ↔ main Bun. Workers add two message types:

- **`relay`** (worker → C#): `{"type":"relay","target":"other-worker","channel":"...","data":{...}}` — request routing to another worker or main Bun
- **`relay_in`** (C# → worker): `{"type":"relay_in","channel":"...","data":{...}}` — forwarded from another worker
- **`worker_ports`** (C# → worker): `{"type":"worker_ports","data":{"worker-name":54321}}` — port map for direct connections

```csharp
var result = await context.Workers["data-processor"].Query("heavy-compute", new { data = payload });
```

### Worker ↔ Worker (direct WebSocket)

When a worker has `browserAccess: true`, other workers can connect directly via WebSocket — bypassing C#. Used for high-throughput real-time data streaming.

```typescript
const dp = ctx.workers.connect("data-processor");
const result = await dp.query("heavy-compute", bigData);
dp.subscribe("ticks:realtime", processTick);
```

### Worker ↔ Main Bun

Main Bun services also receive the `worker_ports` message and can use `ctx.workers.connect()` and `ctx.relay()` to communicate with workers.

See [Workers](./workers.md) for the full communication protocol.

---

## Process Isolation Benefits

Because the three processes are truly separate:

- **A WebKit content process crash** does not affect your C# host or Bun process. Keystone detects it via `WKNavigationDelegate.ContentProcessDidTerminate` and reloads the WebView automatically.
- **A Bun process crash** does not affect your native windows or rendered UI. The C# host detects it via stdout EOF and restarts Bun with exponential backoff.
- **C# crashes** are the only fatal crash — but C# is significantly more stable than Node.js for native code since it has real memory ownership and no GC/finalizer races with OS APIs.

All recovery behavior is configurable in `keystone.json`:

```json
{
  "processRecovery": {
    "bunAutoRestart": true,
    "bunMaxRestarts": 5,
    "bunRestartBaseDelayMs": 500,
    "bunRestartMaxDelayMs": 30000,
    "webViewAutoReload": true,
    "webViewReloadDelayMs": 200
  }
}
```

Your app layer can hook into crash events:

```csharp
// In ICorePlugin.Initialize()
context.OnBunCrash += exitCode => {
    logger.Warn($"Bun exited with code {exitCode} — restarting");
};
context.OnBunRestart += attempt => {
    logger.Info($"Bun recovered (attempt {attempt})");
};
context.OnWebViewCrash += windowId => {
    logger.Warn($"WebView content process crashed in window {windowId}");
};
```

---

## Comparison with Electron

| | Electron | Keystone |
|---|---|---|
| Main process runtime | Node.js | C# / .NET 10 |
| Renderer process | Chromium per-window | WebKit per-window (Apple-managed) |
| IPC from renderer | `ipcRenderer.invoke()` | `invoke()` via `WKScriptMessageHandler` |
| IPC from main | `ipcMain.handle()` | `RegisterInvokeHandler()` |
| Background services | Worker threads in main | Bun subprocess (separate PID) |
| Native APIs | `electron.dialog`, `shell`, etc. | Built-in `dialog:*`, `shell:*`, `app:*` invoke handlers |
| Native UI | None (HTML everywhere) | Metal/Skia rendering + native AppKit windows |
| Crash isolation | Renderer process crash → recovery event | WebKit crash → auto-reload; Bun crash → auto-restart |
| Process count | 2+ (main + one renderer per window) | 3+ (C# host + Bun main + WebKit content + 0–N workers) |
| Native code | N-API addons | Direct C#, FFI, P/Invoke, platform frameworks |

The key structural difference: Electron's main process is JavaScript running in Node. Keystone's main process is C# with direct access to AppKit, Metal, CoreAnimation, and any macOS framework via P/Invoke or `.NET` bindings. The web layer in Keystone is optional — native Metal/Skia rendering is available without it.

---

## Going C# Only

The web layer is entirely optional. If you don't configure a `bun` block in `keystone.json`, Keystone doesn't spawn a Bun process and no WebView is created. Your app renders entirely in the Metal/Skia pipeline via `IWindowPlugin`.

```json
{
  "name": "Pure Native App",
  "id": "com.example.native"
}
```

```csharp
// A fully native app — no web layer at all
public class MyWindow : IWindowPlugin
{
    public string WindowType => "main";
    public (float w, float h) DefaultSize => (1200, 800);

    public SceneNode? BuildScene(FrameState state)
    {
        return new FlexNode
        {
            Direction = FlexDirection.Column,
            Children = [
                new TextNode("Hello from Metal/Skia"),
                new ButtonNode("Click me", onClick: () => action("do-thing"))
            ]
        };
    }
}
```

The C# layer can also be omitted — if there's no `app/*.csproj`, the runtime runs web-only with built-in window management and the standard `invoke` API surface.

---

## Next

- [Getting Started](./getting-started.md) — scaffold a project and run it
- [Workers](./workers.md) — additional Bun processes for parallelism and extension isolation
- [Web Components](./web-components.md) — writing UI in TypeScript
- [Native API Reference](./native-api.md) — `invoke`, `action`, `subscribe`, `dialog`, `shell`
- [HTTP Router](./http-router.md) — `fetch("/api/...")` over the invoke bridge; Electron-style `invoke()` also supported
- [C# App Layer](./csharp-app-layer.md) — `ICorePlugin`, custom invoke handlers, native-only windows
- [Plugin System](./plugin-system.md) — hot-reloadable DLL plugins: service, logic, library, window types
