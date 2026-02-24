# Plugin System

Hot-reloadable C# DLLs loaded from configured plugin directories. The runtime watches those directories and reloads changed assemblies without restarting the process — each plugin runs in its own collectible `AssemblyLoadContext` so the old code is fully unloaded before the new version takes over.

There are five plugin types. Each interface serves a distinct purpose.

---

## Plugin Types at a Glance

| Interface | Purpose | Thread |
|-----------|---------|--------|
| `ICorePlugin` | App bootstrap, manager initialization, lifecycle wiring | Main (once at startup) |
| `IServicePlugin` | Background work, main-thread services, system integration | Background or main (your choice) |
| `ILogicPlugin` | Render/compute logic, GPU pipelines, per-window processing | Window render thread |
| `ILibraryPlugin` | Shared code, utilities, and infrastructure reused by other plugins | Any |
| `IWindowPlugin` | Full native windows with Metal/Skia rendering | Window render thread |

All five are discovered automatically when their DLL appears in a configured plugin directory. No per-plugin registration in `keystone.json` is required.

---

## Plugin Directories and Validation

Configure plugin locations under `plugins` in `keystone.json`:

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

Load order is deterministic: `dir` -> `userDir` -> `extensionDir`.

At load and hot-reload time, Keystone validates each plugin binary before unloading the old version:

1. If the host app has a macOS `TeamIdentifier`, plugin signature checks are enforced.
2. Plugin must pass `codesign --verify --strict`.
3. Plugin must expose a `TeamIdentifier`.
4. If `allowExternalSignatures = false`, plugin team must match the host team.
5. If `allowExternalSignatures = true`, signed plugins from other teams are accepted.

In ad-hoc/unsigned local dev builds (host has no `TeamIdentifier`), these checks are skipped.

If validation fails, the new DLL is rejected and the previously loaded plugin remains active.

---

## `ICorePlugin` — App Bootstrap

Your app's entry point. The runtime loads it from the path specified by `appAssembly` in `keystone.json` and calls `Initialize(ICoreContext)` once, after framework startup, before any windows spawn.

```csharp
public interface ICorePlugin
{
    string CoreName { get; }
    void Initialize(ICoreContext context);
}
```

Use this to initialize singletons, register services into `ServiceLocator`, wire lifecycle events, and configure state. Everything the app needs should be bootstrapped here.

The `ICorePlugin` assembly is **not** a hot-reload plugin — it is tightly coupled to the framework binary and loaded once at startup via `appAssembly`. It should never live in `dylib/` alongside the hot-reloadable plugins.

### Example — app bootstrap

```csharp
public class MyApp : ICorePlugin
{
    public string CoreName => "MyApp";

    public void Initialize(ICoreContext context)
    {
        // Register services into ServiceLocator so other plugins can reach them
        ServiceLocator.Register(ApplicationRuntime.Instance!.WindowManager);

        // Touch singletons so they're alive before plugins load
        _ = AssetManager.Instance;
        _ = TaskManager.Instance;

        // Handle app-specific actions
        context.OnUnhandledAction = (action, source) =>
        {
            if (action == "toggle_mode")
                AppMode.Toggle();
        };

        // Cleanup on shutdown
        context.OnShutdown += () =>
        {
            TaskManager.Instance.Dispose();
        };
    }
}
```

### Loading via `appAssembly`

```jsonc
// keystone.json
{
  "appAssembly": "dylib/MyApp.Core.dll"   // path relative to keystone.json
}
```

The path is relative to the app root (where `keystone.json` lives). In dev side-by-side layout, both the app assembly and plugins live in `dylib/` — the framework loads the `ICorePlugin` first via `appAssembly`, then discovers the remaining plugin types from the same directory.

---

## `IServicePlugin` — Background and Main-Thread Services

Service plugins run independently of any window. They're the right choice for anything that needs to run continuously in the background, own a long-lived connection, or expose functionality to other plugins and invoke handlers.

```csharp
public interface IServicePlugin
{
    string ServiceName { get; }
    bool RunOnBackgroundThread => false;  // true = background thread, false = main thread
    void Initialize();
    void Shutdown();
}
```

**`RunOnBackgroundThread`** controls which thread the service lives on. Most services want `true` — they do I/O, timers, or polling work that should not block the AppKit run loop. Set `false` only if the service needs to be called from the main thread by other plugins.

### Example — clipboard monitor

```csharp
public class ClipboardService : IServicePlugin
{
    public string ServiceName => "clipboard";
    public bool RunOnBackgroundThread => true;

    private Timer? _timer;

    public void Initialize()
    {
        ServiceLocator.Register(this);

        // Poll for clipboard changes every 500ms
        _timer = new Timer(_ =>
        {
            string? text = null;
            NSApplication.SharedApplication.InvokeOnMainThread(() =>
                text = NSPasteboard.GeneralPasteboard.GetStringForType(NSPasteboardType.String));

            if (text != null)
                BunManager.Instance?.Push("clipboard:change", new { text });

        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    public void Shutdown()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public string? Read()
    {
        string? text = null;
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
            text = NSPasteboard.GeneralPasteboard.GetStringForType(NSPasteboardType.String));
        return text;
    }
}
```

### State across hot-reloads

Implement `IReloadableService` to preserve state when the plugin is reloaded:

```csharp
public class MyService : IReloadableService
{
    public string ServiceName => "my-service";
    public bool RunOnBackgroundThread => true;

    private int _counter;

    public void Initialize() { }
    public void Shutdown() { }

    public byte[]? SerializeState() =>
        System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(_counter));

    public void RestoreState(byte[]? state)
    {
        if (state == null) return;
        _counter = System.Text.Json.JsonSerializer.Deserialize<int>(state);
    }
}
```

---

## `ILogicPlugin` — Render and Compute Plugins

Logic plugins are attached to windows and invoked during the render cycle. Window plugins call the logic plugin's render methods from their own render path. They're the right tool for GPU compute, custom render passes, or anything that processes per-frame data.

```csharp
public interface ILogicPlugin
{
    string LogicName { get; }
    void Initialize();
    bool RequiresGpu => false;    // true = Metal compute resources available
    int RenderOrder => 0;         // compositing order within the window canvas
    IEnumerable<string> Dependencies => Array.Empty<string>();
}
```

**`RenderOrder`** controls layer ordering within the render canvas. Lower values render first (background). The convention:

| Range | Purpose |
|-------|---------|
| `-100` | Deep background |
| `0` | Standard content (default) |
| `100` | Overlays |
| `200` | HUD / debug |

**`RequiresGpu`** signals that this plugin will use Metal compute shaders or GPU buffers. When set, `RenderContext.Gpu` provides `IGpuContext` — cast to `WindowGpuContext` for the concrete Metal device and command queue.

**`Dependencies`** — data source keys this plugin consumes. Apps define their own dependency key convention.

### GPU Compute Access

`RenderContext.Gpu` exposes `IGpuContext`, which provides object-typed accessors to the per-window Metal state:

```csharp
public interface IGpuContext
{
    object Device { get; }           // IMTLDevice — shared, thread-safe
    object Queue { get; }            // IMTLCommandQueue — per-window
    object GraphicsContext { get; }  // GRContext — per-window, NOT thread-safe
    object? ImportTexture(IntPtr textureHandle, int width, int height);  // Metal → Skia
}
```

Cast to `WindowGpuContext` for strongly-typed access:

```csharp
if (ctx.Gpu is not WindowGpuContext gpu) return;

// Now you have:
// gpu.Device     — IMTLDevice
// gpu.Queue      — IMTLCommandQueue
// gpu.GRContext  — GRContext (Skia)
// gpu.CreateImageFromTexture(handle, w, h) — import Metal texture into Skia
```

From there, the plugin decides how to organize its GPU state — embedded Metal shader strings, persistent buffers, per-window compute pipelines, etc. Since logic plugins are attached to a window, per-window state management is natural (static dictionary keyed by window ID, or instance fields if using per-window plugin instances).

### Example — GPU downsampler

```csharp
public class LineRenderer : ILogicPlugin
{
    public string LogicName => "lines";
    public bool RequiresGpu => true;
    public int RenderOrder => 0;

    static readonly ConcurrentDictionary<string, GpuState> _state = new();

    public void Initialize() { }

    public static void Render(RenderContext ctx, string windowId, float x, float y, float w, float h)
    {
        if (ctx.Gpu is not WindowGpuContext gpu) return;

        var state = _state.GetOrAdd(windowId, _ => new GpuState(gpu.Device));
        // ... dispatch compute, read back results, draw with ctx ...
    }
}

class GpuState : IDisposable
{
    public readonly IMTLComputePipelineState Pipeline;
    public IMTLBuffer? OutputBuffer;

    const string Shader = @"
#include <metal_stdlib>
using namespace metal;
kernel void downsample(...) { /* ... */ }
";

    public GpuState(IMTLDevice device)
    {
        var lib = device.CreateLibrary(Shader, new MTLCompileOptions(), out _);
        Pipeline = device.CreateComputePipelineState(lib!.CreateFunction("downsample")!, out _);
    }

    public void Dispose() { OutputBuffer?.Dispose(); }
}
```

---

## `ILibraryPlugin` — Shared Code and Utilities

Library plugins don't do work on their own — they exist to be shared across other plugins. When a library plugin changes and reloads, the DyLibLoader performs a **cascade reload**: every plugin that depends on that library is also unloaded and reloaded in the correct order, automatically.

```csharp
public interface ILibraryPlugin
{
    string LibraryName { get; }
    void Initialize();
}
```

This is the right pattern for:
- Shared data models used by multiple service or logic plugins
- Utility classes (color math, geometry, parsers) that multiple plugins import
- Common infrastructure (a database client, an HTTP session) that you want to initialize once and share

### Example — shared theme library

```csharp
// SharedTheme.dll — depends on nothing, other plugins depend on this
public class SharedThemeLibrary : ILibraryPlugin
{
    public string LibraryName => "shared-theme";

    public static SharedThemeLibrary? Instance { get; private set; }
    public SKColor Accent { get; private set; } = SKColors.CornflowerBlue;

    public void Initialize()
    {
        Instance = this;
        var stored = KeystoneDb.Instance?.GetString("theme:accent");
        if (stored != null) Accent = SKColor.Parse(stored);
    }

    public void SetAccent(SKColor color)
    {
        Accent = color;
        KeystoneDb.Instance?.SetString("theme:accent", color.ToString());
    }
}
```

Any other plugin that references `SharedTheme.dll` in its csproj will automatically cascade-reload when `SharedTheme.dll` changes.

---

## `IWindowPlugin` — Native Windows

Window plugins render full Metal/Skia windows. They run on the window's dedicated render thread, called at vsync. See [C# App Layer](./csharp-app-layer.md) for the full `IWindowPlugin` and `WindowPluginBase` reference, scene graph API, hit testing, workspace serialization, and immediate-mode Skia usage.

---

## `IStatefulPlugin` — State Across Hot-Reloads

Any plugin can implement `IStatefulPlugin` to preserve in-memory state across hot-reloads. This is separate from `IReloadableService` (for services) and `SerializeConfig`/`RestoreConfig` (for workspace persistence) — `IStatefulPlugin` is for ephemeral state that matters during development but shouldn't be written to disk.

```csharp
public interface IStatefulPlugin
{
    byte[] SerializeState();
    void RestoreState(byte[] state);
}
```

When a DLL is about to be unloaded, the runtime calls `SerializeState()` on the old instance. After loading the new DLL, `RestoreState()` is called on the new instance with the saved bytes.

### Example — preserving chart scroll position across reload

```csharp
public class ChartWindow : WindowPluginBase, IStatefulPlugin
{
    public override string WindowType => "chart";

    private float _scrollOffset;
    private float _zoomLevel = 1.0f;

    public byte[] SerializeState()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(_scrollOffset);
        bw.Write(_zoomLevel);
        return ms.ToArray();
    }

    public void RestoreState(byte[] state)
    {
        using var ms = new MemoryStream(state);
        using var br = new BinaryReader(ms);
        _scrollOffset = br.ReadSingle();
        _zoomLevel = br.ReadSingle();
    }
}
```

---

## Directory Layout

```
dylib/
├── AppCore.dll             # ICorePlugin (auto-discovered, initialized first)
├── StreamService.dll       # IServicePlugin
├── RenderLogic.dll         # ILogicPlugin (GPU compute, depends on AppCore)
├── SharedUI.dll            # ILibraryPlugin (depended on by window plugins)
├── ContentWindow.dll       # IWindowPlugin (depends on AppCore + SharedUI)
└── native/
    └── libmynative.dylib   # Rust/C native library loaded via [DllImport]
```

The `native/` subdirectory is where Rust or C dylibs go. Plugin assemblies that use `[DllImport]` are resolved in `dylib/` first, then `dylib/native/`.

---

## Hot Reload

When you drop a new DLL into `dylib/` or overwrite an existing one, the runtime:

1. Waits 200ms after the last file change (debounce).
2. Identifies the plugin type(s) in the assembly.
3. If it's a library or logic plugin, computes the cascade: every plugin that depends on it is also reloaded in dependency order.
4. Unloads the old version — disposes the `AssemblyLoadContext`, runs GC, verifies the ALC is collected (warns if not after 10s — indicates a pinned reference somewhere).
5. Loads the new version — instantiates plugin types, calls `Initialize()`.

Cascade example: `SharedButtons.dll` changes → `ChartWindow.dll` (which depends on it) is automatically unloaded and reloaded after.

### What survives a reload

- Anything stored in `KeystoneDb` (SQLite) or `ServiceLocator`
- State explicitly preserved via `IStatefulPlugin` or `IReloadableService`
- Native windows — they keep rendering; the plugin instance is just swapped
- Workspace config via `SerializeConfig`/`RestoreConfig`

### What resets on reload

- In-memory state (local variables, fields) not captured by `IStatefulPlugin`
- Timers not disposed in `Shutdown()`
- Active subscriptions not cleaned up

Always implement `Shutdown()` to cancel timers and release subscriptions.

---

## Registering Custom Plugin Types

If you have a plugin interface specific to your app (e.g. `IChartPlugin`), you can hook into the same hot-reload discovery system:

```csharp
// In ICorePlugin.Initialize()
DyLibLoader.Instance?.RegisterCustomPluginType<IChartPlugin>(
    onLoaded: plugin => ChartRegistry.Register(plugin),
    onUnloaded: name => ChartRegistry.Unregister(name)
);
```

From this point, any DLL in `dylib/` that implements `IChartPlugin` is discovered, loaded, and hot-reloaded exactly like the built-in types.

---

## Build Setup

A plugin csproj targets `net10.0-macos` with `osx-arm64` and references the engine projects. Critical settings:

- **`EnableDynamicLoading`** — required for collectible `AssemblyLoadContext` (hot-reload)
- **`<Private>false</Private>`** on engine references — prevents copying engine DLLs into plugin output
- **`CopyLocalLockFileAssemblies: false`** — prevents copying transitive NuGet dependencies
- **`AllowUnsafeBlocks`** — needed for plugins touching GPU buffers or native memory

### Standard plugin csproj

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

  <!-- Engine project references — Private=false prevents copying engine DLLs -->
  <ItemGroup>
    <ProjectReference Include="../../keystone/Keystone.Core/Keystone.Core.csproj"><Private>false</Private></ProjectReference>
    <ProjectReference Include="../../keystone/Keystone.Core.Runtime/Keystone.Core.Runtime.csproj"><Private>false</Private></ProjectReference>
    <ProjectReference Include="../../keystone/Keystone.Core.Management/Keystone.Core.Management.csproj"><Private>false</Private></ProjectReference>
  </ItemGroup>

  <!-- Copy built DLL to dylib/ for hot-reload discovery -->
  <Target Name="PostBuild" AfterTargets="PostBuildEvent">
    <Copy SourceFiles="$(TargetPath)" DestinationFolder="../dylib/" />
  </Target>
</Project>
```

### Additional references for GPU plugins

```xml
  <ProjectReference Include="../../keystone/Keystone.Core.Graphics.Skia/Keystone.Core.Graphics.Skia.csproj">
    <Private>false</Private>
  </ProjectReference>
```

### Referencing other app-level plugins

Plugins that depend on app code (e.g. a core plugin's types) reference the built DLL:

```xml
  <Reference Include="AppCore">
    <HintPath>../dylib/AppCore.dll</HintPath>
    <Private>false</Private>
  </Reference>
```

This creates a dependency edge — `DyLibLoader` tracks it and cascade-reloads dependent plugins when the referenced DLL changes.

### Build order

Build your core plugin first (all other plugins depend on it), then libraries (windows may depend on some), then logic/service/window plugins in parallel.

---

## Next

- [C# App Layer](./csharp-app-layer.md) — `ICorePlugin`, native windows, invoke handlers
- [Configuration Reference](./configuration.md) — `plugins` block in `keystone.json`
- [Process Model](./process-model.md) — where plugins fit in the three-process architecture
