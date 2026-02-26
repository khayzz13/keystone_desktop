# Keystone Desktop — Build Guide

Keystone is a native desktop framework: C# host process owns the platform run loop and native windows; a Bun subprocess runs TypeScript services and bundles/serves web UI; WebKit content processes render HTML/CSS/JS inside native window slots. Three real OS processes with full cross-communication.

## Process Architecture

```
C# Host (AppKit+Metal on macOS / GTK4+Vulkan on Linux)
  ↕ stdin/stdout NDJSON
Bun Subprocess (services + HTTP server + HMR)
  ↕ WebSocket
WebKit Content Process (per window, OS-managed)
```

**Communication triangle — every pair talks both ways:**
- Browser → C#: `invoke()` via WKScriptMessageHandler (zero Bun round-trip, direct)
- C# → Browser: `BunManager.Instance.Push(channel, data)` over WebSocket
- Browser → Bun: `query()` / `invokeBun()` / `subscribe()` over WebSocket
- Bun → Browser: `svc.push(channel, data)` / `ctx.push(channel, data)` over WebSocket
- C# → Bun: `context.Bun.Query()` / `BunManager.Instance.SendAction()` over stdin NDJSON
- Bun → C#: `ctx.action()` / query replies over stdout NDJSON

---

## Project Structure

```
my-app/
├── keystone.json              # C# host config: windows, plugins, recovery
├── bun/
│   ├── host.ts                # Bun lifecycle hooks (optional — onReady, onShutdown, etc.)
│   ├── keystone.config.ts     # Bun config: services, HMR, security
│   ├── web/
│   │   └── app.ts             # Web component (export mount, unmount)
│   └── services/
│       └── data.ts            # Bun service (defineService or manual exports)
├── app/                       # Optional C# app layer
│   └── AppCore.cs             # ICorePlugin implementation
└── dylib/                     # Hot-reloadable plugin DLLs output here
    ├── AppCore.dll             # ICorePlugin (appAssembly)
    ├── MyWindowPlugin.dll      # IWindowPlugin
    ├── MyService.dll           # IServicePlugin
    └── native/
        └── libmynative.dylib   # Rust/C native libraries
```

---

## Configuration

### `keystone.json`

```jsonc
{
  "name": "My App",
  "id": "com.example.myapp",
  "version": "1.0.0",
  "windows": [
    {
      "component": "app",        // maps to bun/web/app.ts or IWindowPlugin.WindowType
      "title": "My App",
      "width": 1200, "height": 800,
      "spawn": true,             // open on launch (default: true)
      "titleBarStyle": "hidden", // "hidden" | "toolkit" | "toolkit-native" | "none"
      "floating": false,         // always-on-top
      "renderless": false,       // skip GPU surface — use for web-only windows (saves 30–60MB RAM)
      "headless": false          // invisible WebKit window, never shown (implies renderless)
    }
  ],
  "bun": { "enabled": true, "root": "bun" },        // omit to go pure-native
  "appAssembly": "dylib/MyApp.Core.dll",             // ICorePlugin DLL — loaded once, not hot-reloaded
  "plugins": {
    "enabled": true,
    "dir": "dylib",              // directory for hot-reloadable IWindowPlugin/IServicePlugin/etc.
    "userDir": "$APP_SUPPORT/plugins",
    "extensionDir": "$APP_SUPPORT/extensions",
    "hotReload": true,
    "debounceMs": 200,
    "allowExternalSignatures": false
  },
  "processRecovery": {
    "bunAutoRestart": true, "bunMaxRestarts": 5,
    "bunRestartBaseDelayMs": 500, "bunRestartMaxDelayMs": 30000,
    "webViewAutoReload": true, "webViewReloadDelayMs": 200
  }
}
```

**`titleBarStyle`:** `"hidden"` = native traffic lights, web content full-bleed. `"toolkit"` = borderless, GPU title bar with close/min/float/tabs. `"toolkit-native"` = native traffic lights + GPU title bar with tabs/float (no close/min buttons). `"none"` = fully frameless.
**`renderless: true`** — no Metal/Vulkan surface. All title bar styles are valid; GPU title bar components won't render without a GPU surface.
**`headless: true`** — invisible window, never shown. Forces `renderless: true`. For background JS, PDF capture, test harnesses.

### `bun/keystone.config.ts`

```typescript
import { defineConfig } from "@keystone/sdk/config";
export default defineConfig({
  services: { dir: "services", hotReload: true },
  web: {
    dir: "web",
    autoBundle: true, hotReload: true,
    components: { "settings": "src/settings/index.tsx" },  // explicit name → path mappings
  },
  http: { enabled: true, hostname: "127.0.0.1" },  // port always OS-assigned
  watch: { extensions: [".ts", ".tsx", ".svelte"], debounceMs: 150 },
  health: { enabled: true, intervalMs: 30_000 },
  security: {
    mode: "auto",  // "open" | "allowlist" | "auto" (open in dev, allowlist when packaged)
    allowedActions: ["window:minimize", "window:close", "myapp:*"],
    allowEval: "auto",
  },
});
```

---

## Dev Setup & Build System

`@keystone/sdk` and `keystone-desktop` are **not npm packages** — they are vendored from the engine by the build tooling.

### How `@keystone/sdk` gets into `node_modules`

`tools/cli.py`'s `vendor_engine_bun()` runs as part of every `build` or `run` step:

```
engine/bun/sdk/           →  app/bun/node_modules/@keystone/sdk/
engine/bun/               →  app/bun/node_modules/keystone-desktop/
engine/bun/lib/           →  app/bun/node_modules/@keystone/lib/
```

This is a **file copy** (`shutil.copytree`), not a symlink or runtime hook. Bun then resolves `@keystone/sdk/*` from `node_modules` normally.

**`keystone.build.yaml`** controls engine location — no auto-download, vendor only:
```yaml
app_directory: "."               # path to app root (relative to build.py)
framework_directory: "../keystone"  # path to engine source or distribution
```
`build.py` reads these keys, resolves the engine path, and delegates to `engine/tools/cli.py`.

**To set up a dev environment:**
```bash
python3 build.py        # vendors SDK, installs bun deps, compiles C# if present
python3 build.py --run  # same + launches the app
```
Re-run `build.py` after engine changes to re-vendor the SDK.

### tsconfig.json — IDE paths

`bun/tsconfig.json` extends `{{ENGINE_REL}}/bun/tsconfig.base.json` and declares `@keystone/sdk/*` paths pointing into the engine source. `{{ENGINE_REL}}` is a scaffolding placeholder — replace it with the actual relative path to the engine for IDE type-checking. Example:

```json
{
  "extends": "../../keystone/bun/tsconfig.base.json",
  "compilerOptions": {
    "paths": { "@keystone/sdk/*": ["../../keystone/bun/sdk/*"] }
  }
}
```

The actual Bun build always uses the vendored `node_modules` copy regardless of what tsconfig says.

### Package / distribution

`tools/package.py` builds a self-contained `.app`:
- Pre-bundles all web components via `Bun.build()` into JS/CSS — no `.ts` source ships
- Compiles `host.ts` + services into a single-file Bun executable
- Embeds `keystone.resolved.json` (pre-resolved bun config, no `.ts` evaluation at runtime)
- `bun install` + vendoring do **not** run at launch — everything is pre-built

---

## Paradigm 1 — Web Components (UI layer)

Files in `bun/web/` are auto-discovered by filename. Each exports `mount` and optionally `unmount`.

```typescript
// bun/web/app.ts
import { invoke, subscribe, action, clipboard, dialog, nativeWindow } from "@keystone/sdk/bridge";

export function mount(root: HTMLElement, ctx: SlotContext) {
  // ctx.windowId — native window ID for this slot instance
  // ctx.slotKey  — component name ("app")

  root.innerHTML = `<button id="btn">Click</button><p id="out"></p>`;

  // Subscribe to C# or Bun pushes — replays last value; returns unsubscribe fn
  const unsub = subscribe(`window:${ctx.windowId}:update`, (data) => {
    root.querySelector("#out")!.textContent = data.title;
  });

  root.querySelector("#btn")?.addEventListener("click", async () => {
    const text = await clipboard.readText();
    await invoke("myapp:process", { input: text });
  });
}

export function unmount(root: HTMLElement) {
  // cleanup listeners, destroy component instances, etc.
}
```

**React:**
```typescript
import { createRoot } from "react-dom/client";
import App from "./App";
export function mount(root: HTMLElement, ctx: SlotContext) {
  const r = createRoot(root);
  r.render(<App windowId={ctx.windowId} />);
  (root as any).__rr = r;
}
export function unmount(root: HTMLElement) {
  (root as any).__rr?.unmount();
}
```

**Theme CSS variables** — auto-applied to `:root` on load and theme change:
```css
background: var(--ks-bg-surface);
color: var(--ks-text-primary);
border: 1px solid var(--ks-stroke);
accent-color: var(--ks-accent);
font-family: var(--ks-font);
```
Tokens: `--ks-bg-base/surface/elevated/chrome/strip/hover/pressed`, `--ks-bg-button/button-hover/button-dark/medium/light`, `--ks-stroke`, `--ks-divider`, `--ks-accent/accent-bright/accent-header`, `--ks-success/warning/danger`, `--ks-text-primary/secondary/muted/subtle`, `--ks-font`

**`fetch("/api/...")` intercept** — routes `/api/*` through C#'s HttpRouter, no real HTTP:
```typescript
const notes = await fetch("/api/notes").then(r => r.json());
await fetch("/api/notes", { method: "POST", body: JSON.stringify({ title: "New" }) });
```

---

## Paradigm 2 — Bun Services (data / logic layer)

Files in `bun/services/` are auto-discovered. Directory services use `index.ts`. Each is a named service.

```typescript
// bun/services/notes.ts
import { defineService } from "@keystone/sdk/service";

export default defineService("notes")
  // Handles invokeBun("notes:getAll") from browser, or ctx.call("notes", ...) from another service
  .handle("notes:getAll", async (_, svc) => svc.store.get("notes") ?? [])
  .handle("notes:create", async ({ title, body }: any, svc) => {
    const note = { id: crypto.randomUUID(), title, body };
    const notes = svc.store.get<any[]>("notes") ?? [];
    svc.store.set("notes", [...notes, note]);
    svc.push("notes:updated", notes);  // broadcast to all subscribers (browser + C#)
    return note;
  })
  .query(async (args: any, svc) => svc.store.get("notes") ?? [])  // query("notes", args)
  .every(60_000, async (svc) => {
    svc.push("notes:heartbeat", { count: (svc.store.get<any[]>("notes") ?? []).length });
  })
  .onAction((action, svc) => {
    if (action === "notes:refresh") svc.push("notes:updated", svc.store.get("notes") ?? []);
  })
  .health((svc) => ({ ok: true }))
  .onStop((svc) => { /* cleanup connections, cancel pending work */ })
  .build();
```

**ServiceHandle** (the `svc` argument in all builder callbacks):
```typescript
svc.store.set(key, value)        // JSON KV, namespaced per service, survives hot-reload + restart
svc.store.get<T>(key)
svc.store.del(key)
svc.store.keys()                 // string[]
svc.store.clear()
svc.push(channel, data)          // broadcast to all WebSocket subscribers + C#
svc.call("other-service", args)  // query another Bun service
svc.ctx                          // raw ServiceContext
```

**Custom web messages** (real-time, bypasses C#):
```typescript
// Service:
export async function start(ctx: ServiceContext) {
  ctx.onWebMessage("chat:send", async (data, ws) => {
    ctx.push("chat:messages", await saveChatMessage(data));
  });
}
// Browser:
keystone().send("chat:send", { text: "Hi", room: "general" });
```

**Calling services from browser:**
```typescript
const notes = await invokeBun<Note[]>("notes:getAll");     // named .handle() handler
const result = await query("notes", args);                 // whole-service .query() handler
const unsub = subscribe("notes:updated", (notes) => setState(notes));
```

**Calling services from C#:**
```csharp
var json = await context.Bun.Query("notes", new { });
BunManager.Instance.SendAction("notes:refresh");
```

---

## Paradigm 2.5 — Bun Host (`bun/host.ts`)

Lifecycle extension point for the Bun process — the Electron `main.js` equivalent. Scaffolded automatically; all hooks optional.

```typescript
import { defineHost } from "@keystone/sdk/host";

export default defineHost({
  async onBeforeStart(ctx) {
    // Before service discovery — register services other services will call(), global handlers
    await ctx.registerService("db", myDbModule);
    ctx.registerInvokeHandler("app:getConfig", async () => loadConfig());
  },
  async onReady(ctx) {
    // After all services started + HTTP server live — windows opening in C#
    ctx.push("app:status", { ready: true });
  },
  async onShutdown(ctx) {
    // Before service stop() calls
    await flushPendingWrites();
  },
  onAction(action, ctx) {
    // Every action from C# or web, alongside per-service onAction handlers (sync only)
    if (action === "app:refresh") ctx.push("app:refresh", {});
  },
});
```

**HostContext** (passed to every hook):
```typescript
ctx.registerService(name, mod)           // register ServiceModule inline, calls start(ctx) immediately
ctx.registerInvokeHandler(channel, fn)   // global invokeBun() target not tied to a service
ctx.onWebMessage(type, fn)               // global send() target
ctx.push(channel, data)                  // broadcast to C# + web
ctx.services                             // ReadonlyMap of all registered services
ctx.config                               // resolved KeystoneRuntimeConfig
```

- `bun/host.ts` changes require process restart (not hot-reloaded)
- File is optional — absent = identical behavior to before

---

## Paradigm 3 — C# App Layer

### Overview — two C# entry points

| | `ICorePlugin` (`appAssembly`) | Hot-reload plugins (`dylib/`) |
|---|---|---|
| Hot-reload | No — loaded once | Yes — swap without restart |
| Purpose | Bootstrap, singletons, lifecycle wiring | Windows, services, logic, shared libraries |
| When to use | App entry point, `ServiceLocator` setup | Everything that changes during dev |

### `ICorePlugin` — App Entry Point

```csharp
using Keystone.Core.Plugins;

public class AppCore : ICorePlugin
{
    public string CoreName => "MyApp";

    public void Initialize(ICoreContext context)
    {
        // Wire lifecycle
        context.OnBeforeRun += () => { /* after all plugins loaded, before windows spawn */ };
        context.OnShutdown  += () => { _db?.Flush(); };

        // Crash recovery hooks
        context.OnBunCrash     += code => Logger.Warn($"Bun crashed ({code})");
        context.OnBunRestart   += attempt => Logger.Info($"Bun recovered (attempt {attempt})");
        context.OnWebViewCrash += windowId => Logger.Warn($"WebView crashed in {windowId}");

        // Action routing — fire-and-forget from any source (browser, menu, keyboard)
        context.OnUnhandledAction = (action, source) => {
            switch (action) {
                case "myapp:new":   context.RunOnMainThread(CreateDocument); break;
                case "myapp:quit":  /* ... */ break;
            }
        };

        // Register custom services into ServiceLocator for other plugins to find
        ServiceLocator.Register(new MyDataService());
        ServiceLocator.Register(ApplicationRuntime.Instance!.WindowManager);

        // Register invoke handlers on every window as it spawns
        context.OnBeforeRun += () => {
            foreach (var win in context.Windows.All)
                RegisterHandlers(win, context);
        };
    }
}
```

### `ICoreContext` — Full Reference

```csharp
public interface ICoreContext
{
    KeystoneConfig Config { get; }     // parsed keystone.json
    string RootDir { get; }            // path to keystone.json directory

    // Registration
    void RegisterWindow(IWindowPlugin plugin);
    void RegisterService<T>(T service) where T : class;
    void RegisterService(IServicePlugin plugin);

    // Lifecycle
    event Action? OnBeforeRun;         // after all plugins init, windows about to spawn
    event Action? OnShutdown;          // shutdown, save state here

    // Crash recovery
    event Action<int>? OnBunCrash;
    event Action<int>? OnBunRestart;
    event Action<string>? OnWebViewCrash;

    // Action routing
    Action<string, string>? OnUnhandledAction { set; }   // (action, source windowId)

    // Threading
    void RunOnMainThread(Action action);
    void RunOnMainThreadAndWait(Action action);

    // Bun bridge
    IBunService Bun { get; }

    // HTTP-style route router (fetch("/api/...") intercept)
    IHttpRouter Http { get; }
}
```

### Custom Invoke Handlers

Register on `ManagedWindow`. Runs on a **thread pool thread** — dispatch to main thread for platform UI APIs.

```csharp
window.RegisterInvokeHandler("myapp:readFile", async args => {
    // args: JsonElement — the args object from JS
    // Return: any JSON-serializable value → resolve the JS promise
    // Throw: Exception → reject the JS promise with reply.error
    var path = args.GetProperty("path").GetString()!;
    if (!File.Exists(path)) throw new FileNotFoundException($"No file at {path}");
    return await File.ReadAllTextAsync(path);
});

// Platform UI API — must dispatch to main thread
window.RegisterInvokeHandler("myapp:pickColor", args => {
    var tcs = new TaskCompletionSource<object?>();
    NSApplication.SharedApplication.InvokeOnMainThread(() => {
        var panel = NSColorPanel.SharedColorPanel;
        panel.MakeKeyAndOrderFront(null);
        tcs.TrySetResult(panel.Color.ToString());
    });
    return tcs.Task;
});
```

```typescript
// Browser
const content = await invoke<string>("myapp:readFile", { path: "/etc/hosts" });
const color   = await invoke<string>("myapp:pickColor");
```

### HTTP Router (`context.Http`)

An alternative to raw invoke handlers. Routes `fetch("/api/...")` calls. Fluent, first-match-wins.

```csharp
context.Http
    .Get("/api/notes", async req => {
        var notes = await _db.GetAllAsync();
        return HttpResponse.Json(notes);
    })
    .Get("/api/notes/:id", async req => {
        var note = await _db.GetAsync(req.Params["id"]);
        return note != null ? HttpResponse.Json(note) : HttpResponse.NotFound();
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

// Streaming — browser gets ReadableStream
context.Http.Get("/api/export", req =>
    HttpResponse.Stream(async stream => {
        await foreach (var row in _db.StreamAllRowsAsync())
            await stream.WriteAsync(JsonSerializer.Serialize(row) + "\n");
    })
);
```

**`HttpRequest` fields:** `Method`, `Path`, `Query` (dict), `Params` (dict from `:param` captures), `Body` (JsonElement), `WindowId`

**Response types:** `HttpResponse.Json(obj, status=200)`, `.Text(str)`, `.NoContent()`, `.NotFound(msg?)`, `.Error(msg, status=500)`, `.Stream(writer)`

### Pushing to Browser from C#

```csharp
// Push to all subscribers of a channel
BunManager.Instance.Push("prices:btc", new { usd = 62_000 });

// Push to a specific window only
BunManager.Instance.Push($"window:{windowId}:notification", new { title = "Done" });

// Query a Bun service from C#
var json = await context.Bun.Query("notes", new { });
using var doc = JsonDocument.Parse(json!);
var count = doc.RootElement.GetProperty("count").GetInt32();

// Dispatch an action to all Bun service onAction handlers
BunManager.Instance.SendAction("myapp:refresh");
```

---

## Paradigm 4 — Hot-Reload Plugin System

Everything in `dylib/` is hot-reloaded. Drop a DLL in — auto-loaded. Overwrite it — old ALC unloaded, new version loads. No restart.

### Plugin Types

| Interface | Purpose | Thread |
|-----------|---------|--------|
| `ICorePlugin` | Bootstrap — loaded via `appAssembly`, NOT hot-reloaded | Main (once) |
| `IWindowPlugin` | GPU/Skia native windows | Window render thread (vsync) |
| `IServicePlugin` | Background work, long-lived connections, system integration | Background or main |
| `ILogicPlugin` | Render/compute logic, GPU pipelines, per-frame processing | Window render thread |
| `ILibraryPlugin` | Shared code reused across other plugins — changing it cascade-reloads dependents | Any |

---

### `IWindowPlugin` — Native GPU/Skia Windows

Extend `WindowPluginBase` for default wiring. `BuildScene()` is preferred (retained scene graph, diffed between frames). `Render()` is for custom Skia drawing.

```csharp
public class DashboardWindow : WindowPluginBase
{
    public override string WindowType => "dashboard";
    public override (float, float) DefaultSize => (1400, 900);
    public override PluginRenderPolicy RenderPolicy => PluginRenderPolicy.Continuous;

    private float _cpuPercent;

    // Scene graph API — retained, diffed between frames (efficient)
    public override SceneNode? BuildScene(FrameState state) =>
        new FlexNode {
            Direction = FlexDirection.Column,
            Background = Theme.BgSurface,
            Padding = 24, Gap = 16,
            Children = [
                new TextNode($"CPU: {_cpuPercent:F1}%") {
                    FontSize = 28, Color = Theme.TextPrimary, FontWeight = 600
                },
                new FlexNode {
                    Height = 4,
                    Width = _cpuPercent / 100f * state.Width,
                    Background = Theme.Accent, Radius = 2
                }
            ]
        };

    // Immediate Skia — use when scene graph doesn't cover your drawing
    public override void Render(RenderContext ctx) {
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        ctx.Canvas.DrawCircle(ctx.Width / 2f, ctx.Height / 2f, 100, paint);
        ctx.RequestRedraw();
    }

    // Hit testing — called on mouse down (action resolution) + mouse move (cursor)
    public override HitTestResult? HitTest(float x, float y, float w, float h) {
        if (x >= w - 40 && x < w && y < 30)
            return new HitTestResult { Action = "window:close", Cursor = CursorType.Default };
        if (y < 30)
            return new HitTestResult { Action = null, Cursor = CursorType.Default };  // drag region
        return null;
    }

    // Scroll input
    public Action<float, float, float, float, float, float>? OnScroll =>
        (deltaX, deltaY, mouseX, mouseY, width, height) => {
            _scrollOffset += deltaY;
            // call RequestRedraw() if needed
        };

    // Keyboard input
    public Action<ushort, KeyModifiers>? OnKeyDown =>
        (keyCode, modifiers) => {
            if (keyCode == 53) CloseOverlay?.Invoke();  // Escape
        };

    // Overlay system — floating panels, dropdowns
    // ShowOverlay and CloseOverlay are wired by ManagedWindow
    void OpenDropdown(float anchorX, float anchorY) {
        ShowOverlay?.Invoke(new MyDropdown(), anchorX, anchorY);
    }
}
```

**Scene node types:** `FlexNode` (direction, padding, gap, background, radius, width, height, children), `TextNode` (text, fontSize, color, fontWeight), `ButtonNode` (label, onClick), `ImageNode`

**`FrameState`:** `Width`, `Height`, `DeltaTime`, `FrameCount`

**`WindowPluginBase` fields set by runtime:** `WindowId` (uint), `ShowOverlay`, `CloseOverlay`

---

### `IServicePlugin` — Background C# Services

```csharp
public class FileWatcherService : IServicePlugin
{
    public string ServiceName => "file-watcher";
    public bool RunOnBackgroundThread => true;  // false = main thread

    private FileSystemWatcher? _watcher;

    public void Initialize()
    {
        ServiceLocator.Register(this);  // expose to other plugins

        _watcher = new FileSystemWatcher("/path/to/watch") { EnableRaisingEvents = true };
        _watcher.Changed += (_, e) => {
            BunManager.Instance?.Push("files:changed", new { path = e.FullPath });
        };
    }

    public void Shutdown()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // Public API for other plugins via ServiceLocator.Get<FileWatcherService>()
    public string[] GetWatchedPaths() => /* ... */;
}
```

**Preserve state across hot-reloads** — implement `IReloadableService`:
```csharp
public class MyService : IReloadableService
{
    public string ServiceName => "my-service";
    public bool RunOnBackgroundThread => true;
    public void Initialize() { }
    public void Shutdown() { }

    private int _counter;
    public byte[]? SerializeState() =>
        System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(_counter));
    public void RestoreState(byte[]? state) {
        if (state != null) _counter = JsonSerializer.Deserialize<int>(state);
    }
}
```

---

### `ILogicPlugin` — Render/Compute Logic

Attached to windows and invoked during their render cycle. Used for GPU compute, custom render passes, per-frame processing.

```csharp
public class LineRenderer : ILogicPlugin
{
    public string LogicName => "lines";
    public bool RequiresGpu => true;   // signals need for Metal/Vulkan resources
    public int RenderOrder => 0;       // -100 = deep bg, 0 = content, 100 = overlay, 200 = HUD
    public IEnumerable<string> Dependencies => Array.Empty<string>();

    public void Initialize() { }

    // Called from IWindowPlugin.Render() passing ctx and window-specific state
    static readonly ConcurrentDictionary<string, GpuState> _state = new();

    public static void Render(RenderContext ctx, string windowId)
    {
        // macOS: cast to WindowGpuContext; Linux: VulkanGpuContext
        if (ctx.Gpu is not WindowGpuContext gpu) return;
        var state = _state.GetOrAdd(windowId, _ => new GpuState(gpu.Device));
        // ... dispatch Metal compute, draw with ctx.Canvas ...
    }
}

class GpuState : IDisposable
{
    public readonly IMTLComputePipelineState Pipeline;
    const string Shader = @"
#include <metal_stdlib>
using namespace metal;
kernel void myKernel(device float* out [[buffer(0)]], uint id [[thread_position_in_grid]]) {
    out[id] = id;
}";
    public GpuState(IMTLDevice device) {
        var lib = device.CreateLibrary(Shader, new MTLCompileOptions(), out _);
        Pipeline = device.CreateComputePipelineState(lib!.CreateFunction("myKernel")!, out _);
    }
    public void Dispose() { }
}
```

**`IGpuContext` (via `ctx.Gpu`):**
```csharp
ctx.Gpu.Device           // IMTLDevice (macOS) | VkDevice (Linux) — shared, thread-safe
ctx.Gpu.Queue            // IMTLCommandQueue (macOS) | VkQueue — per-window
ctx.Gpu.GraphicsContext  // GRContext (Skia) — per-window, NOT thread-safe
ctx.Gpu.ImportTexture(handle, w, h)  // import GPU texture handle into Skia SKImage
// Cast for strongly-typed access:
if (ctx.Gpu is WindowGpuContext gpu) { /* gpu.Device, gpu.Queue, gpu.GRContext */ }
```

---

### `ILibraryPlugin` — Shared Code

No work of its own. When it reloads, DyLibLoader cascade-reloads every plugin that depends on it.

```csharp
public class SharedThemeLibrary : ILibraryPlugin
{
    public string LibraryName => "shared-theme";
    public static SharedThemeLibrary? Instance { get; private set; }
    public SKColor Accent { get; private set; } = SKColors.CornflowerBlue;

    public void Initialize() {
        Instance = this;
        var stored = KeystoneDb.Instance?.GetString("theme:accent");
        if (stored != null) Accent = SKColor.Parse(stored);
    }

    public void SetAccent(SKColor color) {
        Accent = color;
        KeystoneDb.Instance?.SetString("theme:accent", color.ToString());
        BunManager.Instance?.Push("theme:accent", new { hex = color.ToString() });
    }
}
```

---

### `IStatefulPlugin` — Preserve State Across Hot-Reloads

Any plugin type can implement this. State is serialized before unload, deserialized after reload.

```csharp
public class ChartWindow : WindowPluginBase, IStatefulPlugin
{
    public override string WindowType => "chart";
    private float _scrollOffset, _zoomLevel = 1.0f;

    public byte[] SerializeState() {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(_scrollOffset); bw.Write(_zoomLevel);
        return ms.ToArray();
    }
    public void RestoreState(byte[] state) {
        using var ms = new MemoryStream(state);
        using var br = new BinaryReader(ms);
        _scrollOffset = br.ReadSingle(); _zoomLevel = br.ReadSingle();
    }
}
```

**Workspace persistence** (disk, user-facing) — `SerializeConfig`/`RestoreConfig` on `WindowPluginBase`:
```csharp
public override string? SerializeConfig() =>
    JsonSerializer.Serialize(new { FilePath = _path, ScrollY = _scrollY });
public override void RestoreConfig(string json) {
    var cfg = JsonSerializer.Deserialize<dynamic>(json)!;
    ApplyConfig(cfg);
}
public override bool ExcludeFromWorkspace => false;  // set true for transient/debug windows
```

---

### Plugin csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-macos</TargetFramework>  <!-- net10.0 for Linux -->
    <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>  <!-- linux-x64 for Linux -->
    <EnableDynamicLoading>true</EnableDynamicLoading>  <!-- REQUIRED for hot-reload -->
    <AssemblyName>MyPlugin</AssemblyName>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>  <!-- don't copy engine DLLs -->
  </PropertyGroup>

  <ItemGroup>
    <!-- Private=false prevents copying engine DLLs into plugin output -->
    <ProjectReference Include="../../keystone/Keystone.Core/Keystone.Core.csproj">
      <Private>false</Private></ProjectReference>
    <ProjectReference Include="../../keystone/Keystone.Core.Runtime/Keystone.Core.Runtime.csproj">
      <Private>false</Private></ProjectReference>
    <ProjectReference Include="../../keystone/Keystone.Core.Management/Keystone.Core.Management.csproj">
      <Private>false</Private></ProjectReference>

    <!-- GPU windows: macOS -->
    <ProjectReference Include="../../keystone/Keystone.Core.Graphics.Skia/Keystone.Core.Graphics.Skia.csproj">
      <Private>false</Private></ProjectReference>
    <!-- GPU windows: Linux — replace above with: -->
    <!-- <ProjectReference Include=".../Keystone.Core.Graphics.Skia.Vulkan/..."><Private>false</Private></ProjectReference> -->

    <!-- Reference another app-level plugin DLL (creates cascade-reload dependency edge) -->
    <Reference Include="AppCore">
      <HintPath>../dylib/AppCore.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>

  <!-- Auto-copy to dylib/ for hot-reload discovery -->
  <Target Name="PostBuild" AfterTargets="PostBuildEvent">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="../dylib/" />
  </Target>
</Project>
```

**Custom plugin types** — hook into hot-reload discovery:
```csharp
// In ICorePlugin.Initialize()
DyLibLoader.Instance?.RegisterCustomPluginType<IChartPlugin>(
    onLoaded: plugin => ChartRegistry.Register(plugin),
    onUnloaded: name => ChartRegistry.Unregister(name)
);
```

---

## Paradigm 5 — Native API (from browser)

All imports from `@keystone/sdk/bridge`. All `invoke()`-based — require WKWebView.

```typescript
import {
  app, nativeWindow, dialog, shell,
  clipboard, screen, notification, nativeTheme, powerMonitor,
  globalShortcut, headless,
  invoke, invokeBun, subscribe, action, query, keystone
} from "@keystone/sdk/bridge";
```

### Core bridge
```typescript
invoke<T>(channel, args?)      // → C# RegisterInvokeHandler, direct, 15s timeout
invokeBun<T>(channel, args?)   // → Bun .handle() handler, over WebSocket, 15s timeout
subscribe(channel, cb)         // WebSocket sub, replays last value, returns unsub fn
action("myapp:foo")            // fire-and-forget → C# OnUnhandledAction + all service onAction
query("my-service", args?)     // Bun whole-service .query() handler
keystone().publish(ch, data)   // publish to channel (all windows receive)
keystone().send(type, data)    // raw WebSocket message (services receive via onWebMessage)
```

### `app`
```typescript
await app.getPath("userData")     // ~/Library/Application Support/<id>
await app.getPath("documents")    // also: downloads, desktop, temp, appRoot
await app.getName(); await app.getVersion()
app.quit()
```

### `nativeWindow`
```typescript
await nativeWindow.setTitle("My App — Untitled")
await nativeWindow.open("settings")             // returns new window ID
await nativeWindow.setFloating(true)
await nativeWindow.getBounds()                  // { x, y, width, height }
await nativeWindow.setBounds({ width: 1024 })   // partial — omitted fields unchanged
await nativeWindow.center()
nativeWindow.minimize(); nativeWindow.maximize(); nativeWindow.close()
```

### `dialog`
```typescript
const paths = await dialog.openFile({ title: "Open", filters: ["png","jpg"], multiple: true })
const path  = await dialog.saveFile({ title: "Export", filters: ["pdf"], defaultName: "out.pdf" })
const btn   = await dialog.showMessage({ title: "Delete?", message: "Cannot undo.", buttons: ["Cancel","Delete"] })
```

### `clipboard`
```typescript
const text = await clipboard.readText()  // null if empty
await clipboard.writeText("hello")
await clipboard.clear()
await clipboard.hasText()                // boolean
// macOS: NSPasteboard | Windows: CF_UNICODETEXT | Linux: wl-paste/xclip
```

### `screen`
```typescript
type DisplayInfo = { x, y, width, height, scaleFactor, primary: boolean }
const displays = await screen.getAllDisplays()
const primary  = await screen.getPrimaryDisplay()
const cursor   = await screen.getCursorScreenPoint()  // { x, y }
```

### `notification`
```typescript
await notification.show("Build complete", "Compiled in 2.3s")
// macOS: osascript | Linux: notify-send | Windows: MessageBox
```

### `nativeTheme`
```typescript
const dark = await nativeTheme.isDarkMode()
const unsub = nativeTheme.onChange((dark) => document.documentElement.classList.toggle("dark", dark))
```

### `powerMonitor`
```typescript
type PowerStatus = { onBattery: boolean; batteryPercent: number }  // -1 = unknown
const status = await powerMonitor.getStatus()
const unsub = powerMonitor.onChange((s) => updateBatteryUI(s))
// macOS: pmset | Windows: GetSystemPowerStatus | Linux: /sys/class/power_supply
```

### `globalShortcut`
```typescript
// Modifiers: Control/Ctrl, Shift, Alt/Option, Meta/Command/Cmd, CommandOrControl
const ok = await globalShortcut.register("CommandOrControl+Shift+P")  // false if taken
await globalShortcut.isRegistered("CommandOrControl+Shift+P")
const unsub = globalShortcut.onFired("CommandOrControl+Shift+P", openCommandPalette)
await globalShortcut.unregister("CommandOrControl+Shift+P")
// macOS: Carbon RegisterEventHotKey | Windows: Win32 RegisterHotKey+WM_HOTKEY thread | Linux: stub (false)
```

### `headless`
```typescript
const id = await headless.open("renderer")            // invisible window, full WebKit, returns windowId
await headless.evaluate(id, `globalFn()`)              // fire-and-forget JS execution in that window
const ids = await headless.list()
await headless.close(id)
// To get results back: headless window pushes to a channel via keystone().publish(ch, data)
// Calling window subscribes to that channel before calling evaluate()
```

---

## Shared Utilities (C#)

### `ServiceLocator`
```csharp
ServiceLocator.Register(new MyService());       // register in ICorePlugin.Initialize()
var svc = ServiceLocator.Get<MyService>();      // retrieve anywhere (returns null if not registered)
```

### `BunManager`
```csharp
BunManager.Instance.Push(channel, data)         // push to WebSocket subscribers + Bun
BunManager.Instance.SendAction(action)          // dispatch action to Bun services
await BunManager.Instance.QueryAsync(svc, args) // query Bun service, returns JSON string
BunManager.Instance.OnServicePush += (channel, json) => { };  // receive Bun service pushes in C#
```

### `KeystoneDb` — C# side SQLite KV
```csharp
KeystoneDb.Instance?.SetString("theme:accent", "#4a6fa5");
KeystoneDb.Instance?.GetString("theme:accent");
KeystoneDb.Instance?.SetBytes("blob:key", bytes);
KeystoneDb.Instance?.GetBytes("blob:key");
```

### `ApplicationRuntime.Instance`
```csharp
ApplicationRuntime.Instance!.WindowManager  // IWindowManager — spawn/close windows
ApplicationRuntime.Instance!.Platform       // IPlatform — clipboard, screen, dark mode, etc.
```

---

## Key Patterns

### Window-scoped push (target one window)
```csharp
BunManager.Instance.Push($"window:{windowId}:event", data);  // C#
```
```typescript
subscribe(`window:${ctx.windowId}:event`, handler);  // browser
```

### Inter-service communication
```typescript
const result = await svc.call("other-service", args);  // request/reply between Bun services
```

### Opening windows programmatically
```typescript
const id = await nativeWindow.open("settings");  // browser
```
```csharp
_windowManager.SpawnWindow("settings");  // C#
```

### Going web-only (no C# app layer)
Omit `appAssembly` and `plugins.enabled` from `keystone.json`. All logic lives in Bun services.

### Going C#-only (no web layer)
Omit the `bun` block from `keystone.json`. No Bun process starts. Implement `IWindowPlugin` for GPU/Skia windows.

---

## Programmatic Bootstrap (`KeystoneApp`)

`KeystoneApp` is a fluent builder API for creating Keystone applications entirely from C# — no `keystone.json` required.

```csharp
KeystoneApp.Create("My App", "com.example.myapp")
    .Window("app", w => w.Title("My App").Size(1200, 800))
    .WithBun()
    .Run();
```

### Builder API

| Method | Effect |
|--------|--------|
| `KeystoneApp.Create(name, id)` | Static factory, returns builder |
| `.Window(component, configure?)` | Register a web window — configure callback exposes `WindowBuilder` |
| `.Window<T>()` | Register a native `IWindowPlugin` by type |
| `.Window(plugin)` | Register a native `IWindowPlugin` instance |
| `.Service<T>()` | Register an `IServicePlugin` by type |
| `.WithBun(root?)` | Enable Bun subprocess (default root: `"bun"`) |
| `.WithPlugins(dir?)` | Enable hot-reloadable plugin loading (default dir: `"dylib"`) |
| `.RootDir(path)` | Override root directory resolution |
| `.Run()` | Build, initialize, run. Blocks until shutdown. |

### WindowBuilder

| Method | Effect |
|--------|--------|
| `.Title(string)` | Window title |
| `.Size(float w, float h)` | Initial size in points |
| `.NoSpawn()` | Don't open on launch — register as a spawnable type |
| `.Toolbar(configure)` | Add a toolbar strip below the title bar |

### Mixed web + native example

```csharp
KeystoneApp.Create("Trading Platform", "com.example.trading")
    .Window("dashboard", w => w.Title("Dashboard").Size(1400, 900))
    .Window("settings", w => w.Title("Settings").Size(600, 400).NoSpawn())
    .Window<ChartWindow>()
    .Window<OrderBookWindow>()
    .Service<MarketDataService>()
    .WithBun()
    .WithPlugins()
    .Run();
```

`KeystoneApp` and `keystone.json` are not mutually exclusive — if a `keystone.json` exists, the runtime reads it regardless.

---

## Window Chrome (`titleBarStyle`)

Four window chrome modes control the combination of native controls and GPU-rendered title bar:

| `titleBarStyle` | Window type | Native controls | GPU title bar |
|---|---|---|---|
| `"hidden"` (default) | Titled | Traffic lights / GTK decorations | No |
| `"toolkit"` | Borderless | No | Yes — close, minimize, float, tabs |
| `"toolkit-native"` | Titled | Traffic lights / GTK decorations | Yes — tabs, float only (no close/min) |
| `"none"` | Borderless | No | No |

Any style can combine with `renderless: true`. GPU title bar components won't render without a GPU surface, but window style and native controls still apply.

**`"toolkit-native"` is for windows that want both native window management (traffic lights, rounded corners, OS window snapping) and the GPU title bar (tabs, float toggle).** The title bar is 52px tall, with a 58px left spacer to clear macOS traffic lights. Close and minimize are handled by the native controls — the GPU bar only renders tabs and the float toggle.

---

## Common Mistakes

- **`action()` vs `invoke()`** — `action()` is fire-and-forget, hits all `onAction` handlers globally. `invoke()` is request/reply to a specific named handler. Don't use action when you need a return value.
- **Thread safety in invoke handlers** — handlers run on thread pool. Any AppKit/GTK platform UI APIs require `RunOnMainThread`.
- **`invokeBun` vs `invoke`** — `invoke` → C# directly (fast, no Bun hop). `invokeBun` → Bun `.handle()` (over WebSocket). Use `invoke` for native OS APIs, `invokeBun` for business logic.
- **`subscribe` replays last value** — new subscribers immediately get the last cached value. If your channel carries ephemeral events (not state snapshots), this can cause stale fires.
- **`renderless: true`** is valid with all `titleBarStyle` values — the window chrome style applies but GPU title bar components won't render without a GPU surface.
- **`ICorePlugin` via `appAssembly` is NOT hot-reloaded** — only plugins discovered from `dylib/` hot-reload. Don't put your ICorePlugin in `dylib/` without `appAssembly` pointing to it.
- **`IWindowPlugin` and `IServicePlugin` in `dylib/` DO hot-reload** — implement `IStatefulPlugin` or `IReloadableService` to preserve in-memory state across reloads.
- **`globalShortcut` on Linux** — always returns false. Wayland GlobalShortcuts portal not implemented.
- **Service module JS variables** — wiped on hot-reload. Persist anything durable in `svc.store` (SQLite).
- **`Private=false` on engine references** in plugin csproj — omitting this copies engine DLLs into plugin output and breaks ALC unloading.
- **Plugin `Shutdown()` must clean up** — timers, watchers, subscriptions not disposed in `Shutdown()` persist as leaked references; the ALC won't be collected and hot-reload won't fully unload.
