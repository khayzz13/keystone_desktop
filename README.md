# keystone-desktop

> **Early development / experimental.** macOS only for now, looking for Linux/Win contributors for DX and Vulkan integrations.

A native desktop application framework. Three processes — a C# host, a Bun runtime, and a WebKit renderer — form a triangle with full communication across every path.

Build your frontend in web tech (React, Svelte, vanilla JS), your backend services in TypeScript, and your main process in C# with direct access to Metal, AppKit, and the full macOS platform. Or skip any layer you don't need.

https://discord.gg/d2GVrZJda7 - OFFICIAL DISCORD
---

## The architecture

Keystone runs as three independent OS processes:

```
           Browser (WKWebView)
          ╱                     ╲
     invoke()              invokeBun()
     fetch("/api/…")       query() / send()
     push                  subscribe()
          ╲                     ╱
    C#  Host ◄── stdin/stdout ──► Bun
    (.NET 10)     (NDJSON)      (TypeScript)
```

Every pair talks in both directions. A crash in one process doesn't take down the others — WebKit auto-reloads, Bun auto-restarts with backoff, and C# owns the lifecycle of everything.

### C# host process

The main process. Owns the AppKit run loop, creates native windows, drives the Metal/Skia GPU pipeline, loads plugins, and manages all child processes. This is where platform integration, rendering, and app lifecycle live.

If you don't need custom native code, you don't have to write any — built-in invoke handlers cover file dialogs, window management, shell integration, and path queries out of the box.

### Bun subprocess

The TypeScript runtime. Runs service modules, bundles and serves web components with HMR, and provides a WebSocket bridge between browser and backend. Bun's built-in bundler means no webpack/vite/esbuild configuration — components are bundled on demand.

### WebKit renderer

WKWebView slots embedded inside native windows. Full DOM, CSS, fetch, Web Workers — the complete browser platform, powered by the system WebKit (not a bundled Chromium). Use any frontend framework or none.

---

## What you can build

**Web-only** — TypeScript UI + Bun services, no C# code at all. Declare windows in `keystone.json`, implement them as web components. The built-in API surface covers file dialogs, window control, and shell integration.

**Hybrid** — Web UI for your interface, C# for platform integration, GPU rendering, or anything that needs native access. A single window can composite Metal/Skia rendering and WKWebView content together.

**Native-only** — Pure C# with Metal/Skia rendering, Flex layout via Taffy, and no browser overhead. For visualization tools, real-time graphics, or performance-critical workloads.

All three modes compose freely. A single app can have web windows, native windows, and hybrid windows side by side.

---

## How it works

### Declarative config

A `keystone.json` manifest declares your app. Windows, plugins, Bun config, process recovery — all in one file:

```jsonc
{
  "name": "My App",
  "id": "com.example.myapp",
  "version": "1.0.0",
  "bun": { "root": "bun" },
  "windows": [
    { "component": "app", "title": "My App", "width": 1024, "height": 700, "spawn": true }
  ]
}
```

### Web components

A web component is a TypeScript file that exports `mount` and optionally `unmount`. Bun discovers, bundles, and serves it automatically:

```typescript
import { invoke, subscribe } from "@keystone-desktop/sdk/bridge";

export function mount(root: HTMLElement, ctx: SlotContext) {
  // Full DOM, any framework, any structure
}

export function unmount(root: HTMLElement) {
  // Cleanup
}
```

### IPC — every direction, multiple ways

Three processes, six directions, and every direction has multiple communication surfaces. The full framework exposes **35+ distinct IPC pathways** — request/reply, fire-and-forget, pub/sub, streaming, relays, and direct WebSocket connections between workers.

| Path | Surfaces |
|------|----------|
| Browser → C# | `invoke()`, `fetch("/api/...")`, `action()` |
| C# → Browser | `push`, JS eval, streaming response chunks |
| Browser → Bun | `invokeBun()`, `query()`, `send()`, `subscribe()`, `publish()` |
| Bun → Browser | channel push, action broadcast, HMR |
| C# → Bun | query, action, push, eval, health, shutdown (NDJSON stdin) |
| Bun → C# | result, service_push, action relay, ready (NDJSON stdout) |
| Bun → Bun | inter-service `call()`, worker relay, direct WebSocket |
| Window → Window | action broadcast, targeted channel push |

```typescript
// Browser → C# (direct, fastest path — no Bun round-trip)
const files = await invoke("dialog:openFile", { multiple: true });

// Browser → Bun service
const notes = await invokeBun("notes:getAll");

// Live data (Bun or C# → Browser)
subscribe("metrics:cpu", (data) => updateUI(data));

// HTTP-shaped requests routed to C# (no real HTTP server)
const res = await fetch("/api/users");

// Fire-and-forget to any Bun service
send("analytics:track", { event: "click" });

// Service-to-service (same Bun process, direct call)
const user = await ctx.call("auth", { token });
```

### Hot-reloadable plugins

C# plugins are DLLs dropped into `dylib/`. The runtime auto-discovers and loads them. Overwrite a DLL and it hot-reloads — old code unloads, new code loads, state preserved. You can also load native libraries (Rust, C, C++) via `dylib/native/` — anything accessible through `[DllImport]` or P/Invoke.

Four built-in plugin interfaces, but the system is open — implement your own plugin types on top of the framework.

| Interface | Purpose |
|-----------|---------|
| `IWindowPlugin` | Native GPU-rendered windows (Metal/Skia) |
| `IServicePlugin` | Background services |
| `ILibraryPlugin` | Shared code used by other plugins |
| `ILogicPlugin` | Render/compute logic, GPU pipelines |

### Native rendering

For windows that need GPU rendering without a browser:

```csharp
public override SceneNode? BuildScene(FrameState state)
{
    return Flex.Column(
        Flex.Text($"CPU: {_cpu:F1}%", fontSize: 28),
        Flex.Row(Flex.Spacer(), Flex.Button("Reset", onClick: ResetCounters))
    );
}
```

SkiaSharp + Metal for drawing. Taffy (Rust FFI) for Flex/Grid layout. Per-window GPU isolation. Retained scene graph with automatic diffing — no GPU re-upload unless something changes.

### Batteries included

Desktop frameworks typically require a constellation of packages to cover basic app needs. Keystone ships these as first-class built-in APIs — no extra dependencies, no glue code:

| Capability | How it works |
|------------|-------------|
| File dialogs | `invoke("dialog:openFile")`, `invoke("dialog:saveFile")` |
| Window management | `invoke("window:open")`, `invoke("window:close")`, `invoke("window:setTitle")` |
| Shell integration | `invoke("shell:openPath")`, `invoke("shell:openExternal")` |
| App info & paths | `invoke("app:getName")`, `invoke("app:getPath")` |
| HTTP routing | `fetch("/api/...")` intercepted and routed to C# handlers |
| Live data streaming | `subscribe(channel)` — pub/sub across all three processes |
| Hot module replacement | Automatic — save a file, Bun rebundles, slot reloads |
| Process crash recovery | Auto-restart with exponential backoff, configurable in `keystone.json` |
| Code signing & notarization | `build.py --package` handles signing, entitlements, and notarization |
| Bundling | Bun's built-in bundler — no webpack, vite, or esbuild config |
| Menu system | Declared in `keystone.json`, wired to actions automatically |
| Theme sync | System theme changes pushed to all windows in real-time |

---

## Stack

| Layer | Technology |
|-------|-----------|
| Host / main process | C# / .NET 10, AppKit |
| GPU rendering | SkiaSharp + Metal |
| Layout engine | Taffy (Rust FFI) — Flexbox and CSS Grid |
| TypeScript runtime | Bun |
| Web renderer | WKWebView (system WebKit) |

---

## Who this is for

Developers building desktop applications who want native performance and platform access without giving up web tools for UI. Teams with TypeScript/React investment who need more than a browser can give them — GPU rendering, real native windows, platform APIs, process isolation.

---

## Getting started

```bash
# Create a new app from the template
python3 tools/create-app.py my-app
cd my-app
python3 build.py --run
```

## Building from source

**Requirements:** macOS 15+ (Apple Silicon), .NET 10 SDK, Bun, Rust toolchain

```bash
git clone https://github.com/khayzz13/keystone_desktop.git
cd keystone-desktop
python3 build.py
```

## Documentation

- [Getting Started](docs/getting-started.md)
- [Process Model](docs/process-model.md)
- [Web Components](docs/web-components.md)
- [Bun Services](docs/bun-services.md)
- [HTTP Router](docs/http-router.md)
- [Plugin System](docs/plugin-system.md)
- [C# App Layer](docs/csharp-app-layer.md)
- [Native Window API](docs/native-api.md)
- [Configuration Reference](docs/configuration.md)
- [Build & Packaging](docs/build-and-packaging.md)

## License

MIT
