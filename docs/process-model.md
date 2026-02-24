# Process Model

Keystone runs as three or more independent OS processes. Understanding this is the foundation for everything else — it determines how your code is structured, where state lives, and what boundaries you communicate across.

---

## The Processes

```
┌─────────────────────────────────────────────┐
│           C# Host Process                   │
│  .NET 10 / AppKit+Metal (macOS)             │
│           GTK4+Vulkan (Linux)               │
│                                             │
│  ┌─────────────┐   ┌────────────────────┐   │
│  │  App Layer  │   │   Plugin System    │   │
│  │  (ICorePlugin)   │   (DyLib hot-reload)  │   │
│  └─────────────┘   └────────────────────┘   │
│                                             │
│  ┌─────────────────────────────────────┐    │
│  │         Window Manager              │    │
│  │  ManagedWindow × N (per window)     │    │
│  │  GPU/Skia rendering, event routing  │    │
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
│  OS-managed          │
└──────────────────────┘
```

### C# Host Process

The main process. Owns the platform run loop (AppKit on macOS, GTK4 on Linux), creates and manages all native windows, drives the GPU/Skia rendering pipeline (Metal on macOS, Vulkan on Linux), and routes all input events. It also owns the lifecycle of all other processes.

Written in C# with direct access to native platform APIs on both macOS and Linux.

Everything in `Keystone.Core.Runtime` runs here. Your C# app layer (`ICorePlugin`) and hot-reloadable plugins (`IWindowPlugin`, `IServicePlugin`, `ILogicPlugin`, `ILibraryPlugin`) also run in this process.

### Bun Main Subprocess

A separate OS-level process (different PID) managed by `BunProcess`. The C# host spawns it on startup and communicates via `stdin`/`stdout` NDJSON and WebSocket.

Bun serves two things:
1. **Services** — TypeScript modules that respond to `query()` calls and push data to named channels via `subscribe()`.
2. **Web component bundle server** — An HTTP server that builds and serves your TypeScript UI components on demand, with HMR.

Bun is a separate OS-level process from C#, not a thread or module inside it.

### Bun Workers (0–N)

Additional Bun subprocesses, each running `worker-host.ts`. Workers have their own services directory and their own event loop. They communicate with C# via the same stdin/stdout NDJSON protocol as main Bun. Optionally, workers with `browserAccess: true` start a WebSocket server for direct high-throughput connections from other workers, main Bun, or browsers.

Workers are suited for parallelism and extension isolation — similar to what a UtilityProcess provides in other frameworks.

See [Workers](./workers.md) for full documentation.

### WebKit Content Process

When a window needs to show a web component, a WebKit view is created (WKWebView on macOS, WebKitGTK on Linux) and the OS-managed WebKit process spawns automatically. This is the process that actually renders the HTML/CSS/JS.

On macOS, one shared content process services all WKWebView slots in a window via the shared `WKProcessPool`.

---

## How Processes Communicate

The three processes form a full communication triangle — every pair can talk to each other in both directions. No leg is missing.

```
           Browser (WebKit)
          ↗ invoke / fetch        ↘ WebSocket push
         ↙ WebSocket push          ↗ invokeBun / query / send
    C#  ←———————— stdout ———————————  Bun
        ————————— stdin  ————————————→
```

---

### Browser → C# (invoke)

The fastest path from TypeScript to C#. Uses the WebKit script message handler — a direct in-process postMessage call from WebKit to the C# message handler. Zero Bun round-trip.

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

- **A WebKit content process crash** does not affect your C# host or Bun process. Keystone detects it via the platform WebView crash signal (macOS: `WKNavigationDelegate`, Linux: `web-process-terminated`) and reloads the WebView automatically.
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

## Framework Landscape

There are several frameworks in this space. Here's how they compare on the dimensions that matter structurally, followed by honest numbers.

| | Electron | Tauri | Electrobun | Electron.NET | Wails | Keystone Desktop |
|---|---|---|---|---|---|---|
| Main process language | Node.js (JS) | Rust | Bun (TS) | ASP.NET Core (C#) | Go | C# / .NET 10 |
| Renderer | Bundled Chromium | System WebView | System WebView | Bundled Chromium | System WebView | System WebKit (WKWebView / WebKitGTK) |
| Backend services language | JS/Node | Rust | TypeScript | C# | Go | TypeScript (Bun) |
| GPU/native rendering | No (HTML only) | No (HTML only) | No (HTML only) | No (HTML only) | No (HTML only) | Yes — Metal (macOS) / Vulkan (Linux) via SkiaSharp |
| Plugin hot-reload | No | No | No | No | No | Yes — collectible ALC, FileSystemWatcher |
| macOS | Yes | Yes | Yes (primary) | Yes | Yes | Yes |
| Linux | Yes | Yes | Partial | Yes | Yes | Yes |
| Windows | Yes | Yes | Planned | Yes | Yes | Planned |
| Baseline RAM (hello world) | ~100–150 MB | ~30–80 MB | ~50–100 MB | ~150–250 MB | ~50–80 MB | ~250–350 MB (React UI) |
| .app / binary size | ~150–200 MB | ~3–15 MB | ~14 MB | ~150–200 MB | ~5–20 MB | ~200 MB |

### On the numbers

Keystone Desktop's RAM footprint is in Electron territory, not Tauri/Wails territory. That's an honest characterization and worth understanding why.

Tauri and Wails ship a small native binary (Rust or Go) that links against the system WebView — no bundled runtime, very small. Keystone ships the .NET runtime (~30–50 MB) alongside the application, and runs three processes: C# host, Bun subprocess, and WebKit content process. Each has a real footprint. The tradeoff is deliberate: you get a genuinely capable main process in a mature typed language, hot-reloadable C# plugins, a full TypeScript/Bun service layer with SQLite and bundling built in, and a GPU rendering path that doesn't go through a browser at all.

Electron.NET adds an ASP.NET Core web server *inside* an Electron process — it's not a native desktop runtime, it's Electron with .NET wedged in. The architecture is fundamentally different from Keystone, where C# is the native host and owns the platform run loop directly.

The .app size is large because the .NET runtime is bundled. This is the same reason Electron apps are large — they bundle Chromium. The parallel holds: you get runtime capabilities in exchange for bundle size.

If binary size and baseline memory are the primary constraints, Tauri or Wails are strong choices. Keystone Desktop is for teams that want C# as a real main process — direct platform API access, hot-reloadable native code, GPU rendering — with TypeScript for services and UI, not bolted on as an afterthought.

---

## Going C# Only

The web layer is entirely optional. If you don't configure a `bun` block in `keystone.json`, Keystone doesn't spawn a Bun process and no WebView is created. Your app renders entirely in the GPU/Skia pipeline via `IWindowPlugin`.

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
                new TextNode("Hello from GPU/Skia"),
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
