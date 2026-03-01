# C# Layer

> Last updated: 2026-02-28

The C# host is Keystone's main process — the equivalent of Electron's main process, written in C#. You get the full .NET 10 standard library, direct P/Invoke to platform frameworks (AppKit/Metal on macOS, GTK4/Vulkan on Linux), and native threads with real memory ownership.

This document covers the **app layer** (`ICorePlugin`), the **plugin system** (hot-reloadable DLLs), the **HTTP router**, and **programmatic bootstrap** (`KeystoneApp`).

---

## Entry Point — `ICorePlugin`

Your app layer is a class implementing `ICorePlugin`. The runtime discovers it from DLLs in `dylib/` via `DyLibLoader` and calls `Initialize()` once, after engine startup, before any windows spawn.

```csharp
using Keystone.Core.Plugins;

namespace MyApp;

public class AppCore : ICorePlugin
{
    public string CoreName => "MyApp";

    public void Initialize(ICoreContext context)
    {
        // Register services, windows, event handlers — all from here
    }
}
```

`ICoreContext` is your handle to the runtime. Use it for everything — avoid reaching for `ApplicationRuntime.Instance` or other singletons directly.

The app layer is **optional**. If the built-in invoke handlers cover your needs (file dialogs, window management, path queries), skip it entirely.

### ICoreContext Reference

```csharp
public interface ICoreContext
{
    // App identity and config
    KeystoneConfig Config { get; }
    string RootDir { get; }

    // Registration
    void RegisterWindow(IWindowPlugin plugin);
    void RegisterService<T>(T service) where T : class;
    void RegisterService(IServicePlugin plugin);

    // Lifecycle events
    event Action? OnBeforeRun;       // windows are about to spawn
    event Action? OnShutdown;        // app is shutting down, save state here

    // Process lifecycle (crash recovery)
    event Action<int>? OnBunCrash;        // Bun subprocess exited unexpectedly
    event Action<int>? OnBunRestart;      // Bun restarted successfully
    event Action<string>? OnWebViewCrash; // WebKit content process crashed

    // Custom action handling
    Action<string, string>? OnUnhandledAction { set; }

    // Thread control
    void RunOnMainThread(Action action);
    void RunOnMainThreadAndWait(Action action);

    // Bun bridge
    IBunService Bun { get; }

    // HTTP-style route router
    IHttpRouter Http { get; }
}
```

### Lifecycle Events

```
ICorePlugin.Initialize()
    ↓
context.OnBeforeRun        ← all plugins initialized, windows about to spawn
    ↓
[initial windows spawn]
    ↓
[main loop runs]
    ↓
context.OnShutdown         ← save state here, before cleanup begins
```

```csharp
public void Initialize(ICoreContext context)
{
    context.OnBeforeRun += () =>
    {
        // Restore saved state, apply theming, pre-populate data
    };

    context.OnShutdown += () =>
    {
        _db?.Flush();
    };
}
```

### Process Recovery Hooks

```csharp
context.OnBunCrash += exitCode =>
{
    Logger.Warn($"Bun exited with code {exitCode}");
};

context.OnBunRestart += attempt =>
{
    Logger.Info($"Bun recovered (attempt {attempt})");
};

context.OnWebViewCrash += windowId =>
{
    Logger.Warn($"WebView crashed in window {windowId}");
};
```

---

## Custom Invoke Handlers

`invoke()` from the TypeScript side resolves to a named handler registered on `ManagedWindow`. Built-in handlers cover `app:*`, `window:*`, `dialog:*`, `external:*`, `darkMode:*`, `battery:*`, and `hotkey:*`. Register your own for anything custom.

The handler signature is `Func<JsonElement, Task<object?>>`:

```csharp
window.RegisterInvokeHandler("myapp:readFile", async args =>
{
    var path = args.GetProperty("path").GetString()
               ?? throw new ArgumentException("path required");

    if (!File.Exists(path))
        throw new FileNotFoundException($"No file at {path}");

    return await File.ReadAllTextAsync(path);
});
```

```typescript
const content = await invoke<string>("myapp:readFile", { path: "/etc/hosts" });
```

Handlers run on a thread pool thread. For platform UI APIs, dispatch to the main thread:

```csharp
window.RegisterInvokeHandler("myapp:pickColor", args =>
{
    var tcs = new TaskCompletionSource<object?>();
    NSApplication.SharedApplication.InvokeOnMainThread(() =>
    {
        var panel = NSColorPanel.SharedColorPanel;
        panel.MakeKeyAndOrderFront(null);
        tcs.TrySetResult(panel.Color.ToString());
    });
    return tcs.Task;
});
```

---

## Custom Actions

Actions are fire-and-forget strings routed through `ActionRouter`. Menu items, keyboard shortcuts, toolbar buttons, and web-layer `action()` calls all flow through the same path.

```csharp
context.OnUnhandledAction = (action, source) =>
{
    switch (action)
    {
        case "myapp:new-document":
            context.RunOnMainThread(() => CreateNewDocument());
            break;

        case "myapp:export-pdf":
            Task.Run(() => ExportCurrentDocument());
            break;
    }
};
```

```typescript
action("myapp:new-document");
action("myapp:export-pdf");
```

---

## Native Windows (GPU/Skia)

When you need maximum rendering performance or want to bypass the web layer, implement `IWindowPlugin`. The renderer calls `BuildScene()` or `Render()` on a per-window background thread at vsync.

### WindowPluginBase

Most windows extend `WindowPluginBase`:

```csharp
public abstract class WindowPluginBase : IWindowPlugin
{
    public abstract string WindowType { get; }
    public virtual string WindowTitle => WindowType;
    public virtual (float Width, float Height) DefaultSize => (800, 600);
    public virtual PluginRenderPolicy RenderPolicy => PluginRenderPolicy.Continuous;
    public virtual IEnumerable<string> Dependencies => Array.Empty<string>();

    public uint WindowId { get; set; }

    public abstract void Render(RenderContext ctx);
    public virtual SceneNode? BuildScene(FrameState state) => null;
    public virtual HitTestResult? HitTest(float x, float y, float w, float h) => null;

    // Workspace persistence
    public virtual string? SerializeConfig() => null;
    public virtual void RestoreConfig(string json) { }
    public virtual bool ExcludeFromWorkspace => false;

    // Overlay system
    public Action<IOverlayContent, double, double>? ShowOverlay { get; set; }
    public Action? CloseOverlay { get; set; }
}
```

### Scene Graph API (recommended)

`BuildScene()` returns a retained scene graph. The renderer diffs and caches geometry between frames — no GPU re-upload unless something changes.

```csharp
public override SceneNode? BuildScene(FrameState state)
{
    return new FlexNode
    {
        Direction = FlexDirection.Column,
        Background = Theme.BgSurface,
        Padding = 24,
        Gap = 16,
        Children =
        [
            new TextNode($"CPU: {_cpuPercent:F1}%")
            {
                FontSize = 28,
                Color = Theme.TextPrimary,
                FontWeight = 600
            },
            new FlexNode
            {
                Height = 4,
                Width = _cpuPercent / 100f * state.Width,
                Background = Theme.Accent,
                Radius = 2
            }
        ]
    };
}
```

### Immediate-mode API

`Render()` gives you a raw Skia canvas via `RenderContext`:

```csharp
public override void Render(RenderContext ctx)
{
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    ctx.Canvas.DrawCircle(ctx.Width / 2f, ctx.Height / 2f, 100, paint);
    ctx.RequestRedraw();
}
```

### Hit Testing

```csharp
public override HitTestResult? HitTest(float x, float y, float w, float h)
{
    if (x >= w - 40 && x < w && y < 30)
        return new HitTestResult { Action = "window:close", Cursor = CursorType.Default };

    if (y < 30)
        return new HitTestResult { Action = null, Cursor = CursorType.Default }; // drag region

    return null;
}
```

### Logic Plugin Dispatch

Window plugins invoke logic plugins via `LogicRegistry` typed dispatch. The app defines delegate types matching its render signatures, then dispatches through the framework — zero reflection, zero boxing on the hot path.

```csharp
// App-side delegate
public delegate void CanvasRenderDelegate(
    RenderContext ctx, float x, float y, float w, float h,
    string windowId, ViewState state);
```

**Single named plugin:**
```csharp
LogicRegistry.Dispatch<CanvasRenderDelegate>("grid", "Render",
    del => del(ctx, x, y, w, h, windowId, viewState));
```

**All plugins in compositor order** (sorted by `ILogicPlugin.RenderOrder`):
```csharp
LogicRegistry.DispatchAll<CanvasRenderDelegate>("Render",
    del => del(ctx, x, y, w, h, windowId, viewState));
```

**Subset by RenderOrder range:**
```csharp
LogicRegistry.DispatchRange<CanvasRenderDelegate>("Render", -100, 0,
    del => del(ctx, x, y, w, h, windowId, viewState));
```

The delegate is created once via reflection on first call, then cached. Hot-reload invalidates per-plugin cache entries automatically.

### Scroll and Keyboard Input

```csharp
public Action<float, float, float, float, float, float>? OnScroll =>
    (deltaX, deltaY, mouseX, mouseY, width, height) =>
    {
        _zoomLevel += deltaY * 0.01f;
    };

public Action<ushort, KeyModifiers>? OnKeyDown =>
    (keyCode, modifiers) =>
    {
        if (keyCode == 53) CloseOverlay?.Invoke(); // Escape
    };
```

### Overlay System

`WindowPluginBase` provides `ShowOverlay` and `CloseOverlay` for floating overlay windows:

```csharp
var dropdown = new MyDropdown(items);
ShowOverlay?.Invoke(dropdown, anchorX, anchorY);
CloseOverlay?.Invoke();
```

### Workspace Serialization

Windows can persist their configuration across sessions:

```csharp
public override string? SerializeConfig()
{
    return JsonSerializer.Serialize(new LayoutConfig
    {
        FilePath  = view.FilePath,
        ScrollY   = view.ScrollY,
        FontSize  = view.FontSize,
    });
}

public override void RestoreConfig(string json)
{
    var layout = JsonSerializer.Deserialize<LayoutConfig>(json);
    if (layout == null) return;
    ApplyLayout(layout);
}
```

Set `ExcludeFromWorkspace => true` on windows that shouldn't be persisted. This is distinct from `IStatefulPlugin` which preserves ephemeral in-memory state across hot-reloads.

---

## Bun Bridge from C#

`context.Bun` exposes `IBunService` — push to channels, query Bun services, or eval JS.

```csharp
context.Bun.Push("data:updated", new { items = 42, status = "ready" });

var result = await context.Bun.Query("file-scanner", new { dir = "/tmp" });
using var doc = JsonDocument.Parse(result!);
var count = doc.RootElement.GetProperty("count").GetInt32();
```

---

## HTTP Router

The HTTP router is an optional convenience layer. It lets you write C# handler functions that the browser calls with a normal `fetch()`. There is no HTTP server — requests go through the existing bridge between the browser and C#.

When `keystone()` initializes, it patches `window.fetch` to intercept requests whose path starts with `/api/`. Instead of making a real HTTP request, the intercepted fetch calls `invoke("http:request", ...)`, which routes to `HttpRouter.DispatchAsync()` on the C# side.

### Registering Routes

```csharp
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
```

```typescript
const notes = await fetch("/api/notes").then(r => r.json());
```

### Route Parameters

Segments prefixed with `:` are captured into `req.Params`:

```csharp
context.Http.Get("/api/projects/:projectId/tasks/:taskId", req => {
    var projectId = req.Params["projectId"];
    var taskId    = req.Params["taskId"];
    return HttpResponse.Json(_db.GetTask(projectId, taskId));
});
```

### The Request Object

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

### Response Types

```csharp
HttpResponse.Json(object? body, int status = 200)
HttpResponse.Text(string body, int status = 200)
HttpResponse.NoContent()                            // 204
HttpResponse.NotFound(string? message = null)        // 404
HttpResponse.Error(string message, int status = 500)
HttpResponse.Stream(Func<IHttpStreamWriter, Task>)  // streaming
```

### Streaming Responses

For long-running operations, progress, or live data:

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
    console.log(JSON.parse(decoder.decode(value)));
}
```

### invoke() vs fetch()

Both models go through the same underlying `WKWebView` → C# bridge with no performance difference:

| Approach | Best for |
|----------|----------|
| `invoke()` + `RegisterInvokeHandler` | Platform calls, one-off operations, named channels |
| `context.Http` + `fetch()` | CRUD data, REST-shaped APIs, streaming responses |

---

## Plugin System

Hot-reloadable C# DLLs loaded from configured plugin directories. The runtime watches directories and reloads changed assemblies without restarting the process — each plugin runs in its own collectible `AssemblyLoadContext` so old code is fully unloaded before the new version takes over.

### Plugin Types

| Interface | Purpose | Thread |
|-----------|---------|--------|
| `ICorePlugin` | App bootstrap, manager initialization, lifecycle wiring | Main (once at startup) |
| `IServicePlugin` | Background work, main-thread services, system integration | Background or main |
| `ILogicPlugin` | Render/compute logic, GPU pipelines, per-window processing | Window render thread |
| `ILibraryPlugin` | Shared code, utilities reused by other plugins | Any |
| `IWindowPlugin` | Full native windows with GPU/Skia rendering | Window render thread |

All five are discovered automatically when their DLL appears in a configured plugin directory. No per-plugin registration in `keystone.json` is required.

### Plugin Directories and Validation

```jsonc
{
  "plugins": {
    "dir": "dylib",
    "userDir": "$APP_SUPPORT/plugins",
    "extensionDir": "$APP_SUPPORT/extensions",
    "allowExternalSignatures": false
  }
}
```

Load order: `dir` → `userDir` → `extensionDir`.

At load and hot-reload time, Keystone validates each plugin binary:

1. If the host app has a macOS `TeamIdentifier`, plugin signature checks are enforced.
2. Plugin must pass `codesign --verify --strict`.
3. If `allowExternalSignatures = false`, plugin team must match the host team.
4. If `allowExternalSignatures = true`, signed plugins from other teams are accepted.

In ad-hoc/unsigned local dev builds, these checks are skipped.

### `IServicePlugin`

Service plugins run independently of any window — background work, long-lived connections, or functionality exposed to other plugins.

```csharp
public interface IServicePlugin
{
    string ServiceName { get; }
    bool RunOnBackgroundThread => false;  // true = background thread
    void Initialize();
    void Shutdown();
}
```

Register from your `ICorePlugin`:

```csharp
context.RegisterService(new ClipboardService());
```

Other code reaches it via `ServiceLocator`:

```csharp
var svc = ServiceLocator.Get<ClipboardService>();
```

### `ILogicPlugin`

Logic plugins are attached to windows and invoked during the render cycle. Window plugins dispatch to them through `LogicRegistry`.

```csharp
public interface ILogicPlugin
{
    string LogicName { get; }
    void Initialize();
    bool RequiresGpu => false;
    int RenderOrder => 0;         // compositing order within the canvas
    IEnumerable<string> Dependencies => Array.Empty<string>();
}
```

**`RenderOrder` convention:**

| Range | Purpose |
|-------|---------|
| `-100` | Deep background |
| `0` | Standard content (default) |
| `100` | Overlays |
| `200` | HUD / debug |

**`RequiresGpu`** — when set, `RenderContext.Gpu` provides `IGpuContext`:

```csharp
public interface IGpuContext
{
    object Device { get; }           // IMTLDevice (macOS) / VkDevice (Linux)
    object Queue { get; }            // IMTLCommandQueue (macOS) / VkQueue (Linux)
    object GraphicsContext { get; }  // GRContext — per-window, NOT thread-safe
    object? ImportTexture(IntPtr textureHandle, int width, int height);
}
```

Cast to `WindowGpuContext` (macOS) or `VulkanGpuContext` (Linux) for strongly-typed access.

### `ILibraryPlugin`

Library plugins exist to be shared across other plugins. When a library plugin changes and reloads, the DyLibLoader performs a **cascade reload**: every plugin that depends on it is also unloaded and reloaded in dependency order.

```csharp
public interface ILibraryPlugin
{
    string LibraryName { get; }
    void Initialize();
}
```

### `IStatefulPlugin` — State Across Hot-Reloads

Any plugin can implement `IStatefulPlugin` to preserve in-memory state across hot-reloads:

```csharp
public interface IStatefulPlugin
{
    byte[] SerializeState();
    void RestoreState(byte[] state);
}
```

When a DLL is about to be unloaded, the runtime calls `SerializeState()`. After loading the new DLL, `RestoreState()` is called on the new instance.

### Hot Reload

When you drop a new DLL into `dylib/` or overwrite an existing one:

1. 200ms debounce after last file change.
2. Identifies plugin type(s) in the assembly.
3. For library/logic plugins, computes the cascade — every dependent plugin is also reloaded in dependency order.
4. Unloads old version — disposes the `AssemblyLoadContext`, runs GC, verifies collection.
5. Loads new version — instantiates plugin types, calls `Initialize()`.

**What survives**: `KeystoneDb` (SQLite), `ServiceLocator`, `IStatefulPlugin` state, native windows (plugin instance swapped), workspace config.

**What resets**: In-memory fields not captured by `IStatefulPlugin`, timers not disposed in `Shutdown()`.

### Registering Custom Plugin Types

```csharp
DyLibLoader.Instance?.RegisterCustomPluginType<IChartPlugin>(
    onLoaded: plugin => ChartRegistry.Register(plugin),
    onUnloaded: name => ChartRegistry.Unregister(name)
);
```

Any DLL in `dylib/` implementing `IChartPlugin` is then discovered, loaded, and hot-reloaded like built-in types.

### Plugin Build Setup

Critical csproj settings:

- **`EnableDynamicLoading`** — required for collectible `AssemblyLoadContext`
- **`<Private>false</Private>`** on engine references — prevents copying engine DLLs into plugin output
- **`CopyLocalLockFileAssemblies: false`** — prevents copying transitive NuGet dependencies

**Standard plugin csproj (macOS):**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-macos</TargetFramework>
    <RuntimeIdentifier>osx-arm64</RuntimeIdentifier>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <AssemblyName>MyPlugin</AssemblyName>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="../../keystone/Keystone.Core/Keystone.Core.csproj"><Private>false</Private></ProjectReference>
    <ProjectReference Include="../../keystone/Keystone.Core.Runtime/Keystone.Core.Runtime.csproj"><Private>false</Private></ProjectReference>
    <ProjectReference Include="../../keystone/Keystone.Core.Management/Keystone.Core.Management.csproj"><Private>false</Private></ProjectReference>
  </ItemGroup>

  <Target Name="PostBuild" AfterTargets="PostBuildEvent">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="../dylib/" />
  </Target>
</Project>
```

**Linux** — same structure but `<TargetFramework>net10.0</TargetFramework>` and `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>`.

**GPU plugins** add:
```xml
<!-- macOS -->
<ProjectReference Include="../../keystone/Keystone.Core.Graphics.Skia/Keystone.Core.Graphics.Skia.csproj">
  <Private>false</Private>
</ProjectReference>
```

**Cross-plugin references:**
```xml
<Reference Include="AppCore">
  <HintPath>../dylib/AppCore.dll</HintPath>
  <Private>false</Private>
</Reference>
```

This creates a dependency edge — `DyLibLoader` cascade-reloads dependent plugins when the referenced DLL changes.

### Plugin Directory Layout

```
dylib/
├── AppCore.dll             # ICorePlugin (initialized first)
├── StreamService.dll       # IServicePlugin
├── RenderLogic.dll         # ILogicPlugin (depends on AppCore)
├── SharedUI.dll            # ILibraryPlugin
├── ContentWindow.dll       # IWindowPlugin (depends on AppCore + SharedUI)
└── native/
    └── libmynative.dylib   # Rust/C native library
```

The `native/` subdirectory is where Rust or C native libraries go. Plugin assemblies using `[DllImport]` are resolved in `dylib/` first, then `dylib/native/`.

---

## Programmatic Bootstrap (`KeystoneApp`)

`KeystoneApp` is a fluent builder API for creating Keystone applications entirely from C# — no `keystone.json` required.

### Minimal Example

```csharp
KeystoneApp.Create("My App", "com.example.myapp")
    .Window("app", w => w.Title("My App").Size(1200, 800))
    .WithBun()
    .Run();
```

### API

| Method | Effect |
|--------|--------|
| `KeystoneApp.Create(name, id)` | Static factory, returns builder |
| `.Window(component, configure?)` | Register a web window |
| `.Window<T>()` | Register a native `IWindowPlugin` by type |
| `.Window(plugin)` | Register a native `IWindowPlugin` instance |
| `.Service<T>()` | Register an `IServicePlugin` by type |
| `.WithBun(root?)` | Enable Bun subprocess (default root: `"bun"`) |
| `.WithPlugins(dir?)` | Enable hot-reload plugin loading (default dir: `"dylib"`) |
| `.RootDir(path)` | Override root directory |
| `.Run()` | Build, initialize, and run. Blocks until shutdown. |

### WindowBuilder

```csharp
app.Window("app", w => w
    .Title("Editor")
    .Size(1200, 800)
    .Toolbar(t => t
        .Button("Save", "editor:save")
        .Separator()
        .Icon("\u2699", "spawn:settings")
    )
);
```

| Method | Effect |
|--------|--------|
| `.Title(string)` | Window title |
| `.Size(float w, float h)` | Initial size in points |
| `.NoSpawn()` | Don't open on launch — register as spawnable type |
| `.Toolbar(configure)` | Add toolbar strip below title bar |

### Full Example — Mixed Web + Native

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

### When to Use What

| Approach | Best for |
|----------|----------|
| `keystone.json` only | Web-first apps, no C# code needed |
| `keystone.json` + `ICorePlugin` | Web + native, config-driven with C# extensions |
| `KeystoneApp` builder | Fully programmatic bootstrap, compiled executables |

`KeystoneApp` and `keystone.json` are not mutually exclusive. If a `keystone.json` exists, `ApplicationRuntime` reads it regardless — `KeystoneApp` provides initial config that gets merged.

### Logging

`KeystoneApp.Run()` automatically tees stdout/stderr to a log file:

- `KEYSTONE_LOG=/path/to/file.log` controls the path
- Default: `$TMPDIR/keystone.log`
