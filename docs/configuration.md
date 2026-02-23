# Configuration Reference

Keystone uses two configuration files with different responsibilities:

| File | Scope | Read by |
|------|-------|---------|
| `keystone.json` | App identity, windows, plugins, C# runtime | C# host process |
| `bun/keystone.config.ts` | Bun runtime: services, bundling, HMR, security | Bun subprocess |

Both are optional — the runtime has sensible defaults for everything.

---

## `keystone.json`

Lives at the root of your app directory. Controls the C# host process.

### Full schema

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
      "spawn": true           // open this window on launch
    }
  ],

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
    "userDir": "",            // external plugin directory — side-by-side or installer layout
    "hotReload": true,
    "debounceMs": 200,
    "allowExternalSignatures": false  // allow plugins not signed by this app developer
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

  // Bun worker processes — additional Bun subprocesses for parallelism or extension isolation
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
  }
}
```

### `windows[]`

Each entry in `windows` declares a component type the runtime knows about. The `component` name maps directly to a web component (`bun/web/{component}.ts`) or a registered native `IWindowPlugin` with the same `WindowType`.

```jsonc
{
  "component": "dashboard",   // web/dashboard.ts or IWindowPlugin with WindowType="dashboard"
  "title": "Dashboard",       // NSWindow title bar text
  "width": 1400,
  "height": 900,
  "spawn": true               // false = registered but not opened on launch
}
```

Windows with `spawn: false` can be opened programmatically:
```typescript
const id = await nativeWindow.open("dashboard");
```
```csharp
_windowManager.SpawnWindow("dashboard");
```

### `bun`

```jsonc
{
  "bun": {
    "enabled": true,    // set false to disable Bun entirely (pure native mode)
    "root": "bun"       // relative path to the directory containing keystone.config.ts
  }
}
```

When `enabled: false` or the `bun` block is absent, no Bun process is started. All `subscribe()`, `query()`, and WebSocket-based features are unavailable. `invoke()` still works because it bypasses Bun.

### `plugins`

Hot-reloadable C# DLLs dropped into `dylib/`. The runtime watches this directory with a file system watcher.

```jsonc
{
  "plugins": {
    "enabled": true,
    "dir": "dylib",                    // bundled plugin dir, relative to keystone.json
    "userDir": "~/Library/...",        // external dir — supports ~/, $APP_SUPPORT, absolute, or relative paths
    "hotReload": true,                 // watch for changes and reload automatically
    "debounceMs": 200,                 // wait 200ms after last change before reloading
    "allowExternalSignatures": false   // when true, disables library validation for third-party plugins
  }
}
```

`dir` and `userDir` are both optional and can coexist. `dir` is for plugins bundled inside the `.app`; `userDir` is for plugins that live outside the bundle — useful for dev (side-by-side dylib/) and installer layouts where plugins are user-managed.

`userDir` path resolution:
- `~/...` — expands to home directory
- `$APP_SUPPORT/...` — expands to `~/Library/Application Support/<AppName>/...`
- Absolute path — used as-is
- Relative path — resolved against the app root (`keystone.json` directory)

See [Plugin System](./plugin-system.md) for how to author hot-reloadable service, logic, library, and window plugins.

### `processRecovery`

Controls automatic restart behavior when subprocesses crash.

| Key | Default | Description |
|-----|---------|-------------|
| `bunAutoRestart` | `true` | Automatically restart Bun after crash |
| `bunMaxRestarts` | `5` | Give up after N failed restart attempts |
| `bunRestartBaseDelayMs` | `500` | First retry delay; doubles each attempt |
| `bunRestartMaxDelayMs` | `30000` | Backoff cap |
| `webViewAutoReload` | `true` | Reload WKWebView after content process crash |
| `webViewReloadDelayMs` | `200` | Delay before reload (ms) |

Restart delay sequence with defaults: 500ms → 1s → 2s → 4s → 8s → give up.

To disable restart entirely (e.g. in production where you want a crash to be visible):
```json
{ "processRecovery": { "bunAutoRestart": false } }
```

### `workers[]`

Additional Bun subprocesses. Each worker runs `worker-host.ts` with its own services directory.

```jsonc
{
  "workers": [
    {
      "name": "data-processor",          // unique identifier
      "servicesDir": "workers/data-processor",  // relative to bun root
      "autoStart": true,                 // start when main Bun is ready
      "browserAccess": false,            // no WebSocket server
      "maxRestarts": 5,                  // crash recovery limit
      "baseBackoffMs": 1000              // first restart delay
    },
    {
      "name": "extension-host",
      "servicesDir": "extensions",
      "isExtensionHost": true,           // restricted push channels
      "allowedChannels": ["ext:", "ui:notification"],
      "maxRestarts": 3
    }
  ]
}
```

| Field | Default | Description |
|-------|---------|-------------|
| `name` | required | Unique worker name |
| `servicesDir` | required | Services directory, relative to bun root |
| `autoStart` | `true` | Start automatically after main Bun is ready |
| `browserAccess` | `false` | Start a WebSocket server for direct connections |
| `isExtensionHost` | `false` | Restrict push to allowed channels, disable eval |
| `allowedChannels` | `null` | Channel prefixes for extension host push filtering |
| `maxRestarts` | `5` | Maximum restart attempts |
| `baseBackoffMs` | `1000` | First restart delay (doubles each attempt, capped at 30s) |

See [Workers](./workers.md) for full documentation.

---

## `bun/keystone.config.ts`

Lives inside your app's `bun/` directory. Controls the Bun subprocess. The C# host reads `keystone.json`; this file is read by Bun itself at startup.

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

All sections are optional. Missing sections use defaults.

### `services`

```typescript
services: {
    dir: "services",      // services directory, relative to bun root. Default: "services"
    hotReload: true,      // hot-reload services on file change. Default: true
}
```

### `web`

```typescript
web: {
    dir: "web",           // auto-discovery directory for .ts/.tsx files. Default: "web"
    autoBundle: true,     // bundle components on startup. Default: true
    hotReload: true,      // rebundle and send HMR on file change. Default: true

    // Explicit name → entry path mappings (relative to bun root).
    // These are bundled in addition to auto-discovered web/ files.
    // The key is the component name used in keystone.json windows[].component.
    components: {
        "dashboard": "src/dashboard.ts",
        "settings":  "src/settings/index.tsx",
    },
}
```

`web.components` lets you put source files anywhere — no required folder structure. Explicit entries and auto-discovered `web/*.ts` files coexist. When the same name appears in both, the explicit entry wins.

Setting `autoBundle: false` means components aren't pre-bundled at startup — they bundle on first request, adding latency to the first load.

### `http`

```typescript
http: {
    enabled: true,          // start the HTTP server. Default: true
    hostname: "127.0.0.1",  // bind address. Default: "127.0.0.1"
    // port is always dynamic (0 = OS-assigned) — read from the ready signal
}
```

The HTTP server is required for web components. Disable it only in pure-service mode where no web UI is needed.

### `watch`

```typescript
watch: {
    extensions: [".ts", ".tsx", ".js", ".jsx"],  // file extensions to watch
    debounceMs: 150,   // wait before reloading after last change. Default: 150
}
```

Add `.svelte`, `.vue`, etc. if you're using those frameworks:
```typescript
watch: { extensions: [".ts", ".tsx", ".svelte"] }
```

### `health`

```typescript
health: {
    enabled: true,        // run health checks. Default: true
    intervalMs: 30_000,   // check interval in ms. Default: 30000
}
```

When a service's `health()` returns `{ ok: false }`, the runtime calls `stop()` then `start()` on that service. The interval controls how often this is polled.

### `security`

```typescript
security: {
    // Allowlist of actions the web layer may dispatch.
    // Empty (default) = open model, all actions permitted.
    // Wildcards: "myapp:*" matches any action starting with "myapp:"
    allowedActions: [
        "window:minimize",
        "window:close",
        "myapp:*",
    ],
}
```

This only applies to actions dispatched from web components via `action()` over the WebSocket. It does not affect `invoke()` calls (which go directly to C# via `WKScriptMessageHandler`) or actions dispatched from C# internally.

---

## Minimal Configurations

### Web-only (no C#)

```jsonc
// keystone.json
{
  "name": "My App",
  "id": "com.example.myapp",
  "windows": [{ "component": "app", "spawn": true }],
  "bun": { "root": "bun" }
}
```

```typescript
// bun/keystone.config.ts — can be omitted entirely for defaults
export default defineConfig({});
```

### Pure native (no web)

```jsonc
// keystone.json — no bun block, no windows block
{
  "name": "My App",
  "id": "com.example.myapp",
  "appAssembly": "app/bin/Release/net10.0/MyApp.dll"
}
```

No `bun/keystone.config.ts` needed. The C# app layer registers window plugins directly.

### Locked-down production

```jsonc
// keystone.json
{
  "name": "My App",
  "id": "com.example.myapp",
  "processRecovery": {
    "bunMaxRestarts": 3,
    "bunRestartBaseDelayMs": 1000
  }
}
```

```typescript
// bun/keystone.config.ts
export default defineConfig({
    services: { hotReload: false },
    web: { hotReload: false },
    security: {
        allowedActions: ["window:minimize", "window:close", "myapp:*"],
    },
});
```

---

## Environment Variables

The Bun subprocess has access to these environment variables set by the C# host:

| Variable | Set for | Value |
|----------|---------|-------|
| `KEYSTONE_APP_ROOT` | Main + Workers | Absolute path to the app's bun root directory |
| `KEYSTONE_APP_ID` | Main | The `id` field from `keystone.json` |
| `KEYSTONE_WORKER_NAME` | Workers only | The worker's `name` from config |
| `KEYSTONE_SERVICES_DIR` | Workers only | The worker's `servicesDir` from config |
| `KEYSTONE_BROWSER_ACCESS` | Workers only | `"true"` or `"false"` |
| `KEYSTONE_EXTENSION_HOST` | Workers only | `"true"` when `isExtensionHost` is set |
| `KEYSTONE_ALLOWED_CHANNELS` | Workers only | Comma-separated channel prefixes |

Use them in services that need to know the app identity or locate files relative to the bun root:

```typescript
const appId = process.env.KEYSTONE_APP_ID ?? "keystone";
const appRoot = process.env.KEYSTONE_APP_ROOT ?? import.meta.dir;
```

---

## Next

- [Bun Services](./bun-services.md) — service authoring reference
- [Workers](./workers.md) — additional Bun processes for parallelism and extension isolation
- [Web Components](./web-components.md) — component lifecycle and bridge API
- [Process Model](./process-model.md) — how the processes relate
- [C# App Layer](./csharp-app-layer.md) — `ICorePlugin`, native windows, custom invoke handlers
- [Plugin System](./plugin-system.md) — hot-reloadable DLL plugin types
- [Window Chrome](./window-chrome.md) — controlling the native window surface
