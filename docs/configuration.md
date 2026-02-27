# Configuration, Window Chrome & Build

> Last updated: 2026-02-26

Keystone uses two configuration files with different responsibilities:

| File | Scope | Read by |
|------|-------|---------|
| `keystone.config.json` (fallback: `keystone.json`) | App identity, windows, plugins, C# runtime | C# host process |
| `bun/keystone.config.ts` | Bun runtime: services, bundling, HMR, security | Bun subprocess |

Both are optional — the runtime has sensible defaults for everything.

---

## `keystone.config.json` / `keystone.json`

Lives at the root of your app directory. The host searches `keystone.config.json` first, then `keystone.json`.

### Full Schema

```jsonc
{
  // App identity — required
  "name": "My App",
  "id": "com.example.myapp",
  "version": "1.0.0",

  // Windows to register and optionally spawn on launch
  "windows": [
    {
      "component": "app",     // maps to bun/web/app.ts
      "title": "My App",
      "width": 1024,
      "height": 700,
      "spawn": true,          // open this window on launch
      "titleBarStyle": "hidden",  // "hidden" | "toolkit" | "toolkit-native" | "none"
      "floating": false,      // always-on-top (default: false)
      "renderless": false     // skip GPU/Skia entirely for web-only windows (default: false)
    }
  ],

  // Network endpoint allow-list — controls outbound fetch in Bun services
  "security": {
    "network": {
      "mode": "auto",          // "auto" | "open" | "allowlist"
      "allowedEndpoints": [
        "db.local:5432",       // hostname:port
        "api.example.com",     // hostname only (any port)
        "*.reddit.com"         // wildcard subdomain
      ]
    }
  },

  // Bun subprocess — omit to disable web layer entirely
  "bun": {
    "enabled": true,
    "root": "bun"             // path to app's bun directory, relative to keystone.json
  },

  // Optional C# app assembly — omit if no native app layer
  "appAssembly": "app/bin/Release/net10.0/MyApp.Core.dll",

  // Hot-reloadable C# plugin DLLs
  "plugins": {
    "enabled": true,
    "dir": "dylib",           // bundled plugin directory (relative to keystone.json)
    "userDir": "",            // publisher-managed external plugin directory
    "extensionDir": "",       // community/third-party plugin directory
    "hotReload": true,
    "debounceMs": 200,
    "allowExternalSignatures": false  // allow signed plugins from other teams
  },

  // C# script files (.csx)
  "scripts": {
    "enabled": true,
    "dir": "scripts",
    "hotReload": true,
    "autoCreateDir": true
  },

  // App icon directory
  "iconDir": "icons",

  // Custom menu bar items (merged with engine defaults)
  "menus": {
    "File": [
      { "title": "New", "action": "myapp:new", "shortcut": "cmd+n" },
      { "title": "Open...", "action": "myapp:open", "shortcut": "cmd+o" }
    ]
  },

  // Bun worker processes
  "workers": [
    {
      "name": "data-processor",
      "servicesDir": "workers/data-processor",
      "autoStart": true,
      "browserAccess": false
    }
  ],

  // Process crash recovery — all optional, shown with defaults
  "processRecovery": {
    "bunAutoRestart": true,
    "bunMaxRestarts": 5,
    "bunRestartBaseDelayMs": 500,
    "bunRestartMaxDelayMs": 30000,
    "webViewAutoReload": true,
    "webViewReloadDelayMs": 200
  },

  // Build settings (packaging only — stripped at runtime)
  "build": {
    "pluginMode": "side-by-side",
    "category": "public.app-category.finance",
    "outDir": "dist",
    "signingIdentity": null,
    "requireSigningIdentity": false,
    "notarize": false,
    "notaryProfile": null,
    "dmg": false,
    "minimumSystemVersion": "15.0",
    "extraResources": []
  }
}
```

### Startup Validation

The host validates config at startup and fails fast on invalid input.

| Area | Rule |
|------|------|
| Identity | `name` and `id` are required (non-empty) |
| Plugins | `plugins.dir` required when `plugins.enabled = true`; `plugins.debounceMs >= 0` |
| Scripts | `scripts.dir` required when `scripts.enabled = true` |
| Bun | `bun.root` required when `bun.enabled = true` |
| Process recovery | `bunMaxRestarts >= 0`; `bunRestartBaseDelayMs >= 0`; `bunRestartMaxDelayMs >= bunRestartBaseDelayMs`; `webViewReloadDelayMs >= 0` |
| Windows | each `windows[i].component` required; `width > 0`; `height > 0`; `titleBarStyle` must be `hidden`, `toolkit`, `toolkit-native`, or `none` |
| Security | `security.network.mode` must be `auto`, `open`, or `allowlist`; endpoint strings must be non-empty |
| Workers | each worker needs non-empty `name` and `servicesDir`; worker names must be unique; `maxRestarts >= 0`; `baseBackoffMs >= 0`; if `isExtensionHost = true`, `allowedChannels` cannot be empty |

### `windows[]`

Each entry declares a component type. The `component` name maps to a web component (`bun/web/{component}.ts`) or a registered native `IWindowPlugin` with the same `WindowType`.

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `component` | string | required | Web component name or `IWindowPlugin.WindowType` |
| `title` | string | component name | Window title |
| `width` | number | 800 | Initial width in points |
| `height` | number | 600 | Initial height in points |
| `spawn` | bool | true | Open on launch |
| `titleBarStyle` | string | `"hidden"` | See [Window Chrome](#window-chrome) below |
| `floating` | bool | false | Always-on-top |
| `renderless` | bool | false | Skip GPU context and render thread entirely |

#### `renderless`

When `true`, the window skips GPU context creation and Skia render thread entirely. The window is a native shell with a full-window web view — no Metal/Vulkan/D3D12 surface allocated.

| | Normal window | Renderless window |
|-|---------------|-------------------|
| GPU surface | Metal/Vulkan/D3D12 swap chain per window | None |
| Memory per window | +30–60 MB (GPU context + buffers) | Baseline only |
| Native rendering | Skia canvas available | GPU rendering unavailable |
| Web content | Yes | Yes |

### `bun`

```jsonc
{
  "bun": {
    "enabled": true,    // set false to disable Bun entirely (pure native mode)
    "root": "bun"       // relative path to the directory containing keystone.config.ts
  }
}
```

When `enabled: false` or absent, no Bun process starts. `invoke()` still works (bypasses Bun). `subscribe()`, `query()`, and WebSocket features are unavailable.

`compiledExe` and `compiledWorkerExe` are set by the packager during `--package` — don't set manually.

### `plugins`

Hot-reloadable C# DLLs from up to three directories: bundled (`dir`), publisher-managed (`userDir`), and community (`extensionDir`).

Load order: `dir` → `userDir` → `extensionDir`.

Path resolution for `userDir` and `extensionDir`:
- `~/...` — home directory
- `$APP_SUPPORT/...` — `~/Library/Application Support/<AppName>/...`
- Absolute path — as-is
- Relative path — resolved against app root

When the host binary has a macOS `TeamIdentifier` (signed build), every plugin load is validated:
1. `codesign --verify --strict` must pass
2. Plugin must include a `TeamIdentifier`
3. If `allowExternalSignatures = false`, plugin team must match host team
4. If `allowExternalSignatures = true`, other teams accepted but signatures still required

In ad-hoc/unsigned dev builds, team/signature checks are skipped.

### `processRecovery`

| Key | Default | Description |
|-----|---------|-------------|
| `bunAutoRestart` | `true` | Automatically restart Bun after crash |
| `bunMaxRestarts` | `5` | Give up after N failed restart attempts |
| `bunRestartBaseDelayMs` | `500` | First retry delay; doubles each attempt |
| `bunRestartMaxDelayMs` | `30000` | Backoff cap |
| `webViewAutoReload` | `true` | Reload WKWebView after content process crash |
| `webViewReloadDelayMs` | `200` | Delay before reload (ms) |

### `workers[]`

| Field | Default | Description |
|-------|---------|-------------|
| `name` | required | Unique worker name |
| `servicesDir` | required | Services directory, relative to bun root |
| `autoStart` | `true` | Start after main Bun is ready |
| `browserAccess` | `false` | Start a WebSocket server for direct connections |
| `isExtensionHost` | `false` | Restrict push to allowed channels, disable eval |
| `allowedChannels` | `null` | Channel prefixes for extension host filtering |
| `maxRestarts` | `5` | Maximum restart attempts |
| `baseBackoffMs` | `1000` | First restart delay (doubles, capped at 30s) |

### `security`

| Field | Default | Description |
|-------|---------|-------------|
| `mode` | `"auto"` | `"auto"` = open in dev, allowlist packaged. `"open"` = no restrictions. `"allowlist"` = always enforce. |
| `allowedEndpoints` | `[]` | Hostname or hostname:port patterns. `*` prefix for wildcard subdomains. |

When enforcing, `127.0.0.1` and `localhost` are always allowed.

How it works:
1. C# reads `security.network` and initializes `NetworkPolicy`
2. C# plugins implementing `INetworkDeclarer` merge their endpoints
3. Resolved policy forwarded to Bun via `KEYSTONE_NETWORK_MODE` and `KEYSTONE_NETWORK_ENDPOINTS` env vars
4. `host.ts` / `worker-host.ts` intercept `globalThis.fetch()` in allowlist mode

### `build` (packaging only)

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `pluginMode` | string | `"side-by-side"` | `"side-by-side"` or `"bundled"` |
| `category` | string | `"public.app-category.utilities"` | macOS app category (LSApplicationCategoryType) |
| `outDir` | string | `"dist"` | Output directory for packaged .app |
| `signingIdentity` | string | null | Developer ID cert name (null = ad-hoc) |
| `requireSigningIdentity` | bool | false | Fail packaging if no real identity |
| `notarize` | bool | false | Submit to Apple notarization |
| `notaryProfile` | string | null | Keychain profile for `xcrun notarytool` |
| `dmg` | bool | false | Create DMG after packaging |
| `minimumSystemVersion` | string | `"15.0"` | Minimum macOS version |
| `extraResources` | array | `[]` | Additional files to copy into Resources/ |

---

## `bun/keystone.config.ts`

Lives inside your app's `bun/` directory. Controls the Bun subprocess.

```typescript
import { defineConfig } from "@keystone/sdk/config";

export default defineConfig({
    services: { ... },
    web: { ... },
    http: { ... },
    watch: { ... },
    health: { ... },
    security: { ... },
});
```

All sections optional. At package time, this is resolved and written as `bun/keystone.resolved.json` in the bundle.

### `services`

```typescript
services: {
    dir: "services",      // default: "services"
    hotReload: true,      // default: true
}
```

### `web`

```typescript
web: {
    dir: "web",           // default: "web"
    autoBundle: true,     // bundle on startup. default: true
    hotReload: true,      // HMR on file change. default: true
    preBuilt: false,      // set by packager. default: false

    components: {
        "dashboard": "src/dashboard.ts",
        "settings":  "src/settings/index.tsx",
    },
}
```

### `http`

```typescript
http: {
    enabled: true,          // default: true
    hostname: "127.0.0.1",  // default: "127.0.0.1"
}
```

### `watch`

```typescript
watch: {
    extensions: [".ts", ".tsx", ".js", ".jsx"],
    debounceMs: 150,   // default: 150
}
```

### `health`

```typescript
health: {
    enabled: true,        // default: true
    intervalMs: 30_000,   // default: 30000
}
```

### `security`

```typescript
security: {
    mode: "auto",              // "auto" | "open" | "allowlist"
    allowedActions: [
        "window:minimize",
        "window:close",
        "myapp:*",
    ],
    allowEval: "auto",         // "auto" | true | false
    networkMode: "open",       // inherited from KEYSTONE_NETWORK_MODE
    networkEndpoints: [],      // inherited from KEYSTONE_NETWORK_ENDPOINTS
}
```

Action allowlisting only affects `action()` via WebSocket — not `invoke()` calls (direct to C#).

---

## Environment Variables

Set by the C# host for the Bun subprocess:

| Variable | Set for | Value |
|----------|---------|-------|
| `KEYSTONE_APP_ROOT` | Main + Workers | Absolute path to bun root |
| `KEYSTONE_APP_ID` | Main | `id` from config |
| `KEYSTONE_APP_NAME` | Main + Workers | `name` from config |
| `KEYSTONE_NETWORK_MODE` | Main + Workers | `"open"` or `"allowlist"` |
| `KEYSTONE_NETWORK_ENDPOINTS` | Main + Workers | Comma-separated allow-list |
| `KEYSTONE_WORKER_NAME` | Workers only | Worker `name` |
| `KEYSTONE_SERVICES_DIR` | Workers only | Worker `servicesDir` |
| `KEYSTONE_BROWSER_ACCESS` | Workers only | `"true"` or `"false"` |
| `KEYSTONE_EXTENSION_HOST` | Workers only | `"true"` when extension host |
| `KEYSTONE_ALLOWED_CHANNELS` | Workers only | Comma-separated channel prefixes |

---

## Window Chrome

By default, Keystone windows use a native titled window with a transparent title bar — your web component fills the entire frame, native window controls appear at the standard position, and the window gets compositor-level rounded corners.

### `titleBarStyle` Values

| Value | Native Controls | GPU Title Bar | Rounded Corners | Use Case |
|-------|----------------|---------------|-----------------|----------|
| `"hidden"` (default) | Yes (traffic lights on macOS) | No | Yes (compositor) | Standard platform look |
| `"toolkit"` | No (borderless) | Yes (tabs, float toggle) | No | Custom chrome, tiling |
| `"toolkit-native"` | Yes | Yes (tabs offset past controls) | Yes | Native feel + GPU tabs |
| `"none"` | No | No | No | Fully frameless |

### Hidden (Default)

- Native window controls, compositor rounded corners, standard shadow
- Web content fills full window area including behind native controls
- Make areas draggable with CSS: `-webkit-app-region: drag`
- On macOS, leave ~38px padding top for traffic lights

```css
.titlebar {
  -webkit-app-region: drag;
  height: 38px;
}
.titlebar button {
  -webkit-app-region: no-drag;
}
```

### Toolkit (GPU Title Bar)

- GPU-rendered title bar with close, minimize, float toggle, tab strip
- Borderless window — toolkit provides its own buttons
- Web content starts below the title bar
- Supports tab groups, bind/tiling, toolbar system

### Toolkit-Native

- Combines native window controls with GPU title bar
- Traffic lights on macOS, GTK decorations on Linux, plus GPU tab strip
- Tab strip offset to clear native control area

### None (Frameless)

- No title bar, no controls, no rounded corners
- Plain rectangle — you own the entire surface
- For fully custom shapes or floating panels

### Floating Windows

```jsonc
{ "component": "player", "floating": true }
```

Toggle at runtime:
```typescript
await nativeWindow.setFloating(true);
await nativeWindow.setFloating(false);
```

### GPU Corner Radius

For toolkit windows and native `IWindowPlugin` windows, `Theme.CornerRadius` controls the radius for panels, buttons, cards rendered by Skia:

```csharp
Theme.CornerRadius = 8f;  // set before first frame
```

### Controlling Chrome from C#

For precise control, use `OnBeforeRun` hooks:

```csharp
context.OnBeforeRun += () =>
{
    foreach (var win in ApplicationRuntime.Instance!.WindowManager.GetAllWindows())
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            win.NativeWindow?.StandardWindowButton(NSWindowButton.ZoomButton)?.SetHidden(true);
        });
    }
};
```

---

## Build & Packaging

### Build Pipeline

Two distinct phases:

1. **Framework Build** (`keystone/build.py`) — compiles Rust native libraries, C# core libraries, publishes `Keystone.app` bundle
2. **Application Packaging** (`keystone/tools/package.py` or app-specific `build.py`) — assembles distributable app bundle

### Framework Build

```bash
cd keystone/

python3 build.py              # Full build (Rust + C# + publish)
python3 build.py --clean      # Clean + build
python3 build.py --no-rust    # Skip Rust, rebuild C# only
python3 build.py --rust-only  # Rust only
python3 build.py --debug      # Debug configuration
```

Build phases:
1. **Rust** — `cargo build -p keystone-layout --release` → `libkeystone_layout.dylib`
2. **C#** — Core, Platform, Graphics.Skia, Management, Runtime, Toolkit
3. **Publish** — `dotnet publish` → self-contained `Keystone.app`

### Application Packaging

```bash
# Via standalone packager
python3 tools/package.py /path/to/app
python3 tools/package.py /path/to/app --mode bundled --dmg

# Via app build script
python3 build.py --package
python3 build.py --package --mode bundled
```

CLI flags: `--engine PATH`, `--mode MODE`, `--allow-external`, `--dmg`, `--debug`

### Packaging Steps

1. Load config → Find engine → Create bundle structure
2. Generate Info.plist → Copy framework runtime → App icon
3. Copy plugins (bundled or note external dir)
4. Copy app assembly (if `appAssembly` set)
5. **Compile Bun** — services statically imported into wrapper, compiled via `bun build --compile`. No raw `.ts` ships.
6. Copy scripts, extra resources, icons
7. Generate runtime config (stripped + transformed) and `keystone.resolved.json`
8. Apply entitlements → Code signing → Verification
9. Optional DMG → Optional notarization

### Output Structure

```
dist/MyApp.app/Contents/
├── MacOS/
│   ├── MyApp (main host — compiled exe)
│   ├── MyApp-worker (worker host — compiled exe)
│   ├── libkeystone_layout.dylib
│   └── ... (.NET runtime, framework assemblies)
├── Resources/
│   ├── keystone.config.json (runtime config)
│   ├── bun/
│   │   ├── keystone.resolved.json
│   │   └── web/ (.js and .css only)
│   ├── dylib/ (if bundled mode)
│   ├── icons/
│   └── ...
└── Info.plist
```

### Plugin Modes

**Side-by-side (development):**
- Plugins in `dylib/` outside bundle
- Hot-reload enabled
- Smaller bundle, shared across instances

**Bundled (distribution):**
- Plugins in `Resources/dylib/` inside bundle
- Hot-reload disabled
- Self-contained, single-file distribution

**Hybrid:**
```jsonc
{
  "plugins": {
    "dir": "dylib",
    "userDir": "~/Library/Application Support/MyApp/plugins",
    "extensionDir": "~/Library/Application Support/MyApp/extensions",
    "allowExternalSignatures": true
  }
}
```

### Entitlements (macOS)

All Keystone apps use hardened runtime:

| Entitlement | Reason |
|-------------|--------|
| `network.client` + `network.server` | Bun ↔ WebKit localhost IPC |
| `files.user-selected.read-write` | Open/save dialogs |
| `cs.allow-jit` | WebKit/JavaScriptCore JIT |
| `cs.allow-unsigned-executable-memory` | .NET CLR JIT |

When `allowExternalSignatures` enabled, adds `cs.disable-library-validation`.

### Compiled Service Embedding

In development, services are discovered from the filesystem. For distribution, all service code is compiled into the executable.

At package time:
1. Discovers services from service directories
2. Generates a wrapper that statically imports all modules and registers on `globalThis.__KEYSTONE_COMPILED_SERVICES__`
3. `bun build --compile` produces a single native executable
4. `host.ts` / `worker-host.ts` check for the compiled registry before falling back to filesystem

Worker services are keyed by worker name — one exe serves all workers, `KEYSTONE_WORKER_NAME` selects the subset.

No `.ts`, `node_modules/`, `services/`, or `workers/` directories ship. Web component assets (`.js`/`.css`) do ship since browsers need them.

### Engine Discovery

The packager locates the framework:
1. Explicit `--engine` flag
2. Source checkout (`keystone/Keystone.App/bin/Release/...`)
3. Vendored in app (`app-root/keystone-desktop/...`)
4. Global cache (`~/.keystone/engines/{version}/...`)
5. Auto-download from GitHub releases

### Creating Applications

```bash
# Web-only
python3 tools/create-app.py my-app

# With C# scaffolding
python3 tools/create-app.py my-app --native

# Custom identity
python3 tools/create-app.py my-app --name "My App" --id "com.mycompany.myapp"
```

### Development Workflow

```bash
# Start app
python3 build.py --run

# Rebuild plugins only (hot-reload picks them up)
python3 build.py --plugins

# View logs
tail -f /tmp/keystone.log
```

Set `KEYSTONE_ROOT` or pass as CLI arg to point the engine at your app directory.

---

## Minimal Configurations

**Web-only:**
```jsonc
{
  "name": "My App",
  "id": "com.example.myapp",
  "windows": [{ "component": "app", "spawn": true }],
  "bun": { "root": "bun" }
}
```

**Pure native (no web):**
```jsonc
{
  "name": "My App",
  "id": "com.example.myapp",
  "appAssembly": "app/bin/Release/net10.0/MyApp.dll"
}
```

**Locked-down production:**
```jsonc
{
  "name": "My App",
  "id": "com.example.myapp",
  "security": {
    "network": {
      "mode": "allowlist",
      "allowedEndpoints": ["api.myapp.com", "192.168.1.100:5432"]
    }
  }
}
```

```typescript
export default defineConfig({
    services: { hotReload: false },
    web: { hotReload: false },
    security: {
        mode: "allowlist",
        allowedActions: ["window:minimize", "window:close", "myapp:*"],
        allowEval: false,
    },
});
```
