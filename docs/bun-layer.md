# Bun Layer

> Last updated: 2026-02-26

The Bun layer is the TypeScript runtime — a subprocess managed by C#. It discovers and runs service modules, bundles and serves web components with HMR, and provides a WebSocket bridge between browser and backend. Bun's built-in bundler means no webpack/vite/esbuild configuration.

This document covers three topics: **web components** (UI entry points served into WebKit), **services** (background TypeScript modules), and **workers** (additional Bun processes for parallelism and isolation).

---

## Web Components

A web component is a TypeScript/TSX entry point that Bun bundles and serves into a native window slot. The code runs in a full WebKit browser context (WKWebView on macOS, WebKitGTK on Linux) — full DOM, CSS, `fetch`, Web APIs, any ESM-compatible framework.

No required folder structure, no mandatory boilerplate, no framework opinion. The only thing the slot host needs from your entry file is two named exports:

```typescript
export function mount(root: HTMLElement, ctx: SlotContext) { ... }
export function unmount(root: HTMLElement) { ... }  // optional
```

### The Slot Contract

The slot host calls `mount(root, ctx)` when the component is inserted, and `unmount(root)` before it's removed or hot-swapped. If `unmount` is absent the host clears `root.innerHTML` automatically.

```typescript
type SlotContext = {
    slotKey: string;   // component name — "dashboard", "settings", etc.
    windowId: string;  // native window ID — scope channel subscriptions to this
};
```

`windowId` lets you receive pushes targeted at a specific window instance:

```typescript
subscribe(`window:${ctx.windowId}:refresh`, () => reload());
```

```csharp
// C# side
BunManager.Instance.Push($"window:{windowId}:refresh", new { });
```

### Registering Components

**Filename convention** — any `.ts` or `.tsx` file in `bun/web/` is auto-discovered. The filename (minus extension) becomes the component name.

```
bun/web/
├── dashboard.ts    → component "dashboard"
├── settings.tsx    → component "settings"
└── onboarding.ts   → component "onboarding"
```

**Explicit config** — if the entry file lives outside `bun/web/`, register it by name in `keystone.config.ts`:

```typescript
import { defineConfig } from "@keystone/sdk/config";

export default defineConfig({
    web: {
        components: {
            "dashboard": "src/dashboard.ts",
            "settings":  "src/settings/index.ts",
        },
    },
});
```

Both approaches work simultaneously. If the same name appears in both, the explicit entry wins.

### Internal Structure

The entry file is just a Bun bundle entrypoint. Internally it can be anything:

```
src/
├── dashboard.ts          ← entry (export mount, unmount)
├── dashboard/
│   ├── ui.ts             ← your internal structure
│   ├── state.ts
│   └── components/
│       ├── Sidebar.tsx
│       └── Header.tsx
└── shared/
    └── utils.ts
```

The slot host sees exactly two exports. How you arrived at them is your business.

### What Runs in the Browser

Everything available in the system WebKit (WKWebView on macOS, WebKitGTK on Linux):

| | Notes |
|---|---|
| HTML / DOM API | Build it in `mount()` |
| CSS (inline / in JS) | Works everywhere |
| `import "./styles.css"` | Bun resolves at bundle time |
| Standalone `.css` files | Served as static assets — inject a `<link>` tag |
| Static assets (SVG, PNG, fonts) | Served from `bun/` at their relative path |
| `fetch`, WebSocket, Web Workers | All available |
| React, Svelte, Vue, Solid | Any ESM framework bundled by Bun |

### The Slot System

C# manages where components appear using **slots** — named rectangular regions within a native window. A slot maps a component name to a position and size in the window.

The native rendering layer (GPU/Skia) draws everything underneath. When a slot is active, a WebKit view overlays that region with your component's output. The two layers composite via the platform compositor.

All slots in a single window share one WebKit view (and one WebKit content process). The host page manages slot lifecycle via `window.__addSlot`, `window.__moveSlot`, `window.__removeSlot`, and `window.__hotSwapSlot` — you never call these directly.

### Theme Tokens

The bridge applies CSS custom properties to `:root` on load and updates them live when the host pushes theme changes:

```typescript
root.style.background = "var(--ks-bg-surface)";
root.style.color = "var(--ks-text-primary)";
```

Full token list:

| Token | Purpose |
|-------|---------|
| `--ks-bg-base` | Deepest background |
| `--ks-bg-surface` | Standard panel/card background |
| `--ks-bg-elevated` | Elevated elements (modals, popovers) |
| `--ks-bg-chrome` | Window chrome / titlebar |
| `--ks-bg-hover` | Hover state |
| `--ks-bg-pressed` | Active/pressed state |
| `--ks-bg-button` | Default button fill |
| `--ks-accent` | Primary accent color |
| `--ks-accent-bright` | Bright accent (links, highlights) |
| `--ks-success` / `--ks-warning` / `--ks-danger` | Status colors |
| `--ks-text-primary` | Main body text |
| `--ks-text-secondary` | Subdued text |
| `--ks-text-muted` | Placeholder / metadata |
| `--ks-stroke` | Border color |
| `--ks-font` | System font stack |

### `defineComponent` Helper

`defineComponent` is a small convenience that auto-wires a cleanup return value from your mount callback to `unmount`. Entirely optional.

```typescript
import { defineComponent } from "@keystone/sdk/component";
import { subscribe } from "@keystone/sdk/bridge";

export const { mount, unmount } = defineComponent((root, ctx) => {
    root.style.cssText = "height: 100%; background: var(--ks-bg-surface); padding: 24px;";

    const heading = document.createElement("h1");
    root.appendChild(heading);

    const unsub = subscribe(`window:${ctx.windowId}:title`, (d) => {
        heading.textContent = d.value;
    });

    return () => unsub(); // returned function called on unmount automatically
});
```

### Framework Usage

Any ESM-compatible framework works. Bundle time is negligible with Bun's native bundler.

**React:**

```bash
cd bun && bun add react react-dom @types/react @types/react-dom
```

```tsx
import React, { useState, useEffect } from "react";
import { createRoot } from "react-dom/client";
import { subscribe, dialog } from "@keystone/sdk/bridge";
import type { SlotContext } from "@keystone/sdk/component";

function App({ ctx }: { ctx: SlotContext }) {
    const [files, setFiles] = useState<string[]>([]);

    useEffect(() => {
        const unsub = subscribe(`window:${ctx.windowId}:files`, setFiles);
        return () => unsub();
    }, [ctx.windowId]);

    return (
        <div style={{ background: "var(--ks-bg-surface)", height: "100vh", padding: 24 }}>
            <button onClick={() => dialog.openFile({ multiple: true })}>Open</button>
            {files.map(f => <div key={f}>{f}</div>)}
        </div>
    );
}

let reactRoot: ReturnType<typeof createRoot> | null = null;

export function mount(root: HTMLElement, ctx: SlotContext) {
    reactRoot = createRoot(root);
    reactRoot.render(<App ctx={ctx} />);
}

export function unmount() {
    reactRoot?.unmount();
    reactRoot = null;
}
```

**Svelte:**

```bash
cd bun && bun add svelte
```

```typescript
import App from "./App.svelte";
import type { SlotContext } from "@keystone/sdk/component";

let instance: any;

export function mount(root: HTMLElement, ctx: SlotContext) {
    instance = new App({ target: root, props: { ctx } });
}

export function unmount() {
    instance?.$destroy();
}
```

For Svelte/Vue file extensions add them to the watch list in `keystone.config.ts`:

```typescript
export default defineConfig({
    watch: { extensions: [".ts", ".tsx", ".svelte"] },
    web: {
        components: { "app": "src/app.ts" },
    },
});
```

### Multiple Components Per Window

A window can show more than one component. Each gets its own named slot. The C# `IWindowPlugin` (or `WebWindowPlugin` from `keystone.json` config) specifies which components and where.

For dynamic slot management — different components at runtime — use the C# layer via `FlexNode` with `WebComponent` nodes, which tell the renderer to create slots at specific layout positions.

### HMR (Hot Module Replacement)

HMR is automatic for both auto-discovered files and explicit `web.components` entries. When you save any registered entry file, the runtime:

1. Rebundles the changed file with `Bun.build`.
2. Sends an `__hmr__` message over the WebSocket to all connected windows.
3. The slot host calls `unmount`, clears the root, re-imports the new bundle, calls `mount`.

No page reload. The bridge WebSocket stays open. Subscriptions re-register in the new `mount` call as long as you set them up inside it.

### Static Assets

Files in the app's `bun/` directory are served statically by the HTTP server:

```typescript
const img = document.createElement("img");
img.src = "/web/assets/logo.png";   // served from bun/web/assets/logo.png
```

### Component Lifecycle

```
C# spawns window
    ↓
WebKit view created, loads /__host__
    ↓
host page ready (window.__ready = true)
    ↓
C# calls window.__addSlot("app", "/web/app.js", x, y, w, h, windowId)
    ↓
host page imports /web/app.js
    ↓
app.ts mount(root, { slotKey: "app", windowId }) called
    ↓
[component runs, bridge connects, data flows]
    ↓
[file saved]
    ↓
Bun rebundles app.ts → app.js
Bun sends __hmr__ over WebSocket
    ↓
host page calls app.ts unmount(root)
host page imports /web/app.js?t=<timestamp>
host page calls new app.ts mount(root, ctx)
    ↓
[window closes or app quits]
    ↓
C# calls window.__removeSlot("app")
host page calls unmount(root)
```

---

## Services

Services are TypeScript modules that run in a Bun subprocess. They handle background work — data fetching, file watching, database access, scheduled tasks, inter-process coordination — and expose it to both the web layer and C# via a clean query/push API.

Every file in `bun/services/` is auto-discovered and started on launch. No registration, no imports into a central file. Drop a module in and it runs.

### The Service Contract

A service module exports specific functions that the runtime calls at lifecycle points. You can write them manually or use the builder SDK.

```typescript
// bun/services/my-service.ts — manual style
import type { ServiceContext } from "@keystone/sdk/service";

export async function start(ctx: ServiceContext) {
    // Called once when the service loads (and after hot-reload)
}

export function query(args: any) {
    // Called when the web layer or C# calls query("my-service", args)
    return { ok: true };
}

export function stop() {
    // Called before hot-reload or shutdown — clean up timers, connections, etc.
}

export function health(): { ok: boolean } {
    // Called by the runtime health monitor every 30s
    return { ok: true };
}

export function onAction(action: string) {
    // Called when an action is dispatched from web or C#
}
```

### The Builder API (recommended)

`defineService` wraps the lifecycle into a composable builder. It also wires the SQLite-backed store and service handle automatically.

```typescript
import { defineService } from "@keystone/sdk/service";

export default defineService("weather")
    .query(async (args: { city: string }, svc) => {
        const cached = svc.store.get<any>(args.city);
        if (cached) return cached;

        const data = await fetch(`https://wttr.in/${args.city}?format=j1`).then(r => r.json());
        svc.store.set(args.city, data);
        return data;
    })
    .every(60_000, async (svc) => {
        for (const city of svc.store.keys()) {
            const data = await fetch(`https://wttr.in/${city}?format=j1`).then(r => r.json());
            svc.store.set(city, data);
            svc.push("weather:update", { city, data });
        }
    })
    .health((svc) => ({ ok: svc.store.keys().length >= 0 }))
    .onStop((svc) => svc.store.clear())
    .build();
```

#### Builder Methods

| Method | When it runs | Notes |
|--------|-------------|-------|
| `.query(fn)` | On `query("name", args)` call | Can be async. Return value sent to caller. |
| `.every(ms, fn)` | On a recurring interval | Timers auto-cancelled on stop. |
| `.onAction(fn)` | On action dispatch | Receives the full action string. |
| `.health(fn)` | Every 30s (configurable) | Return `{ ok: false }` to trigger auto-restart. |
| `.onStop(fn)` | Before hot-reload or shutdown | Clean up connections, cancel pending work. |
| `.network(opts)` | At module load (before start) | Declare endpoint requirements — see [Network Declarations](#network-declarations). |
| `.build(init?)` | At startup | Optional `init` runs after start. Returns the module. |

### The Service Handle

Every handler receives a `ServiceHandle` with scoped utilities:

```typescript
type ServiceHandle = {
    ctx: ServiceContext;         // raw runtime context
    store: StoreHandle;          // namespaced SQLite key-value store
    call: (service, args?) => Promise<any>;   // query another service
    push: (channel, data) => void;            // broadcast to all subscribers
    fetch: typeof globalThis.fetch;           // policy-enforced fetch (or unrestricted if declared)
};
```

#### `push` — broadcast data

`push` broadcasts to every subscriber on a named channel — both web components and C#. It also signals the C# host via stdout so it can forward to its own listeners.

```typescript
svc.push("metrics:cpu", { percent: 72.4, cores: 8 });
```

Web components subscribe:
```typescript
subscribe("metrics:cpu", (data) => {
    gauge.textContent = `${data.percent.toFixed(1)}%`;
});
```

C# receives via `OnServicePush`:
```csharp
BunManager.Instance.OnServicePush += (channel, json) => {
    if (channel == "metrics:cpu") { /* parse json */ }
};
```

#### `call` — inter-service queries

Services can query each other without going through the web layer or C#:

```typescript
const location = await svc.call("location", { ip: req.ip });
const forecast = await fetchForecast(location.city);
```

#### `store` — persistent key-value store

Each service gets a namespaced slice of a shared SQLite database at `bun/data/services.db`. Values survive hot-reloads and app restarts.

```typescript
svc.store.set("last-sync", Date.now());
svc.store.get<number>("last-sync");   // → timestamp
svc.store.del("last-sync");
svc.store.keys();                     // → string[]
svc.store.clear();                    // wipe namespace
```

Values are JSON-serialized — any JSON-compatible type works.

### Querying Services

**From TypeScript (web layer):**

```typescript
import { query } from "@keystone/sdk/bridge";

const weather = await query("weather", { city: "San Francisco" });

const ks = keystone();
const files = await ks.query("file-scanner", { dir: "/home/user/Documents" });
```

**From C#:**

```csharp
var json = await context.Bun.Query("weather", new { city = "San Francisco" });
using var doc = JsonDocument.Parse(json!);
var temp = doc.RootElement.GetProperty("temp_C").GetString();
```

### Service Directory Layout

```
bun/services/
├── metrics.ts          # single-file service — name is "metrics"
├── file-scanner.ts
└── database/           # directory service — name is "database"
    ├── index.ts        # entry point
    └── schema.ts       # internal module (not a service)
```

Both single files and directories work. Directory services let you split complex services across multiple files without exposing internal modules to the runtime.

### Hot Reload

When you save a service file, the runtime:

1. Calls the existing service's `stop()`.
2. Clears the require cache for that service file (or directory).
3. Re-requires the module and calls `start(ctx)` again.

State in variables is lost on reload — but anything in `svc.store` (SQLite) survives, because the database persists across process lifetimes.

Hot reload is per-service. Changing `database/schema.ts` reloads the `database` service. Changing a shared module in `lib/` triggers a full reload of all services.

### Action Handlers

Services can respond to action strings dispatched from the web layer or C#:

```typescript
defineService("file-manager")
    .onAction((action, svc) => {
        if (action === "file:refresh") {
            svc.push("files:list", scanFiles());
        }
    })
    .build();
```

Actions are dispatched to **all** registered action handlers across all services — there's no routing by service name. Use prefixes to namespace your actions.

### Health Monitoring

The runtime polls `.health()` every 30 seconds (configurable). If a service returns `{ ok: false }`, the runtime calls `stop()` then `start()` to restart it.

```typescript
defineService("db-connection")
    .health((svc) => {
        const connected = svc.store.get<boolean>("connected") ?? false;
        return { ok: connected, lastCheck: Date.now() };
    })
    .build(async (svc) => {
        try {
            await connectToDatabase();
            svc.store.set("connected", true);
        } catch {
            svc.store.set("connected", false);
        }
    });
```

### Custom Web Messages

Services can register handlers for arbitrary messages sent from the web layer via `keystone().send(type, data)`:

```typescript
// Service
export async function start(ctx: ServiceContext) {
    ctx.onWebMessage("chat:send", async (data, ws) => {
        const saved = await saveChatMessage(data);
        ctx.push("chat:messages", saved);
    });
}
```

```typescript
// Web layer
const ks = keystone();
ks.send("chat:send", { text: "Hello", roomId: "general" });
```

### Built-in Services

The runtime registers a `paths` service automatically — always available, never overwritten by app services.

```typescript
const paths = await query("paths");
// {
//   appRoot: "/path/to/app/bun",
//   userData: "~/.keystone/<app-id>",
//   documents: "~/Documents",
//   downloads: "~/Downloads",
//   desktop: "~/Desktop",
//   temp: "/tmp"
// }
```

### Security — Action Allowlisting

The web layer action policy is controlled by `security.mode` in `keystone.config.ts`.
Use `"allowlist"` (or `"auto"` in packaged mode) to restrict which actions are permitted:

```typescript
export default defineConfig({
    security: {
        mode: "allowlist",
        allowedActions: [
            "window:minimize",
            "window:maximize",
            "window:close",
            "app:quit",
            "myapp:*",          // wildcard — all actions prefixed "myapp:"
        ],
    },
});
```

Actions not in the allowlist are logged and dropped before they reach C# or service action handlers. Invoke calls (`invoke()` via `WKScriptMessageHandler`) are not subject to this allowlist — they bypass Bun entirely.

### Network Declarations

The app-level network allow-list (defined in `keystone.config.json` → `security.network`) controls which hostnames Bun services can `fetch()`. In allowlist mode, any fetch to an unlisted host throws.

Services that need external access declare their endpoints with `.network()`:

```typescript
export default defineService("news")
  .network({ endpoints: ["reddit.com", "finviz.com", "*.rssfeeds.com"] })
  .every(300_000, async (svc) => {
    const data = await fetch("https://reddit.com/r/wallstreetbets.json").then(r => r.json());
    svc.push("news:reddit", data);
  })
  .build();
```

Declared endpoints are merged into the global allow-list at service discovery time — they become available to all services, not just the declaring one.

**Unrestricted services** — for services that truly need to talk to any host (e.g. a proxy or debug tool):

```typescript
export default defineService("debug-proxy")
  .network({ unrestricted: true })
  .build(async (svc) => {
    // svc.fetch is the original unpatched fetch — bypasses the allow-list
    const res = await svc.fetch("https://any-host.example.com/test");
  });
```

**C# plugins** can also declare endpoints by implementing `INetworkDeclarer`:

```csharp
public class MyService : IServicePlugin, INetworkDeclarer
{
    public string ServiceName => "my-service";
    public IEnumerable<string> NetworkEndpoints => new[] { "api.external.com" };

    public void Initialize() { /* ... */ }
    public void Shutdown() { }
}
```

### Compiled Distribution

When you package your app with `python3 build.py --package`, services are compiled directly into the executable. No `.ts` source files ship in the bundle.

```
Development:  host.ts → readdirSync("services/") → require() each .ts file
Distribution: compiled exe → __KEYSTONE_COMPILED_SERVICES__ global → all services already loaded
```

- **Source code is protected** — service logic is inside the compiled binary, not inspectable
- **No filesystem dependency** — the exe runs without `node_modules/`, `services/`, or any `.ts` files
- **Same API** — services work identically in both modes; no code changes needed

The `svc.store` (SQLite) database still lives on disk at `data/services.db` — the store is initialized lazily on first access so it works correctly inside compiled executables where `import.meta.dir` resolves to Bun's read-only virtual filesystem.

---

## App Host (`bun/host.ts`)

`bun/host.ts` is an optional entry point that gives you lifecycle hooks into the Bun process itself. The place for setup that doesn't belong in a service module: direct service registration, global invoke handlers, process-wide initialization, and shutdown cleanup.

```typescript
import { defineHost } from "@keystone/sdk/host";

export default defineHost({
  async onBeforeStart(ctx) {
    // Fires before service discovery.
    await ctx.registerService("db", myDbModule);
    ctx.registerInvokeHandler("app:getConfig", async () => loadConfig());
  },

  async onReady(ctx) {
    // Fires after all services are started and the HTTP server is live.
    ctx.push("app:status", { ready: true });
  },

  async onShutdown(ctx) {
    // Fires before service stop() calls. Last chance to flush state.
    await flushPendingWrites();
  },

  onAction(action, ctx) {
    // Global action handler — receives every action from C# or web.
    if (action === "app:refresh") ctx.push("app:refresh", {});
  },
});
```

### Lifecycle Phases

| Hook | When | Use for |
|------|------|---------|
| `onBeforeStart` | Before service discovery | Direct service registration, global handler setup |
| `onReady` | After all services started, HTTP server live | Initial pushes, process-wide background tasks |
| `onShutdown` | Before service `stop()` calls | Flushing writes, external connection teardown |
| `onAction` | Every action from C# or web | Global action routing not tied to one service |

### `HostContext`

```typescript
type HostContext = {
  registerService: (name: string, mod: ServiceModule) => Promise<void>;
  registerInvokeHandler: (channel: string, handler: (args: any) => any) => void;
  onWebMessage: (type: string, handler: (data: any) => void) => void;
  push: (channel: string, data: any) => void;
  readonly services: ReadonlyMap<string, ServiceModule>;
  readonly config: ResolvedConfig;
};
```

### Registering Services Directly

`ctx.registerService` lets you register a service module inline rather than dropping a file in `bun/services/`:

```typescript
export default defineHost({
  async onBeforeStart(ctx) {
    const db = await openDatabase();

    await ctx.registerService("db", {
      name: "db",
      async start() {},
      query: (args) => db.query(args.sql),
      stop: () => db.close(),
    });
  },
});
```

Services registered in `onBeforeStart` are available to all discovered services that call `ctx.call("db", ...)` in their own `start()`.

### Global Invoke Handlers

```typescript
export default defineHost({
  onBeforeStart(ctx) {
    ctx.registerInvokeHandler("app:getConfig", async () => {
      return loadConfigFromDisk();
    });
  },
});
```

Browser calls this via `invokeBun("app:getConfig")`.

`bun/host.ts` changes require a process restart — the file is not hot-reloaded.

---

## Workers

Workers are additional Bun subprocesses managed by the C# host. They solve two problems:

1. **Parallelism** — CPU-bound TypeScript work blocks the main Bun event loop. Workers run in their own process with their own event loop.
2. **Extension isolation** — third-party TypeScript code runs in a sandboxed worker with restricted push channels.

Workers use the same service contract as the main Bun process — `start(ctx)`, `query()`, `stop()`, `health()`. A service running in a worker is identical to a service running in main Bun, except for what `ctx` can do.

### Configuration

Workers are declared in `keystone.json`:

```jsonc
{
  "workers": [
    {
      "name": "data-processor",
      "servicesDir": "workers/data-processor",
      "autoStart": true,
      "browserAccess": false
    },
    {
      "name": "extension-host",
      "servicesDir": "extensions",
      "autoStart": true,
      "browserAccess": false,
      "isExtensionHost": true,
      "allowedChannels": ["ext:"],
      "maxRestarts": 3
    }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `name` | required | Unique identifier for this worker |
| `servicesDir` | required | Services directory, relative to the app's `bun/` root |
| `autoStart` | `true` | Start automatically when the main Bun process is ready |
| `browserAccess` | `false` | Start a WebSocket server for direct browser/worker connections |
| `isExtensionHost` | `false` | Restrict `ctx.push()` to allowed channels only |
| `allowedChannels` | `null` | Channel prefixes the extension host may push to |
| `maxRestarts` | `5` | Maximum restart attempts before giving up |
| `baseBackoffMs` | `1000` | First restart delay; doubles each attempt (capped at 30s) |

### Directory Layout

```
bun/
├── host.ts                          # Main Bun (unchanged)
├── worker-host.ts                   # Worker entry point (framework-provided)
├── services/                        # Main host services
├── workers/
│   └── data-processor/
│       ├── heavy-compute.ts         # Worker service
│       └── aggregator.ts
└── extensions/                      # Extension host services
    ├── user-extension/
    │   └── index.ts
    └── custom-alerts.ts
```

### Communication

Workers have two communication paths:

**Path 1: C# Relay (always available)**

All workers communicate through C# via stdin/stdout NDJSON — the same protocol as main Bun.

```typescript
// Worker → Worker
ctx.relay("data-processor", "task:start", { job: "backfill", range: "2024-01" });
```

Flow: Worker writes relay JSON to stdout → C# routes through `BunWorkerManager.Route()` → C# writes to target worker stdin → target dispatches locally.

Worker → Main Bun: same flow with `target: "main"`.
Main Bun → Worker: `ctx.relay("data-processor", "channel", data)`.

**Path 2: Direct WebSocket (when target has `browserAccess: true`)**

When a worker has `browserAccess: true`, it runs a WebSocket server on `127.0.0.1` (random port). Other workers and main Bun can connect directly — bypassing C# entirely. This is the high-throughput path.

```typescript
export async function start(ctx: ServiceContext) {
    const dp = ctx.workers.connect("data-processor");

    const result = await dp.query("heavy-compute", { data: bigPayload });

    dp.subscribe("ticks:realtime", (tick) => {
        processTick(tick);
    });

    dp.send("task:enqueue", { job: "cleanup" });
}
```

**`ctx.workers` API:**

| Method | Description |
|--------|-------------|
| `ctx.workers.connect(name)` | Open a WebSocket to a worker. Returns a `WorkerConnection`. Throws if no WebSocket. |
| `ctx.workers.ports()` | Returns the port map: `Record<string, number>` |

**`WorkerConnection` API:**

| Method | Description |
|--------|-------------|
| `query(service, args?)` | Query a service on the connected worker. Returns a promise. |
| `subscribe(channel, cb)` | Subscribe to a push channel. Returns an unsubscribe function. |
| `send(type, data?)` | Fire-and-forget typed message. |
| `close()` | Close the WebSocket. |

**When to use which:**

| Scenario | Use |
|----------|-----|
| One-off command to another worker | `ctx.relay()` — simple, always works |
| Real-time data streaming between workers | `ctx.workers.connect()` — direct, no C# hop |
| Worker without `browserAccess` | `ctx.relay()` — only option |
| Cross-worker query with reply | `ctx.workers.connect().query()` |

### C# API

**Pre-configured workers** (declared in `keystone.json` with `autoStart: true`):

```csharp
var processor = context.Workers["data-processor"];
var result = await processor.Query("heavy-compute", new { data = payload });
processor.Push("config:update", new { threshold = 0.5 });
```

**Dynamic workers** (spawned at runtime):

```csharp
var worker = context.Workers.Spawn(
    new BunWorkerConfig
    {
        Name = "temp-cruncher",
        ServicesDir = "workers/cruncher",
        BrowserAccess = false,
        MaxRestarts = 2,
    },
    workerHostPath,
    appRoot
);

var result = await worker.Query("crunch", new { input = data });
context.Workers.Stop("temp-cruncher");
```

**`BunWorkerManager` API:**

| Method/Property | Description |
|-----------------|-------------|
| `Instance` | Singleton accessor |
| `this[string name]` | Get worker by name, or `null` |
| `All` | `IReadOnlyDictionary<string, BunWorker>` of all workers |
| `Spawn(config, hostPath, appRoot)` | Start a new worker. Returns the `BunWorker`. |
| `Stop(name)` | Stop a specific worker |
| `StopAll()` | Stop all workers (called during shutdown) |
| `Route(from, target, channel, data)` | Forward a relay message between workers |
| `BroadcastPorts()` | Send port map to all workers and main Bun |

**`BunWorker` API:**

| Method/Property | Description |
|-----------------|-------------|
| `Name` | Worker name |
| `IsRunning` | `true` if ready and process alive |
| `Port` | WebSocket port (null if no browserAccess) |
| `Services` | List of discovered service names |
| `Query(service, args?)` | Query a service on this worker |
| `Push(channel, data)` | Push data to this worker |
| `Stop()` | Graceful shutdown |

### Extension Host Mode

When `isExtensionHost: true`, the worker runs with restrictions:

- **Channel filtering**: `ctx.push()` only works for channels matching `allowedChannels` prefixes. Pushes to other channels are silently dropped.
- **`eval` disabled**: The `eval` stdin command returns an error instead of executing.
- Extensions are just normal services in the configured directory — same lifecycle, same contract.

### Crash Recovery

Each worker has independent crash recovery with exponential backoff:

- Restart delay: `baseBackoffMs * 2^(attempt - 1)`, capped at 30 seconds
- After `maxRestarts` consecutive failures, the worker stays down
- A successful restart resets the counter

```csharp
var worker = context.Workers["data-processor"];
worker.OnCrash += exitCode => Log.Warn($"Worker crashed: {exitCode}");
worker.OnRestart += attempt => Log.Info($"Worker recovered (attempt {attempt})");
```

### Worker Service Store

Worker services get a namespaced SQLite store, same as main Bun services. The store uses the same `data/services.db` database — namespaces are per-service-name, not per-worker, so a service called "metrics" in a worker shares the namespace with a service called "metrics" in main Bun. Name your worker services distinctly to avoid collisions.

### Compiled Distribution

When packaged, worker services are compiled into a single executable alongside the worker host runtime. One compiled exe serves all workers — the `KEYSTONE_WORKER_NAME` environment variable determines which subset of services to load.

No `.ts` source files, `node_modules/`, or worker service directories ship in the bundle.
