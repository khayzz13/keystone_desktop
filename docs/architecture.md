# Architecture & Getting Started

> Last updated: 2026-02-26

## Documentation

| Document | Contents |
|----------|----------|
| **Architecture & Getting Started** (this file) | Process model, IPC, getting started, project modes |
| [Bun Layer](./bun-layer.md) | Web components, services, workers, host.ts |
| [C# Layer](./csharp-layer.md) | ICorePlugin, plugin system, HTTP router, programmatic bootstrap |
| [SDK Reference](./sdk-reference.md) | Bridge API, invoke/subscribe/action, dialog, shell, clipboard, theme |
| [Configuration](./configuration.md) | keystone.json, keystone.config.ts, window chrome, build & packaging |

---

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

Also available: `fetch("/api/...")` — intercepted by `bridge.ts` and routed through `HttpRouter` on the C# side. Same underlying invoke mechanism, REST-shaped API. See [C# Layer — HTTP Router](./csharp-layer.md#http-router).

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

There are several frameworks in this space. Here's how they compare on the dimensions that matter structurally, followed by honest numbers. It should be noted that Keystone Desktop is in early stages and not intended for production use cases in current form. 

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

Tauri and Wails ship a small native binary (Rust or Go) that links against the system WebView — no bundled runtime, very small. Keystone ships the .NET runtime (~50+MB), and Bun runtime alongside the application, and runs three processes: C# host, Bun subprocess, and WebKit content process. Each has a real footprint. The tradeoff is deliberate: you get a genuinely capable main process in a mature typed language, hot-reloadable C# plugins, a full TypeScript/Bun service layer with SQLite and bundling built in, and a GPU rendering path that doesn't go through a browser at all.

Electron.NET adds an ASP.NET Core web server *inside* an Electron process — it's not a native desktop runtime, it's Electron with .NET wedged in. The architecture is fundamentally different from Keystone, where C# is the native host and owns the platform run loop directly.

The .app size is large because the .NET runtime is bundled. This is the same reason Electron apps are large — they bundle Chromium. The parallel holds: you get runtime capabilities in exchange for bundle size.

If binary size and baseline memory are the primary constraints, Tauri or Wails are strong choices. Keystone Desktop is intend for developer that wants C# as a real main process — direct platform API access, hot-reloadable native code, GPU rendering if needed — with TypeScript for services and UI, not bolted on as an afterthought.

---

## Getting Started

### Requirements

**macOS:**
- macOS 15+ (Apple Silicon)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Bun](https://bun.sh) — `curl -fsSL https://bun.sh/install | bash`
- Rust toolchain — `curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh`

**Linux:**
- GTK4 and WebKitGTK 4.1 (`libgtk-4-dev`, `libwebkit2gtk-4.1-dev`)
- Vulkan drivers and headers (`vulkan-tools`, `libvulkan-dev`)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Bun](https://bun.sh) — `curl -fsSL https://bun.sh/install | bash`
- Rust toolchain — `curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh`

### Project Structure

A Keystone app is a directory with a `keystone.json` manifest and whatever layers you need:

```
my-app/
├── keystone.json           # App manifest — the only required file
├── build.py                # Build and run script
├── bun/                    # TypeScript layer (optional)
│   ├── package.json
│   ├── tsconfig.json
│   ├── host.ts             # Bun lifecycle hooks (optional)
│   ├── keystone.config.ts  # Bun runtime config
│   ├── web/
│   │   └── app.ts          # UI component
│   └── services/           # Background Bun services (optional)
├── app/                    # C# app layer (optional)
│   ├── MyApp.Core.csproj
│   └── App.cs              # ICorePlugin entry point
├── dylib/                  # Hot-reloadable C# plugin DLLs (optional)
│   └── native/             # Native dylibs (Rust, C)
└── icons/                  # App icons
```

None of the optional layers are required together — you can have web-only (no `app/`, no `dylib/`), native-only (no `bun/`), or any combination.

### The App Manifest

`keystone.json` is read by the runtime before anything else starts. For fully programmatic bootstrap without a config file, see [C# Layer — Programmatic Bootstrap](./csharp-layer.md#programmatic-bootstrap).

```json
{
  "name": "My App",
  "id": "com.example.myapp",
  "version": "0.1.0",

  "windows": [
    {
      "component": "app",
      "title": "My App",
      "width": 1024,
      "height": 700
    }
  ],

  "bun": { "root": "bun" }
}
```

Every key in `windows` corresponds to a component name. `"app"` maps to `bun/web/app.ts`. Windows open on launch by default — set `"spawn": false` to register a window type without opening it immediately.

### Build and Run

Each app ships its own `build.py` that knows its specific plugin layout and build order:

```bash
cd my-app
python3 build.py           # build plugins into dylib/
python3 build.py --package # build + produce MyApp.app in dist/
```

Common flags:

| Flag | Effect |
|------|--------|
| `--package` | Produce a `.app` bundle in `dist/` after building |
| `--bundle` | With `--package`: copy `dylib/` into the bundle (self-contained) |
| `--allow-external` | Allow plugins not signed by this app (disables library validation) |
| `--debug` | Debug configuration |
| `--clean` | Remove `bin/`/`obj/` before building |

### Your First Component

`bun/web/app.ts` is your main window's UI. It exports `mount` and `unmount` — plain functions called by the slot host.

```typescript
import { keystone, query, dialog } from "@keystone/sdk/bridge";

export function mount(root: HTMLElement) {
  root.style.cssText = `
    display: flex;
    flex-direction: column;
    height: 100%;
    background: var(--ks-bg-surface);
    color: var(--ks-text-primary);
    font-family: var(--ks-font);
    padding: 32px;
    gap: 16px;
  `;

  const h1 = document.createElement("h1");
  h1.textContent = "Hello, Keystone";

  const btn = document.createElement("button");
  btn.textContent = "Open File";
  btn.onclick = async () => {
    const paths = await dialog.openFile({ multiple: false });
    if (paths) h1.textContent = paths[0];
  };

  root.appendChild(h1);
  root.appendChild(btn);
}

export function unmount(root: HTMLElement) {
  root.innerHTML = "";
}
```

No HTML file. No bundler config. The runtime bundles `app.ts` and serves it into a managed host page. HMR is automatic — save the file, the component hot-swaps in place without reloading the window.

### What's Running at Runtime

When you run a web-mode app:

1. C# host starts. Reads `keystone.json`.
2. Bun subprocess spawns. `host.ts` discovers services and web components.
3. Bun writes a ready signal to stdout: `{ "status": "ready", "port": 3847, ... }`.
4. C# reads the ready signal. `BunManager` attaches.
5. First window spawns. C# creates the native window (NSWindow on macOS, GtkWindow on Linux) with its GPU surface.
6. Render thread starts. GPU/Skia draws native chrome if enabled.
7. A WebKit view is created (WKWebView on macOS, WebKitGTK on Linux). Loads `/__host__` from Bun.
8. The host page loads your `app.ts` component. `mount(root)` is called.
9. The bridge `keystone()` client initializes — WebSocket connects, theme tokens apply.

---

## Project Modes

Keystone supports three compositions. All three run the same runtime.

### Web-only (default)

TypeScript UI + Bun services. No C#. Covers the built-in `invoke()` API surface (`app:*`, `window:*`, `dialog:*`, `shell:*`). The `examples/docs-viewer` is a complete working example of this mode.

```jsonc
{
  "name": "My App",
  "id": "com.example.myapp",
  "windows": [{ "component": "app", "width": 1024, "height": 700 }],
  "bun": { "root": "bun" }
}
```

### Web + Native C#

TypeScript UI + Bun services + C# app layer. Register custom `invoke()` handlers, control window lifecycle, use platform APIs. Required for anything beyond the built-in API surface.

Add an `app/` directory with a `.csproj` and implement `ICorePlugin`. `build.py` detects the csproj automatically.

### Pure Native (C# only)

GPU/Skia rendering with no WebView, no Bun. Maximum performance — every pixel rendered by your plugin in the GPU pipeline (Metal on macOS, Vulkan on Linux).

```json
{
  "name": "Pure Native",
  "id": "com.example.native"
}
```

```csharp
public class MyWindow : IWindowPlugin
{
    public string WindowType => "main";
    public (float Width, float Height) DefaultSize => (1200, 800);

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
