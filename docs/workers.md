# Bun Worker Processes

Workers are additional Bun subprocesses managed by the C# host. They solve two problems:

1. **Parallelism** — CPU-bound TypeScript work blocks the main Bun event loop. Workers run in their own process with their own event loop. Equivalent to Electron's `UtilityProcess`.
2. **Extension isolation** — third-party TypeScript code runs in a sandboxed worker with restricted push channels. Equivalent to VS Code's extension host process.

Workers use the same service contract as the main Bun process — `start(ctx)`, `query()`, `stop()`, `health()`. A service running in a worker is identical to a service running in main Bun, except for what `ctx` can do.

---

## Configuration

Workers are declared in `keystone.json`:

```jsonc
{
  "workers": [
    {
      "name": "data-processor",
      "servicesDir": "workers/data-processor",
      "autoStart": true,
      "browserAccess": false
    },
    {
      "name": "extension-host",
      "servicesDir": "extensions",
      "autoStart": true,
      "browserAccess": false,
      "isExtensionHost": true,
      "allowedChannels": ["ext:"],
      "maxRestarts": 3
    }
  ]
}
```

### Worker config fields

| Field | Default | Description |
|-------|---------|-------------|
| `name` | required | Unique identifier for this worker |
| `servicesDir` | required | Services directory, relative to the app's `bun/` root |
| `autoStart` | `true` | Start automatically when the main Bun process is ready |
| `browserAccess` | `false` | Start a WebSocket server for direct browser/worker connections |
| `isExtensionHost` | `false` | Restrict `ctx.push()` to allowed channels only |
| `allowedChannels` | `null` | Channel prefixes the extension host may push to |
| `maxRestarts` | `5` | Maximum restart attempts before giving up |
| `baseBackoffMs` | `1000` | First restart delay; doubles each attempt (capped at 30s) |

---

## Directory Layout

```
bun/
├── host.ts                          # Main Bun (unchanged)
├── worker-host.ts                   # Worker entry point (framework-provided)
├── services/                        # Main host services
├── workers/
│   └── data-processor/
│       ├── heavy-compute.ts         # Worker service
│       └── aggregator.ts
└── extensions/                      # Extension host services
    ├── user-extension/
    │   └── index.ts
    └── custom-alerts.ts
```

Workers discover services using the same convention as main Bun — single `.ts` files or directories with `index.ts` in the configured `servicesDir`.

---

## Communication

Workers have two communication paths:

### Path 1: C# Relay (always available)

All workers communicate through C# via stdin/stdout NDJSON — the same protocol as main Bun. No direct process-to-process IPC.

**Worker → Worker:**
```typescript
// In a service on worker "analytics"
ctx.relay("data-processor", "task:start", { job: "backfill", range: "2024-01" });
```

1. Worker "analytics" writes `{"type":"relay","target":"data-processor","channel":"task:start","data":{...}}` to stdout
2. C# `BunWorker` picks this up, routes through `BunWorkerManager.Route()`
3. C# writes `{"type":"relay_in","channel":"task:start","data":{...}}` to worker "data-processor" stdin
4. The target worker dispatches it locally

**Worker → Main Bun:** same flow with `target: "main"`.

**Main Bun → Worker:** use `ctx.relay("data-processor", "channel", data)` from a main Bun service.

### Path 2: Direct WebSocket (when target has `browserAccess: true`)

When a worker has `browserAccess: true`, it runs a WebSocket server on `127.0.0.1` (random port). Other workers (and main Bun) can connect directly — bypassing C# entirely. This is the high-throughput path.

After all autoStart workers are ready, C# broadcasts a `worker_ports` message to every worker and main Bun:
```json
{"type":"worker_ports","data":{"data-processor":54321}}
```

Workers with `browserAccess: false` are omitted from the port map.

**Direct connection from a service:**
```typescript
export async function start(ctx: ServiceContext) {
    const dp = ctx.workers.connect("data-processor");

    // Query a service on the connected worker
    const result = await dp.query("heavy-compute", { data: bigPayload });

    // Subscribe to a push channel — every tick, no C# hop
    dp.subscribe("ticks:realtime", (tick) => {
        processTick(tick);
    });

    // Fire-and-forget
    dp.send("task:enqueue", { job: "cleanup" });
}
```

**`ctx.workers` API:**

| Method | Description |
|--------|-------------|
| `ctx.workers.connect(name)` | Open a WebSocket to a worker. Returns a `WorkerConnection`. Throws if the target has no WebSocket. |
| `ctx.workers.ports()` | Returns the port map: `Record<string, number>` |

**`WorkerConnection` API:**

| Method | Description |
|--------|-------------|
| `query(service, args?)` | Query a service on the connected worker. Returns a promise. |
| `subscribe(channel, cb)` | Subscribe to a push channel. Returns an unsubscribe function. |
| `send(type, data?)` | Fire-and-forget typed message. |
| `close()` | Close the WebSocket. |

### When to use which

| Scenario | Use |
|----------|-----|
| One-off command to another worker | `ctx.relay()` — simple, always works |
| Real-time data streaming between workers | `ctx.workers.connect()` — direct, no C# hop |
| Worker without `browserAccess` | `ctx.relay()` — only option |
| Cross-worker query with reply | `ctx.workers.connect().query()` — or `ctx.relay()` + listen for reply on a channel |

---

## C# API

### Pre-configured workers

Workers declared in `keystone.json` with `autoStart: true` are spawned automatically after the main Bun process is ready.

```csharp
// Access a running worker by name
var processor = context.Workers["data-processor"];
var result = await processor.Query("heavy-compute", new { data = payload });

// Push data to a worker
processor.Push("config:update", new { threshold = 0.5 });
```

### Dynamic workers

Spawn workers at runtime from C# plugins:

```csharp
var worker = context.Workers.Spawn(
    new BunWorkerConfig
    {
        Name = "temp-cruncher",
        ServicesDir = "workers/cruncher",
        BrowserAccess = false,
        MaxRestarts = 2,
    },
    workerHostPath,
    appRoot
);

var result = await worker.Query("crunch", new { input = data });

// Stop when done
context.Workers.Stop("temp-cruncher");
```

### `BunWorkerManager` API

| Method/Property | Description |
|-----------------|-------------|
| `Instance` | Singleton accessor |
| `this[string name]` | Get worker by name, or `null` |
| `All` | `IReadOnlyDictionary<string, BunWorker>` of all workers |
| `Spawn(config, hostPath, appRoot)` | Start a new worker. Returns the `BunWorker`. |
| `Stop(name)` | Stop a specific worker |
| `StopAll()` | Stop all workers (called during shutdown) |
| `Route(from, target, channel, data)` | Forward a relay message between workers |
| `BroadcastPorts()` | Send port map to all workers and main Bun |

### `BunWorker` API

| Method/Property | Description |
|-----------------|-------------|
| `Name` | Worker name |
| `IsRunning` | `true` if ready and process alive |
| `Port` | WebSocket port (null if no browserAccess) |
| `Services` | List of discovered service names |
| `Query(service, args?)` | Query a service on this worker |
| `Push(channel, data)` | Push data to this worker |
| `Stop()` | Graceful shutdown |

---

## Extension Host Mode

When `isExtensionHost: true`, the worker runs with restrictions:

- **Channel filtering**: `ctx.push()` only works for channels matching `allowedChannels` prefixes. Pushes to other channels are silently dropped.
- **`eval` disabled**: The `eval` stdin command returns an error instead of executing.
- Extensions are just normal services in the configured directory — same lifecycle, same `start(ctx)` / `query()` / `stop()` contract.

```jsonc
{
  "name": "extension-host",
  "servicesDir": "extensions",
  "isExtensionHost": true,
  "allowedChannels": ["ext:", "ui:notification"]
}
```

The C# side can add further restrictions — for example, not registering filesystem invoke handlers for extension host windows.

---

## Crash Recovery

Each worker has independent crash recovery with exponential backoff:

- Restart delay: `baseBackoffMs * 2^(attempt - 1)`, capped at 30 seconds
- After `maxRestarts` consecutive failures, the worker stays down
- A successful restart resets the counter
- Workers fire `OnCrash` and `OnRestart` events on the C# side

```csharp
var worker = context.Workers["data-processor"];
worker.OnCrash += exitCode => Log.Warn($"Worker crashed: {exitCode}");
worker.OnRestart += attempt => Log.Info($"Worker recovered (attempt {attempt})");
```

---

## Service Store

Worker services get a namespaced SQLite store, same as main Bun services. The store uses the same `data/services.db` database — namespaces are per-service-name, not per-worker, so a service called "metrics" in a worker shares the namespace with a service called "metrics" in main Bun. Name your worker services distinctly to avoid collisions.

```typescript
// In a worker service
export async function start(ctx: ServiceContext) {
    // Use the defineService builder — store is auto-namespaced
    // Or manually: import { store } from "../lib/store"; const s = store("my-worker-service");
}
```

---

## Next

- [Bun Services](./bun-services.md) — service authoring reference (same contract for workers)
- [Process Model](./process-model.md) — how all processes relate
- [Configuration Reference](./configuration.md) — `workers` config in `keystone.json`
