# keystone-desktop

> **Early development / experimental.** macOS, Linux, and Windows supported.

A software foundation and framework enabaling you to build with HTML/TS/CSS and/or C#. Three processes — a C# host, a Bun runtime, and a WebKit renderer — form a triangle with full communication across every path.

Build your frontend in web tech (React, Svelte, vanilla JS), your backend services in TypeScript, and your main process in C# with direct access to the native platform — Metal on macOS, Vulkan on Linux, D3D12 on Windows. Or skip any layer you don't need.

https://discord.gg/d2GVrZJda7 - OFFICIAL DISCORD
---

## The architecture

Keystone runs as three independent OS processes:

```
           Browser (WebKit)
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

The main process. Owns the native run loop (AppKit on macOS, GTK4 on Linux, Win32 on Windows), creates native windows, drives the GPU/Skia rendering pipeline (Metal on macOS, Vulkan on Linux, D3D12 on Windows), loads plugins, and manages all child processes. This is where platform integration, rendering, and app lifecycle live.

If you don't need custom native code, you don't have to write any — built-in invoke handlers cover file dialogs, window management, shell integration, and path queries out of the box.

### Bun subprocess

The TypeScript runtime. Runs service modules, bundles and serves web components with HMR, and provides a WebSocket bridge between browser and backend. Bun's built-in bundler means no webpack/vite/esbuild configuration — components are bundled on demand.

### Web renderer

Embedded inside native windows — WKWebView on macOS, WebKitGTK on Linux, WebView2 on Windows. Full DOM, CSS, fetch, Web Workers — the complete browser platform. Use any frontend framework or none.

---

## What you can build

**Web-only** — TypeScript UI + Bun services, no C# code at all. Declare windows in `keystone.json`, implement them as web components. The built-in API surface covers file dialogs, window control, and shell integration.

**Hybrid** — Web UI for your interface, C# for platform integration, GPU rendering, or anything that needs native access. A single window can composite GPU/Skia rendering and WebKit content together.

**Native-only** — Pure C# with GPU/Skia rendering, Flex layout via Taffy, and no browser overhead. For visualization tools, real-time graphics, or performance-critical workloads.

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
import { invoke, subscribe } from "@keystone/sdk/bridge";

export function mount(root: HTMLElement, ctx: SlotContext) {
  // Full DOM, any framework, any structure
}

export function unmount(root: HTMLElement) {
  // Cleanup
}
```


## Design Decisions Worth Noting

**On performance vs. Electron:**
WebKit is significantly more efficient than Chromium — lower CPU overhead and better integration with the OS compositor. On macOS, Apple actively optimizes WebKit for the hardware. On Linux, WebKitGTK shares the same engine. On Windows, WebView2 uses the system Chromium (Edge), which is updated independently of the app. Keystone Desktop's web layer inherits all of that. The Metal/Vulkan/D3D12 + Skia native rendering path doesn't pay any browser overhead at all.

**Why C# and not Go, Rust, or Swift?**
.NET 10 has mature platform bindings on macOS, Linux, and Windows, first-class async, and a plugin system (AssemblyLoadContext) that's genuinely good for hot-reload. The alternative — Swift for the host — would mean no hot-reloadable plugins without significant additional infrastructure, and no Linux or Windows story. Rust would mean writing platform bindings from scratch. C# is the pragmatic choice for a framework that needs to be both native-capable and developer-friendly across platforms.

**Why Bun and not Node or Deno?**
Bun's built-in bundler (`Bun.build`) eliminates the need for a separate bundler process. The entire TypeScript → browser-ready JS pipeline runs inside the Bun process with no configuration. Bun's startup time is faster than Node, which matters for the subprocess restart recovery path.

**Why a separate Bun process?**
Bun didn't exist when Electron was designed. It's not a Node replacement in the sense of being a drop-in — it's a different thing: built-in bundler, fast startup, native SQLite, TypeScript without a build step. The subprocess model exists because that's how you get process isolation (a JS crash doesn't kill the C# host), but the more important point is that Bun makes the entire TypeScript layer self-contained in a way Node never was. You get a bundler, a package manager, a runtime, and a test runner in one binary that starts in milliseconds.

**Why WebKit slots instead of a single full-window WebView?**
WebKit in slot mode lets the GPU/Skia layer render anywhere in the window that doesn't have a web component. This enables hybrid windows: native custom chrome, native data visualization, or native-rendered toolbars composited with web UI panels — all in one window. A single full-window WebView would surrender the entire pixel budget to the browser.

**Why flexbox via Rust FFI instead of a C# layout engine?**
Taffy is a production-quality, well-tested flexbox/grid implementation. Writing a flexbox engine in C# would be a significant project; using Taffy's proven implementation via FFI is pragmatic. The FFI boundary is thin — layout input goes in, computed rects come out.### IPC — every direction, multiple ways

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
| `IWindowPlugin` | Native GPU-rendered windows (Metal/Vulkan/D3D12 + Skia) |
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

SkiaSharp + Metal/Vulkan/D3D12 for drawing. Taffy (Rust FFI) for Flex/Grid layout. Per-window GPU isolation. Retained scene graph with automatic diffing — no GPU re-upload unless something changes.

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
| Code signing & release trust | `build.py --package` handles signing, entitlements, signature verification, and optional notarization (`build.notarize`) |
| Bundling | Bun's built-in bundler — no webpack, vite, or esbuild config |
| Menu system | Declared in `keystone.json`, wired to actions automatically |
| Theme sync | System theme changes pushed to all windows in real-time |

---

## Stack

| Layer | macOS | Linux | Windows |
|-------|-------|-------|---------|
| Host / main process | C# / .NET 10, AppKit | C# / .NET 10, GTK4 | C# / .NET 10, Win32 |
| GPU rendering | SkiaSharp + Metal | SkiaSharp + Vulkan (Silk.NET) | SkiaSharp + D3D12 (Vortice) |
| Layout engine | Taffy (Rust FFI) | Taffy (Rust FFI) | Taffy (Rust FFI) |
| TypeScript runtime | Bun | Bun | Bun |
| Web renderer | WKWebView | WebKitGTK | WebView2 |

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

**macOS requirements:** macOS 15+ (Apple Silicon), .NET 10 SDK, Bun, Rust toolchain

**Linux requirements:** GTK4, WebKitGTK 4.1, Vulkan drivers, .NET 10 SDK, Bun, Rust toolchain

**Windows requirements:** Windows 10 1903+ (for WebView2 runtime), .NET 10 SDK, Bun, Rust toolchain

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
- [Window Chrome](docs/window-chrome.md)
- [Workers](docs/workers.md)
- [Build & Packaging](docs/build-and-packaging.md)

## License

MIT
