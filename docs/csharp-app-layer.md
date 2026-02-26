# C# App Layer

The C# app layer is Keystone's equivalent of Electron's main process — written in C#. You get the full .NET 10 standard library, direct P/Invoke to any platform framework (AppKit/Metal on macOS, GTK4/Vulkan on Linux), and native threads with real memory ownership.

The app layer is **optional**. If the built-in invoke handlers cover your needs (file dialogs, window management, path queries), skip it. When you do need it, implement `ICorePlugin` in a class library, build it to `dylib/`, and point `appAssembly` in `keystone.json` at the output DLL. The framework loads it once at startup — it is not a hot-reload plugin.

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

---

## ICoreContext Reference

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

    // HTTP-style route router (optional convenience layer)
    IHttpRouter Http { get; }
}
```

---

## Custom Invoke Handlers

`invoke()` from the TypeScript side resolves to a named handler registered on `ManagedWindow`. Built-in handlers cover `app:*`, `window:*`, `dialog:*`, and `shell:*`. Register your own for anything custom.

The handler signature is `Func<JsonElement, Task<object?>>`:
- `JsonElement` — the args object from JS (may be default if no args passed)
- Return value is JSON-serialized and sent as `reply.result`
- Throw an `Exception` to send `reply.error` (rejects the JS promise)

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
// TypeScript
const content = await invoke<string>("myapp:readFile", { path: "/etc/hosts" });
```

### Handler Threading

Handlers run on a thread pool thread, not the main thread:

- Async I/O works naturally — `await File.ReadAllTextAsync(...)`, `await httpClient.GetAsync(...)`.
- Anything that needs platform UI APIs (showing panels, modifying windows) must dispatch to the main thread.
- The handler never blocks the run loop.

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
    // source is a window ID or "menu"
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
// TypeScript — dispatch custom actions
action("myapp:new-document");
action("myapp:export-pdf");
```

---

## Native Windows (GPU/Skia)

When you need maximum rendering performance or want to bypass the web layer, implement `IWindowPlugin`. The renderer calls `BuildScene()` or `Render()` on a per-window background thread at vsync.

### WindowPluginBase

Most windows extend `WindowPluginBase`, which provides default implementations and wiring:

```csharp
public abstract class WindowPluginBase : IWindowPlugin
{
    public abstract string WindowType { get; }
    public virtual string WindowTitle => WindowType;
    public virtual (float Width, float Height) DefaultSize => (800, 600);
    public virtual PluginRenderPolicy RenderPolicy => PluginRenderPolicy.Continuous;
    public virtual IEnumerable<string> Dependencies => Array.Empty<string>();

    public uint WindowId { get; set; }  // Set by ManagedWindow before render

    public abstract void Render(RenderContext ctx);
    public virtual SceneNode? BuildScene(FrameState state) => null;
    public virtual HitTestResult? HitTest(float x, float y, float w, float h) => null;

    // Workspace persistence
    public virtual string? SerializeConfig() => null;
    public virtual void RestoreConfig(string json) { }
    public virtual bool ExcludeFromWorkspace => false;

    // Overlay system — wired by ManagedWindow
    public Action<IOverlayContent, double, double>? ShowOverlay { get; set; }
    public Action? CloseOverlay { get; set; }
}
```

### Scene Graph API (recommended)

`BuildScene()` returns a retained scene graph. The renderer diffs and caches geometry between frames — no GPU re-upload unless something changes.

```csharp
public class DashboardWindow : WindowPluginBase
{
    public override string WindowType => "dashboard";
    public override (float, float) DefaultSize => (1400, 900);

    private float _cpuPercent;

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
}
```

### Immediate-mode API

`Render()` gives you a raw Skia canvas via `RenderContext`. Use for custom drawing that the scene graph doesn't express.

```csharp
public override void Render(RenderContext ctx)
{
    using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    ctx.Canvas.DrawCircle(ctx.Width / 2f, ctx.Height / 2f, 100, paint);
    ctx.RequestRedraw();
}
```

### Hit Testing

For input handling on native windows, implement `HitTest`. Called on mouse down (to resolve actions) and mouse move (to update the cursor).

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
// App-side delegate (e.g. TradingCore/LogicDelegates.cs)
public delegate void ChartRenderDelegate(
    RenderContext ctx, float x, float y, float w, float h,
    string windowId, ChartViewState state);
```

**Single named plugin:**
```csharp
LogicRegistry.Dispatch<ChartRenderDelegate>("sessions", "Render",
    del => del(ctx, x, y, w, h, windowId, chartState));
```

**All plugins in compositor order** (sorted by `ILogicPlugin.RenderOrder`):
```csharp
LogicRegistry.DispatchAll<ChartRenderDelegate>("Render",
    del => del(ctx, x, y, w, h, windowId, chartState));
```

**Subset by RenderOrder range** (background, content, overlays, HUD):
```csharp
// Only background layer plugins (RenderOrder -100 to 0)
LogicRegistry.DispatchRange<ChartRenderDelegate>("Render", -100, 0,
    del => del(ctx, x, y, w, h, windowId, chartState));
```

The delegate is created once via reflection on first call, then cached. Subsequent frames hit `ConcurrentDictionary.TryGetValue` + invoke. Hot-reload invalidates per-plugin cache entries automatically.

For logic plugins that don't need typed dispatch (initialization, one-off queries), the reflection fallback still works:
```csharp
LogicRegistry.Invoke("pluginName", "MethodName", arg1, arg2);
```

### Scroll and Keyboard Input

Window plugins can receive scroll and keyboard events directly:

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

`WindowPluginBase` provides `ShowOverlay` and `CloseOverlay` for floating overlay windows (dropdowns, panels, color pickers). Overlays implement `IOverlayContent` — a simpler interface than `IWindowPlugin`.

```csharp
var dropdown = new SymbolDropdown(symbols);
ShowOverlay?.Invoke(dropdown, anchorX, anchorY);
// ...
CloseOverlay?.Invoke();
```

---

## Workspace Serialization

Windows can persist their configuration across sessions via `SerializeConfig`/`RestoreConfig`. The engine calls these during workspace save/load.

```csharp
public override string? SerializeConfig()
{
    var view = GetViewState();
    return JsonSerializer.Serialize(new LayoutConfig
    {
        FilePath  = view.FilePath,
        ScrollY   = view.ScrollY,
        FontSize  = view.FontSize,
        ViewMode  = view.ViewMode
    });
}

public override void RestoreConfig(string json)
{
    var layout = JsonSerializer.Deserialize<LayoutConfig>(json);
    if (layout == null) return;
    ApplyLayout(layout);
}
```

Set `ExcludeFromWorkspace => true` on windows that shouldn't be persisted (transient panels, debug windows).

This is distinct from `IStatefulPlugin` which preserves ephemeral in-memory state across hot-reloads. `SerializeConfig` is for user-facing state written to disk.

---

## State Across Hot-Reloads

Implement `IStatefulPlugin` on any plugin to preserve transient state during development reloads:

```csharp
public class ChartWindow : WindowPluginBase, IStatefulPlugin
{
    private float _scrollOffset;
    private float _priceScale = 1.0f;
    private bool _freeCam;

    public byte[] SerializeState()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(_scrollOffset);
        bw.Write(_priceScale);
        bw.Write(_freeCam);
        return ms.ToArray();
    }

    public void RestoreState(byte[] state)
    {
        using var ms = new MemoryStream(state);
        using var br = new BinaryReader(ms);
        _scrollOffset = br.ReadSingle();
        _priceScale = br.ReadSingle();
        _freeCam = br.ReadBoolean();
    }
}
```

See [Plugin System — IStatefulPlugin](./plugin-system.md#istatefulplugin--state-across-hot-reloads) for more details.

---

## Registering a C#-side Service

`IServicePlugin` is for native services that need platform APIs or tight coupling to the rendering pipeline. See [Plugin System](./plugin-system.md) for the full plugin type reference.

Register from your `ICorePlugin`:

```csharp
context.RegisterService(new ClipboardService());
```

Other code reaches it via `ServiceLocator`:

```csharp
var svc = ServiceLocator.Get<ClipboardService>();
```

---

## Lifecycle Events

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

---

## Process Recovery Hooks

```csharp
context.OnBunCrash += exitCode =>
{
    // Bun exited — auto-restart already scheduled per processRecovery config
    Logger.Warn($"Bun exited with code {exitCode}");
};

context.OnBunRestart += attempt =>
{
    Logger.Info($"Bun recovered (attempt {attempt})");
};

context.OnWebViewCrash += windowId =>
{
    // WebKit content process terminated — auto-reload already scheduled
    Logger.Warn($"WebView crashed in window {windowId}");
};
```

Recovery behavior is configured in `keystone.json` — see [Process Model](./process-model.md#process-isolation-benefits).

---

## HTTP Router

`context.Http` provides a REST-style route API as an alternative to raw invoke handlers. Routes are intercepted from `fetch("/api/...")` in the browser — no real HTTP server involved.

```csharp
context.Http
    .Get("/api/notes", async req =>
    {
        var notes = await db.GetAllAsync();
        return HttpResponse.Json(notes);
    })
    .Post("/api/notes", async req =>
    {
        var note = req.Body<NoteDto>();
        await db.InsertAsync(note);
        return HttpResponse.Json(new { ok = true });
    });
```

```typescript
// Browser
const notes = await fetch("/api/notes").then(r => r.json());
```

See [HTTP Router](./http-router.md) for the full reference.

---

## Bun Bridge from C#

`context.Bun` exposes `IBunService` — push to channels, query Bun services, or eval JS.

```csharp
// Push data to all subscribers on a channel
context.Bun.Push("data:prices", new { btc = 65000.0, eth = 3200.0 });

// Query a Bun service
var result = await context.Bun.Query("file-scanner", new { dir = "/tmp" });
using var doc = JsonDocument.Parse(result!);
var count = doc.RootElement.GetProperty("count").GetInt32();
```

---

## Skipping C# Entirely

If the built-in invoke surface is sufficient, omit the C# app layer. Don't build any DLL with `ICorePlugin`. The runtime starts with only the built-in handlers.

Right choice for apps that:
- Only need file dialogs, path queries, and window management
- Do all logic in Bun services
- Treat the native layer as a display surface only

---

## Next

- [Plugin System](./plugin-system.md) — hot-reloadable DLL plugins (service, logic, library, window types)
- [Native API Reference](./native-api.md) — built-in `invoke` handler reference
- [HTTP Router](./http-router.md) — REST-style API convenience layer
- [Bun Services](./bun-services.md) — TypeScript background services
- [Process Model](./process-model.md) — process isolation and crash recovery
