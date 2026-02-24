# Bun Services

Services are TypeScript modules that run in a Bun subprocess. They handle background work — data fetching, file watching, database access, scheduled tasks, inter-process coordination — and expose it to both the web layer and C# via a clean query/push API.

Every file in `bun/services/` is auto-discovered and started on launch. No registration, no imports into a central file. Drop a module in and it runs.

Services use the same contract whether they run in the main Bun process or in a [worker](./workers.md). Workers let you run services in parallel processes with their own event loop — useful for CPU-bound work or isolating third-party extensions.

---

## The Service Contract

A service module exports specific functions that the runtime calls at lifecycle points. You can write them manually or use the builder SDK.

```typescript
// bun/services/my-service.ts — manual style
import type { ServiceContext } from "@keystone/sdk/service";

export async function start(ctx: ServiceContext) {
    // Called once when the service loads (and after hot-reload)
}

export function query(args: any) {
    // Called when the web layer or C# calls query("my-service", args)
    return { ok: true };
}

export function stop() {
    // Called before hot-reload or shutdown — clean up timers, connections, etc.
}

export function health(): { ok: boolean } {
    // Called by the runtime health monitor every 30s
    return { ok: true };
}

export function onAction(action: string) {
    // Called when an action is dispatched from web or C#
}
```

---

## The Builder API (recommended)

`defineService` wraps the lifecycle into a composable builder. It also wires the SQLite-backed store and service handle automatically.

```typescript
import { defineService } from "@keystone/sdk/service";

export default defineService("weather")
    .query(async (args: { city: string }, svc) => {
        const cached = svc.store.get<any>(args.city);
        if (cached) return cached;

        const data = await fetch(`https://wttr.in/${args.city}?format=j1`).then(r => r.json());
        svc.store.set(args.city, data);
        return data;
    })
    .every(60_000, async (svc) => {
        // Refresh all cached cities every minute and push updates
        for (const city of svc.store.keys()) {
            const data = await fetch(`https://wttr.in/${city}?format=j1`).then(r => r.json());
            svc.store.set(city, data);
            svc.push("weather:update", { city, data });
        }
    })
    .health((svc) => ({ ok: svc.store.keys().length >= 0 }))
    .onStop((svc) => svc.store.clear())
    .build();
```

### Builder Methods

| Method | When it runs | Notes |
|--------|-------------|-------|
| `.query(fn)` | On `query("name", args)` call | Can be async. Return value sent to caller. |
| `.every(ms, fn)` | On a recurring interval | Timers auto-cancelled on stop. |
| `.onAction(fn)` | On action dispatch | Receives the full action string. |
| `.health(fn)` | Every 30s (configurable) | Return `{ ok: false }` to trigger auto-restart. |
| `.onStop(fn)` | Before hot-reload or shutdown | Clean up connections, cancel pending work. |
| `.build(init?)` | At startup | Optional `init` runs after start. Returns the module. |

---

## The Service Handle

Every handler receives a `ServiceHandle` with scoped utilities:

```typescript
type ServiceHandle = {
    ctx: ServiceContext;         // raw runtime context
    store: StoreHandle;          // namespaced SQLite key-value store
    call: (service, args?) => Promise<any>;   // query another service
    push: (channel, data) => void;            // broadcast to all subscribers
};
```

### `push` — broadcast data

`push` broadcasts to every subscriber on a named channel — both web components and C#. It also signals the C# host via stdout so it can forward to its own listeners.

```typescript
svc.push("metrics:cpu", { percent: 72.4, cores: 8 });
```

Web components subscribe to this channel:
```typescript
subscribe("metrics:cpu", (data) => {
    gauge.textContent = `${data.percent.toFixed(1)}%`;
});
```

C# can also receive it directly via `OnServicePush`:
```csharp
BunManager.Instance.OnServicePush += (channel, json) => {
    if (channel == "metrics:cpu") { /* parse json */ }
};
```

### `call` — inter-service queries

Services can query each other without going through the web layer or C#:

```typescript
// In weather service
const location = await svc.call("location", { ip: req.ip });
const forecast = await fetchForecast(location.city);
```

### `store` — persistent key-value store

Each service gets a namespaced slice of a shared SQLite database at `bun/data/services.db`. Values survive hot-reloads and app restarts.

```typescript
svc.store.set("last-sync", Date.now());
svc.store.get<number>("last-sync");   // → timestamp
svc.store.del("last-sync");
svc.store.keys();                     // → string[]
svc.store.clear();                    // wipe namespace
```

Values are JSON-serialized — any JSON-compatible type works.

---

## Querying Services

### From TypeScript (web layer)

```typescript
import { query } from "@keystone/sdk/bridge";

// Request/reply
const weather = await query("weather", { city: "San Francisco" });

// Direct query helper on keystone() instance
const ks = keystone();
const files = await ks.query("file-scanner", { dir: "/home/user/Documents" });
```

### From C#

```csharp
// context.Bun.Query() returns raw JSON string
var json = await context.Bun.Query("weather", new { city = "San Francisco" });
using var doc = JsonDocument.Parse(json!);
var temp = doc.RootElement.GetProperty("temp_C").GetString();
```

---

## Service Directory Layout

```
bun/services/
├── metrics.ts          # single-file service — name is "metrics"
├── file-scanner.ts
└── database/           # directory service — name is "database"
    ├── index.ts        # entry point
    └── schema.ts       # internal module (not a service)
```

Both single files and directories work. Directory services let you split complex services across multiple files without exposing internal modules to the runtime.

---

## Hot Reload

When you save a service file, the runtime:

1. Calls the existing service's `stop()`.
2. Clears the require cache for that service file (or directory).
3. Re-requires the module and calls `start(ctx)` again.

State in variables is lost on reload — but anything in `svc.store` (SQLite) survives, because the database persists across process lifetimes.

Hot reload is per-service. Changing `database/schema.ts` reloads the `database` service. Changing a shared module in `lib/` triggers a full reload of all services.

Configure hot-reload behavior in `keystone.config.ts`:

```typescript
export default defineConfig({
    services: {
        hotReload: true,   // default
        dir: "services",
    },
    watch: {
        debounceMs: 150,   // wait 150ms after last change before reloading
        extensions: [".ts", ".tsx"],
    },
});
```

---

## Compiled Distribution

When you package your app with `python3 build.py --package`, services are compiled directly into the executable. No `.ts` source files ship in the bundle.

The packager generates a wrapper that statically imports all service modules and registers them on a global before loading `host.ts`. At runtime, `host.ts` checks for this global and uses the baked-in services instead of scanning the filesystem:

```
Development:  host.ts → readdirSync("services/") → require() each .ts file
Distribution: compiled exe → __KEYSTONE_COMPILED_SERVICES__ global → all services already loaded
```

This means:
- **Source code is protected** — service logic is inside the compiled binary, not inspectable
- **No filesystem dependency** — the exe runs without `node_modules/`, `services/`, or any `.ts` files
- **Same API** — services work identically in both modes; no code changes needed

The `svc.store` (SQLite) database still lives on disk at `data/services.db` — the store is initialized lazily on first access so it works correctly inside compiled executables where `import.meta.dir` resolves to Bun's read-only virtual filesystem.

See [Build & Packaging — Compiled Service Embedding](./build-and-packaging.md#compiled-service-embedding) for the full compilation pipeline.

---

## Action Handlers

Services can respond to action strings dispatched from the web layer or C#:

```typescript
defineService("file-manager")
    .onAction((action, svc) => {
        if (action === "file:refresh") {
            svc.push("files:list", scanFiles());
        }
    })
    .build();
```

Actions are dispatched to **all** registered action handlers across all services — there's no routing by service name. Use prefixes to namespace your actions.

---

## Health Monitoring

The runtime polls `.health()` every 30 seconds (configurable). If a service returns `{ ok: false }`, the runtime calls `stop()` then `start()` to restart it.

```typescript
defineService("db-connection")
    .health((svc) => {
        const connected = svc.store.get<boolean>("connected") ?? false;
        return { ok: connected, lastCheck: Date.now() };
    })
    .build(async (svc) => {
        try {
            await connectToDatabase();
            svc.store.set("connected", true);
        } catch {
            svc.store.set("connected", false);
        }
    });
```

Adjust the interval in `keystone.config.ts`:

```typescript
export default defineConfig({
    health: {
        enabled: true,
        intervalMs: 10_000,   // check every 10s instead of 30s
    },
});
```

---

## Custom Web Messages

Services can register handlers for arbitrary messages sent from the web layer via `keystone().send(type, data)`. This is useful for real-time bidirectional communication without going through C#.

```typescript
// Service
export async function start(ctx: ServiceContext) {
    ctx.onWebMessage("chat:send", async (data, ws) => {
        // data is whatever the web layer sent
        // ws is the raw ServerWebSocket — you can reply directly
        const saved = await saveChatMessage(data);
        ctx.push("chat:messages", saved);
    });
}
```

```typescript
// Web layer
const ks = keystone();
ks.send("chat:send", { text: "Hello", roomId: "general" });
```

---

## Built-in Services

The runtime registers a `paths` service automatically — always available, never overwritten by app services.

```typescript
// Returns well-known filesystem paths
const paths = await query("paths");
// {
//   appRoot: "/path/to/app/bun",
//   userData: "~/.keystone/<app-id>",
//   documents: "~/Documents",
//   downloads: "~/Downloads",
//   desktop: "~/Desktop",
//   temp: "/tmp"
// }
```

The same paths are accessible from C# via `app:getPath` invoke handler.

---

## Security — Action Allowlisting

By default, the web layer can dispatch any action string to C#. To restrict which actions are permitted, configure `allowedActions` in `keystone.config.ts`:

```typescript
export default defineConfig({
    security: {
        allowedActions: [
            "window:minimize",
            "window:maximize",
            "window:close",
            "app:quit",
            "myapp:*",          // wildcard — all actions prefixed "myapp:"
        ],
    },
});
```

Actions not in the allowlist are logged and dropped before they reach C# or service action handlers. Invoke calls (`invoke()` via `WKScriptMessageHandler`) are not subject to this allowlist — they bypass Bun entirely.

---

## Next

- [Workers](./workers.md) — run services in additional Bun processes for parallelism and extension isolation
- [Web Components](./web-components.md) — TypeScript UI modules, the slot system, HMR
- [Native API Reference](./native-api.md) — built-in invoke handlers
- [Configuration Reference](./configuration.md) — `keystone.json` and `keystone.config.ts`
