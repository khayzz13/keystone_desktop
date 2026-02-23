# Getting Started

## Requirements

- macOS 15+ (Apple Silicon)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Bun](https://bun.sh) — `curl -fsSL https://bun.sh/install | bash`
- Rust toolchain — `curl --proto '=https' --tlsv1.2 -sSf https://sh.rustup.rs | sh`

---

## Project Structure

A Keystone app is a directory with a `keystone.json` manifest and whatever layers you need:

```
my-app/
├── keystone.json           # App manifest — the only required file
├── build.py                # Build and run script
├── bun/                    # TypeScript layer (optional)
│   ├── package.json
│   ├── tsconfig.json
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

---

## The App Manifest

`keystone.json` is read by the runtime before anything else starts.

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

---

## Build and Run

Each app ships its own `build.py` that knows its specific plugin layout and build order. A minimal one builds C# projects and invokes the framework's packager:

```bash
cd my-app
python3 build.py           # build plugins into dylib/
python3 build.py --package # build + produce MyApp.app in dist/
```

Common flags apps expose:

| Flag | Effect |
|------|--------|
| `--package` | Produce a `.app` bundle in `dist/` after building |
| `--bundle` | With `--package`: copy `dylib/` into the bundle (self-contained) |
| `--allow-external` | Allow plugins not signed by this app (disables library validation) |
| `--debug` | Debug configuration |
| `--clean` | Remove `bin/`/`obj/` before building |

The framework's own `build.py` (at `keystone/build.py`) is invoked internally by the app's `--package` mode. You generally don't call it directly unless building the framework itself.

See `python3 build.py --help` for your app's full option list.

---

## Your First Component

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

The `--ks-*` CSS custom properties are theme tokens applied to `:root` by the bridge. See [Web Components](./web-components.md#theme-tokens) for the full token list.

---

## Using the Bridge

`@keystone/sdk/bridge` is the connector between your TypeScript UI and the rest of the system.

```typescript
import { keystone, action, invoke, subscribe, query, app, dialog, nativeWindow, shell } from "@keystone/sdk/bridge";
```

### Talking to C# — `invoke()`

`invoke()` sends a request directly to a C# handler and returns a Promise. No Bun round-trip — uses `WKScriptMessageHandler` for minimal latency.

```typescript
// Built-in handlers
const name = await app.getName();
const paths = await dialog.openFile({ filters: [".ts", ".js"] });

// Custom C# handlers
const outPath = await invoke<string>("myapp:compress", { path: filePath });
```

### Talking to Bun — `subscribe()` and `query()`

```typescript
// Live CPU usage pushed every second from a Bun service
const unsub = subscribe("metrics:cpu", (data) => {
  cpuLabel.textContent = `${data.percent}%`;
});
// Call unsub() to stop

// One-time query
const files = await query("file-scanner", { dir: "/Users/me/Documents" });
```

### Native window actions — `action()`

```typescript
nativeWindow.minimize();
nativeWindow.close();
action("app:quit");
action("myapp:new-document");  // handled by your ICorePlugin
```

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

TypeScript UI + Bun services + C# app layer. Register custom `invoke()` handlers, control window lifecycle, use any macOS API. Required for anything beyond the built-in API surface.

Add an `app/` directory with a `.csproj` and implement `ICorePlugin`. `build.py` detects the csproj automatically.

### Pure Native (C# only)

Metal/Skia rendering with no WebView, no Bun. Maximum performance — every pixel rendered by your plugin in the GPU pipeline.

```json
{
  "name": "Pure Native",
  "id": "com.example.native"
}
```

```csharp
public class MainWindow : IWindowPlugin
{
    public string WindowType => "main";
    public (float Width, float Height) DefaultSize => (1200, 800);

    public void Render(RenderContext ctx)
    {
        ctx.Canvas.DrawText("Hello from Skia", 40, 40, paint);
    }
}
```

---

## What's Running at Runtime

When you run a web-mode app:

1. C# host starts. Reads `keystone.json`.
2. Bun subprocess spawns. `host.ts` discovers services and web components.
3. Bun writes a ready signal to stdout: `{ "status": "ready", "port": 3847, ... }`.
4. C# reads the ready signal. `BunManager` attaches.
5. First window spawns. C# creates the `NSWindow` + `CAMetalLayer`.
6. Render thread starts. Metal/Skia draws native chrome if enabled.
7. A `WKWebView` is created. Loads `/__host__` from Bun.
8. The host page loads your `app.ts` component. `mount(root)` is called.
9. The bridge `keystone()` client initializes — WebSocket connects, theme tokens apply.

---

## Next

- [Process Model](./process-model.md) — how the three processes relate
- [Web Components](./web-components.md) — component lifecycle, HMR, slots
- [Bun Services](./bun-services.md) — background services and data channels
- [C# App Layer](./csharp-app-layer.md) — native code, custom invoke handlers, plugin lifecycle
- [Plugin System](./plugin-system.md) — hot-reloadable DLL plugins
- [Window Chrome](./window-chrome.md) — controlling the native window surface
- [Native API Reference](./native-api.md) — full `invoke`, `action`, `dialog`, `shell` reference
