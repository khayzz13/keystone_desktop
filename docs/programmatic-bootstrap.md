# Programmatic Bootstrap (`KeystoneApp`)

`KeystoneApp` is a fluent builder API for creating Keystone applications entirely from C# — no `keystone.json` required. It handles the full bootstrap sequence: platform initialization, runtime creation, plugin registration, run loop, and shutdown.

Use it when you want full programmatic control over app configuration, or when building a compiled executable where a JSON config file is unnecessary.

---

## Minimal Example

```csharp
KeystoneApp.Create("My App", "com.example.myapp")
    .Window("app", w => w.Title("My App").Size(1200, 800))
    .WithBun()
    .Run();
```

This is equivalent to:

```json
{
  "name": "My App",
  "id": "com.example.myapp",
  "windows": [{ "component": "app", "title": "My App", "width": 1200, "height": 800 }],
  "bun": { "root": "bun" }
}
```

`.Run()` blocks until the application shuts down.

---

## API Reference

### `KeystoneApp.Create(name, id)`

Static factory. Returns a new `KeystoneApp` builder.

```csharp
var app = KeystoneApp.Create("Dashboard", "com.example.dashboard");
```

### `.Window(component, configure?)`

Register a web window. The `component` name maps to `bun/web/{component}.ts`. The optional configure callback exposes a `WindowBuilder` for setting title, size, toolbar, and spawn behavior.

```csharp
app.Window("app", w => w
    .Title("Main Window")
    .Size(1400, 900)
);

// Multiple windows
app.Window("settings", w => w
    .Title("Settings")
    .Size(600, 400)
    .NoSpawn()           // don't open on launch — open via nativeWindow.open("settings")
);
```

### `.Window<T>()`

Register a native `IWindowPlugin` by type. The plugin is instantiated and registered before windows spawn.

```csharp
app.Window<DashboardWindow>();
app.Window<ChartWindow>();
```

### `.Window(plugin)`

Register a native `IWindowPlugin` instance. Use when the plugin needs constructor arguments.

```csharp
app.Window(new EditorWindow(config));
```

### `.Service<T>()`

Register an `IServicePlugin` by type.

```csharp
app.Service<DatabaseService>();
```

### `.WithBun(root)`

Enable the Bun subprocess. `root` defaults to `"bun"` — the directory containing `host.ts`, `keystone.config.ts`, `web/`, and `services/`.

```csharp
app.WithBun();           // default: "bun"
app.WithBun("runtime");  // custom directory
```

### `.WithPlugins(dir)`

Enable hot-reloadable plugin loading from a directory. Defaults to `"dylib"`.

```csharp
app.WithPlugins();           // default: "dylib"
app.WithPlugins("plugins");  // custom directory
```

### `.RootDir(path)`

Override the root directory. By default, the runtime walks up from the executable looking for `keystone.json` or a `dylib/` folder. Use this when your app structure doesn't follow the standard layout.

```csharp
app.RootDir("/Users/me/projects/myapp");
```

### `.Run()`

Build, initialize, and run the application. Blocks until shutdown. Handles:

1. Log file setup (`KEYSTONE_LOG` env var or `$TMPDIR/keystone.log`)
2. Root directory resolution
3. Platform initialization (AppKit+Metal on macOS, GTK4+Vulkan on Linux)
4. `ApplicationRuntime` creation and plugin registration
5. Main run loop
6. Graceful shutdown on exit or fatal error

---

## WindowBuilder

The `WindowBuilder` configures a web window declaration:

| Method | Effect |
|--------|--------|
| `.Title(string)` | Window title |
| `.Size(float w, float h)` | Initial size in points |
| `.NoSpawn()` | Don't open on launch — register as a spawnable type |
| `.Toolbar(configure)` | Add a toolbar strip below the title bar |

### Toolbar

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

---

## Full Example — Mixed Web + Native

```csharp
KeystoneApp.Create("Trading Platform", "com.example.trading")
    // Web windows (TypeScript UI)
    .Window("dashboard", w => w.Title("Dashboard").Size(1400, 900))
    .Window("settings", w => w.Title("Settings").Size(600, 400).NoSpawn())

    // Native GPU windows (C# + Skia)
    .Window<ChartWindow>()
    .Window<OrderBookWindow>()

    // Services
    .Service<MarketDataService>()

    // Bun layer for web components + services
    .WithBun()

    // Hot-reloadable plugins
    .WithPlugins()

    .Run();
```

---

## When to Use What

| Approach | Best for |
|----------|----------|
| `keystone.json` only | Web-first apps, no C# code needed |
| `keystone.json` + `ICorePlugin` | Web + native, config-driven with C# extensions |
| `KeystoneApp` builder | Fully programmatic bootstrap, compiled executables, apps that compute their window config at startup |

`KeystoneApp` and `keystone.json` are not mutually exclusive. If a `keystone.json` exists in the root directory, `ApplicationRuntime` reads it regardless — `KeystoneApp` just provides the initial config that gets merged with or replaced by the file.

---

## Logging

`KeystoneApp.Run()` automatically tees stdout and stderr to a log file:

- Set `KEYSTONE_LOG=/path/to/file.log` to control the log path
- Default: `$TMPDIR/keystone.log`
- Console output is preserved — the tee writer sends to both console and file

---

## Next

- [Getting Started](./getting-started.md) — config-file-based project setup
- [C# App Layer](./csharp-app-layer.md) — `ICorePlugin`, custom invoke handlers, native windows
- [Configuration Reference](./configuration.md) — full `keystone.json` schema
- [Plugin System](./plugin-system.md) — hot-reloadable DLL plugins
