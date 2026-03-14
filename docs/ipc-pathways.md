# IPC Pathway Map

> Last updated: 2026-03-13

Complete reference for every IPC pathway in Keystone Desktop — 103 distinct pathways across 18 plane-pair sections. Use this to understand what's available, which transport each path uses, and how to call it from the unified `ipc` facade.

---

## Primitives

| Primitive | Semantics | Facade method |
|-----------|-----------|---------------|
| `call` | Request/reply — caller blocks on a promise or Task | `ipc.host.call()`, `ipc.bun.call()`, `ipc.bun.query()` |
| `action` | Fire-and-forget intent — no reply expected | `ipc.host.action()` |
| `event` | Pub/sub — push to subscribers, no reply | `ipc.subscribe()`, `ipc.web.push()` |
| `value` | Retained latest state — replayed to new subscribers | `ipc.web.pushValue()` |
| `stream` | Chunked / binary / sustained throughput flow | Unix socket (C#↔Bun), `/ws-bin` WebSocket (Browser↔Bun) |

## Trust Levels

| Level | Meaning |
|-------|---------|
| `framework` | Built into the runtime, always available |
| `trusted-app` | App-served code (components, services) — full bridge access |
| `extension` | Isolated worker with channel filtering |
| `untrusted` | Arbitrary remote page — `deny` by default |

---

## Facade Quick Reference

### TypeScript — Browser (`@keystone/sdk/bridge`)

| Call | Target | Returns |
|------|--------|---------|
| `ipc.host.call(channel, args?, {signal?})` | C# (direct postMessage) | `Promise<T>` |
| `ipc.host.action(action)` | C# ActionRouter | void |
| `ipc.bun.call(channel, args?, {signal?})` | Bun `.handle()` | `Promise<T>` |
| `ipc.bun.query(service, args?, {signal?})` | Bun `.query()` | `Promise<T>` |
| `ipc.stream.open(channel)` | Bun binary `/ws-bin` | `BrowserStream` |
| `ipc.subscribe(channel, cb)` | WS pub/sub | unsubscribe fn |
| `ipc.publish(channel, data?)` | WS broadcast | void |

### TypeScript — Service (`@keystone/sdk/service`)

| Call | Target | Returns |
|------|--------|---------|
| `svc.call(service, args)` | Other Bun service | `Promise` |
| `svc.push(channel, data)` | WS + C# | void |
| `svc.pushValue(channel, data)` | WS + C# (retained) | void |
| `svc.registerInvokeHandler(ch, fn)` | Browser invoke target | void |
| `svc.openStream(channel, target?)` | Binary stream | `StreamWriter` |
| `svc.store.get/set/delete` | SQLite KV | value |

### C# — Plugin (`context.Ipc`)

| Call | Target | Returns |
|------|--------|---------|
| `context.Ipc.Bun.Call(service, args?)` | Bun service query | `Task<string?>` |
| `context.Ipc.Bun.Push(channel, data)` | WS broadcast | void |
| `context.Ipc.Bun.PushValue(channel, data)` | WS broadcast (retained) | void |
| `context.Ipc.Bun.Action(action)` | Bun action dispatch | void |
| `context.Ipc.Bun.Eval(code)` | Bun eval | `Task<string?>` |
| `context.Ipc.Bun.OpenStream(channel)` | Binary stream to Bun | `IStreamWriter` |
| `context.Ipc.Bun.OnStream(channel, handler)` | Incoming stream | void |
| `context.Ipc.Web.Push(channel, data)` | All browsers | void |
| `context.Ipc.Web.PushValue(channel, data)` | All browsers (retained) | void |
| `context.Ipc.Web.PushToWindow(wid, ch, data)` | Specific browser | void |
| `context.Ipc.Worker(name).Call(svc, args?)` | Named worker | `Task<string?>` |
| `context.Ipc.Worker(name).Push(ch, data)` | Named worker | void |
| `context.Ipc.Worker(name).Action(action)` | Named worker | void |
| `context.Ipc.Channels.*` | C# in-process | varies |

---

## 1. Browser → C# Host

All pathways require WKWebView context. Two reply transports exist:

- **Direct** (1.1): C# replies via `EvaluateJavaScript` — injects the reply directly into the WebView that sent the request. No Bun involvement. This is the default when `WindowInvokeRouter` has a `directEval` delegate (always wired by `ManagedWindow`).
- **WS relay** (1.7): C# replies via `BunManager.Push` → Bun stdin → Bun WS → browser. Used when `directEval` is null (e.g. a custom `WindowInvokeRouter` without WebView access), and always used for non-invoke pushes like context menu events and window lifecycle.

**Facade:** `ipc.host.call(channel, args)` for request/reply, `ipc.host.action(action)` for fire-and-forget.

| # | Pathway | Primitive | Transport | Trust |
|---|---------|-----------|-----------|-------|
| 1.1 | `ipc.host.call(channel, args)` | `call` | WKScriptMessageHandler postMessage → C# → reply via `EvaluateJavaScript` direct into WebView | trusted-app |
| 1.2 | Main-thread invoke variant | `call` | Same as 1.1, synchronous handler | framework |
| 1.3 | `fetch("/api/...")` intercept | `call` | Bridges to `ipc.host.call("http:request", ...)` internally | trusted-app |
| 1.4 | `fetch("/api/...")` streaming | `stream` | invoke + WS subscribe on stream channel | trusted-app |
| 1.5 | `ipc.host.action(string)` | `action` | Dual: (a) WKScriptMessageHandler postMessage `ks_action` → C# ActionRouter (works without Bun), (b) WS → Bun → stdout `action_from_web` → C# ActionRouter | trusted-app |
| 1.6 | Context menu postMessage | `event` | WKScriptMessageHandler → C# pushes to `window:{id}:contextmenu` via WS relay | trusted-app |
| 1.7 | Invoke reply via WS relay | `call` (reply leg) | C# → `BunManager.Push(ctrl channel)` → Bun stdin → WS → browser. Active when `directEval` is null on `WindowInvokeRouter`. | trusted-app |

### Built-in Invoke Channels

All registered per-window. Call with `ipc.host.call(channel, args)`.

| Namespace | Channels |
|-----------|----------|
| `app:*` | `app:paths`, `app:getVersion`, `app:getName`, `app:setAsDefaultProtocolClient`, `app:removeAsDefaultProtocolClient`, `app:isDefaultProtocolClient` |
| `window:*` | `window:setTitle`, `window:open`, `window:setFloating`, `window:isFloating`, `window:getBounds`, `window:setBounds`, `window:center`, `window:startDrag`, `window:getId`, `window:getTitle`, `window:getParentId`, `window:isFullscreen`, `window:isMinimized`, `window:isFocused`, `window:enterFullscreen`, `window:exitFullscreen`, `window:focus`, `window:hide`, `window:show`, `window:setMinSize`, `window:setMaxSize`, `window:setAspectRatio`, `window:setOpacity`, `window:setResizable`, `window:setContentProtection`, `window:setIgnoreMouseEvents`, `window:showContextMenu` |
| `dialog:*` | `dialog:openFile`, `dialog:saveFile`, `dialog:showMessage` |
| `external:*` | `external:path` |
| `clipboard:*` | `clipboard:readText`, `clipboard:writeText`, `clipboard:clear`, `clipboard:hasText` |
| `screen:*` | `screen:getAllDisplays`, `screen:getPrimaryDisplay`, `screen:getCursorScreenPoint` |
| `darkMode:*` | `darkMode:isDark` |
| `battery:*` | `battery:status` |
| `notification:*` | `notification:show` |
| `hotkey:*` | `hotkey:register`, `hotkey:unregister`, `hotkey:isRegistered` |
| `headless:*` | `headless:list`, `headless:evaluate`, `headless:close` |
| `worker:*` | `worker:spawn`, `worker:evaluate`, `worker:terminate`, `worker:list` |
| `webview:*` | `webview:setInspectable`, `webview:setNavigationPolicy`, `webview:setRequestInterceptor` |
| `sw:*` | `sw:status`, `sw:unregister`, `sw:clearCaches` |
| `diagnostics:*` | `diagnostics:crashes`, `diagnostics:health` |
| `http:request` | Internal — fetch intercept target |

---

## 2. C# Host → Browser

C# pushes data to browsers via `context.Ipc.Web.Push(channel, data)`.

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 2.1 | `context.Ipc.Web.Push(channel, data)` | `event` | stdin NDJSON → Bun WS broadcast |
| 2.2 | Window-scoped push `window:{id}:...` | `event` | Same, scoped by channel convention |
| 2.3 | Invoke reply push | `call` (reply leg) | One-shot reply channel |
| 2.4 | `__nativeTheme__` | `value` | Native theme observer |
| 2.5 | `__powerMonitor__` | `value` | Power state observer |
| 2.6 | `__openUrl__` | `event` | URL scheme activation |
| 2.7 | `__openFile__` | `event` | File open delegate |
| 2.8 | `__secondInstance__` | `event` | Second instance launch |
| 2.9 | `hotkey:{accelerator}` | `event` | Global hotkey fired |
| 2.10 | `diagnostics:crash` | `event` | CrashReporter push |
| 2.11 | `window:{id}:contextmenu` | `event` | Context menu metadata |
| 2.12 | `window:{id}:event` | `event` | Window lifecycle (focus, blur, resize, etc.) |
| 2.13 | HTTP streaming chunks | `stream` | Chunks on dedicated channel |

---

## 3. Browser → Bun Main

All go through the `/ws` WebSocket connection.

**Facade:** `ipc.bun.call(channel, args)` for handler invoke, `ipc.bun.query(service, args)` for service query, `ipc.subscribe(channel, cb)` for pub/sub.

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 3.1 | `ipc.bun.call(channel, args)` | `call` | WS invoke with reply channel |
| 3.2 | `ipc.bun.query(service, args)` | `call` | WS query with id correlation |
| 3.3 | `send(type, data)` | `action` | WS fire-and-forget to `webMessageHandlers` |
| 3.4 | `ipc.host.action(string)` | `action` | WS → forwarded to C# via stdout |
| 3.5 | `ipc.subscribe(channel)` | `event` | WS topic subscribe |
| 3.6 | `ipc.publish(channel, data)` | `event` | WS rebroadcast to all subscribers |
| 3.7 | Binary `/ws-bin?channel=...` WebSocket | `stream` | Dedicated binary WS, raw ArrayBuffer frames |

---

## 4. Bun Main → Browser

**Facade (service-side):** `svc.ipc.web.push(channel, data)` or `svc.ipc.web.pushValue(channel, data)`.

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 4.1 | `svc.ipc.web.push(channel, data)` | `event` | WS `server.publish(channel, ...)` |
| 4.2 | Query result reply | `call` (reply leg) | WS direct send to querying client |
| 4.3 | Invoke reply | `call` (reply leg) | WS send on reply channel |
| 4.4 | HMR notification | `event` | WS broadcast to all |
| 4.5 | Theme replay on connect | `value` | WS send on new connection |
| 4.6 | Action fan-out to browser | `action` | WS broadcast to all |
| 4.7 | Binary `/ws-bin` frames | `stream` | Dedicated binary WS |

---

## 5. Browser → Browser (Cross-Window)

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 5.1 | `ipc.publish(channel)` → `ipc.subscribe(channel)` | `event` | WS topic fan-out via Bun server |
| 5.2 | Window-scoped `window:{id}:...` channels | `event` | Same, scoped by convention |
| 5.3 | WebWorker `postMessage` parent↔worker | `event` | WS publish on `worker:{id}:in/out` |

---

## 6. Bun Main Internal (Service ↔ Service)

**Facade:** `svc.ipc.call(service, args)` for inter-service calls.

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 6.1 | `svc.ipc.call(service, args)` | `call` | Direct function call (same process) |
| 6.2 | Action fan-out across services | `action` | In-process iteration |
| 6.3 | Service push → local WS subscribers | `event` | `server.publish(channel, msg)` |

---

## 7. C# Host → Bun Main

**Facade:** `context.Ipc.Bun.Query(service, args)`, `context.Ipc.Web.Push(channel, data)`.

| # | Pathway | Primitive | NDJSON type |
|---|---------|-----------|-------------|
| 7.1 | Query a Bun service | `call` | `{id, type:"query", service, args}` |
| 7.2 | Push to WS channel | `event` | `{id:0, type:"push", channel, data}` |
| 7.3 | Dispatch action | `action` | `{id:0, type:"action", action}` |
| 7.4 | Health check | `call` | `{id, type:"health"}` |
| 7.5 | Eval JS | `call` | `{id, type:"eval", code}` |
| 7.6 | Shutdown | `action` | `{type:"shutdown"}` |
| 7.7 | Worker port advertisement | `event` | `{type:"worker_ports", data:{...}}` |
| 7.8 | Relay to worker | `event` | `{type:"relay_in", channel, data}` |
| 7.9 | Open stream to Bun | `stream` | Unix socket: `stream_open` envelope → stream chunks |
| 7.10 | Stream chunk | `stream` | Unix socket: `stream_chunk` envelope (binary payload) |
| 7.11 | Close stream | `stream` | Unix socket: `stream_close` envelope |

---

## 8. Bun Main → C# Host

**Facade (service-side):** `svc.ipc.host.call(service, args)` for queries, `svc.ipc.host.action(action)` for actions.

| # | Pathway | Primitive | NDJSON type |
|---|---------|-----------|-------------|
| 8.1 | Query result | `call` (reply) | `{id, result}` |
| 8.2 | Query/eval error | `call` (reply) | `{id, error}` |
| 8.3 | Service push | `event` | `{type:"service_push", channel, data}` |
| 8.4 | Action from web | `action` | `{type:"action_from_web", action}` |
| 8.5 | Ready signal | `event` | `{status:"ready", services, webComponents, port}` |
| 8.6 | Relay to worker | `event` | `{type:"relay", target, channel, data}` |
| 8.7 | HMR passthrough | `event` | stdout passthrough |
| 8.8 | Open stream to C# | `stream` | Unix socket: `stream_open` envelope |
| 8.9 | Stream chunk | `stream` | Unix socket: `stream_chunk` envelope (binary payload) |
| 8.10 | Close/cancel stream | `stream` | Unix socket: `stream_close` or `cancel` envelope |

---

## 9. C# Host → Bun Worker

Same NDJSON protocol as C# → Bun Main. `eval` disabled for extension hosts.

**Facade:** `context.Ipc.Worker(name).Push(channel, data)`.

| # | Pathway | Primitive | NDJSON type |
|---|---------|-----------|-------------|
| 9.1 | Query | `call` | `{id, type:"query", service, args}` |
| 9.2 | Push | `event` | `{id:0, type:"push", channel, data}` |
| 9.3 | Action | `action` | `{id:0, type:"action", action}` |
| 9.4 | Health | `call` | `{id, type:"health"}` |
| 9.5 | Shutdown | `action` | `{type:"shutdown"}` |
| 9.6 | Worker ports | `event` | `{type:"worker_ports", data:{...}}` |
| 9.7 | Relay in | `event` | `{type:"relay_in", channel, data}` |
| 9.8 | Eval | `call` | `{id, type:"eval", code}` (disabled if extension host) |

---

## 10. Bun Worker → C# Host

| # | Pathway | Primitive | NDJSON type |
|---|---------|-----------|-------------|
| 10.1 | Query result | `call` (reply) | `{id, result}` |
| 10.2 | Error | `call` (reply) | `{id, error}` |
| 10.3 | Service push | `event` | `{type:"service_push", channel, data}` |
| 10.4 | Ready signal | `event` | `{status:"ready", services, port}` |
| 10.5 | Relay | `event` | `{type:"relay", target, channel, data}` |

---

## 11. Bun Main → Bun Worker

**Facade (service-side):** `svc.ipc.worker(name).call(service, args)`, `svc.ipc.worker(name).push(channel, data)`.

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 11.1 | Relay (fire-and-forget) | `event` | stdout → C# routes → target stdin `relay_in` |
| 11.2 | Direct WS query | `call` | WebSocket `{type:"query", service, args, id}` |
| 11.3 | Direct WS subscribe | `event` | WebSocket `{type:"subscribe", ...}` |
| 11.4 | Direct WS send | `action` | WebSocket `{type, data}` |

---

## 12. Bun Worker → Bun Main

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 12.1 | Relay via C# | `event` | stdout → C# routes to main Bun stdin |
| 12.2 | Direct WS (if main port known) | `call`/`event`/`action` | WebSocket to main Bun port |

---

## 13. Bun Worker → Bun Worker

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 13.1 | Relay via C# | `event` | stdout → C# routes → target stdin |
| 13.2 | Direct WS query | `call` | WebSocket (if target has `browserAccess`) |
| 13.3 | Direct WS subscribe | `event` | WebSocket |
| 13.4 | Direct WS send | `action` | WebSocket |

---

## 14. Bun Worker Internal

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 14.1 | Inter-service call | `call` | Direct function call (same process) |
| 14.2 | Action fan-out | `action` | In-process iteration |
| 14.3 | Service push → local WS | `event` | `server.publish()` (if browserAccess) |

---

## 15. C# Local Plane (`context.Channels`)

All in-process, no serialization. Assembly-tracked for hot-reload cleanup.

| # | Pathway | Primitive | API |
|---|---------|-----------|-----|
| 15.1 | `Channels.Value<T>(name)` | `value` | Last value retained, replayed to new subscribers |
| 15.2 | `Channels.Event<T>(name)` | `event` | Fire-and-forget, no retention |
| 15.3 | `Channels.Notify(channel)` / `Subscribe(channel, cb)` | `event` | Render-wake — triggers re-render |
| 15.4 | `Channels.Alert.*` | `event` | Alert pipeline — `Error`/`Warn`/`Info` |

---

## 16. Browser → Bun Worker

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 16.1 | Direct WS to worker port | `call`/`event` | WebSocket (if worker has `browserAccess`) |
| 16.2 | Via Bun main relay | `event` | Browser → Bun main WS → relay → C# → worker |

No dedicated SDK helper for browser→worker direct connections yet. The browser can manually construct a WebSocket to the worker's port.

---

## 17. Bun Worker → Browser

| # | Pathway | Primitive | Transport |
|---|---------|-----------|-----------|
| 17.1 | Worker WS `server.publish()` | `event` | Worker's own WS server (if `browserAccess`) |
| 17.2 | Indirect via C# | `event` | worker stdout → C# → main Bun stdin push → WS broadcast |

---

## 18. Web-Surface Adjunct Paths

Higher-level APIs built on top of the core IPC primitives.

| # | Pathway | Primitive | Description |
|---|---------|-----------|-------------|
| 18.1 | `headless.open(component)` | `call` | Spawn headless window |
| 18.2 | `headless.evaluate(windowId, js)` | `call` | Eval JS in headless WebView |
| 18.3 | `headless.list()` / `headless.close()` | `call` | Query/close headless windows |
| 18.4 | `webWorker.spawn(component)` | `call` | Spawn headless window + wire channels |
| 18.5 | `WebWorker.postMessage(data)` parent→worker | `event` | WS publish on `worker:{id}:in` |
| 18.6 | `workerSelf.postMessage(data)` worker→parent | `event` | WS publish on `worker:{id}:out` |
| 18.7 | `WebWorker.evaluate(js)` | `call` | Eval JS in headless WebView |
| 18.8 | `WebWorker.terminate()` | `call` | Close headless window |

---

## Coverage Matrix

Every plane pair and which primitives are implemented:

| From \ To | C# Host | Browser | Bun Main | Bun Worker |
|-----------|---------|---------|----------|------------|
| **Browser** | `call` `action` `event` `stream` | `event` (cross-window) | `call` `action` `event` `stream` | `call` `event` (direct WS) |
| **C# Host** | `value` `event` (channels) | `call`(reply) `event` `value` `stream` | `call` `action` `event` `stream` | `call` `action` `event` |
| **Bun Main** | `call`(reply) `action` `event` `stream` | `call`(reply) `action` `event` `value` `stream` | `call` `action` `event` (internal) | `call` `action` `event` |
| **Bun Worker** | `call`(reply) `event` | `event` | `call` `action` `event` | `call` `action` `event` (internal + cross-worker) |
| **Remote page** | deny | deny | deny | deny |

---

## Known Protocol Details

### Reply Correlation

| Path | Strategy |
|------|----------|
| `ipc.host.call()` (invoke) | Direct `EvaluateJavaScript` injection calling `__ks_dr__()` with integer `id` correlation inside `__invoke_reply__` payload (1.1). When `directEval` is null, uses WS relay on multiplexed `window:{id}:__ctrl__` channel (1.7). |
| `ipc.bun.call()` (invokeBun) | Direct WS `__invoke_reply__` with integer `id` — same `resolveInvokeCallback` as host invoke |
| `ipc.bun.query()` (query) | Integer `id` correlation on `__query_result__` |

### Error Format

All error replies follow `{ code: string, message: string }`:

| Code | Meaning |
|------|---------|
| `handler_error` | Handler threw an exception |
| `handler_not_found` | No handler registered for channel/service |
| `cancelled` | Request cancelled by caller (AbortSignal or Task cancellation) |
| `timeout` | Deadline exceeded |
| `stream_backpressure` | Stream buffer overflow (4MB soft, 16MB hard) |

### Timeouts

| Lane | Default | Configurable |
|------|---------|--------------|
| Browser → C# (`ipc.host.call`) | 15s | AbortSignal |
| Browser → Bun (`ipc.bun.query`) | 10s | AbortSignal |
| Browser → Bun (`ipc.bun.call`) | 15s | AbortSignal |
| C# → Bun (`ManagedProcessBridge.Query`) | 10s | `timeoutMs` parameter |
| C# → Bun (`ManagedProcessBridge.Eval`) | 10s | `timeoutMs` parameter |
| Binary stream | No timeout | `deadlineMs` in envelope (reserved, not yet enforced) |

Constants are defined in `bun/sdk/bridge.ts` (`QUERY_TIMEOUT_MS`, `INVOKE_TIMEOUT_MS`) and `ManagedProcessBridge.cs` (method parameter defaults).

### Cancellation

| Lane | Mechanism |
|------|-----------|
| Browser → C# | `AbortSignal` on `invoke()` → sends `ks_cancel` postMessage → `WindowInvokeRouter` cancels `CancellationTokenSource` in `_inflightInvokes` → handler receives `CancellationToken`, reply sends `cancelled` error |
| Browser → Bun (invokeBun) | `AbortSignal` → sends `__cancel__` WS message → Bun aborts `AbortController` in `inflightOps` → handler receives `AbortSignal`, reply suppressed if already aborted |
| Browser → Bun (query) | `AbortSignal` → sends `__cancel__` WS message → same `inflightOps` mechanism as invokeBun |
| C# → Bun | Timeout fires → callback removed from pending map → `TimeoutException` |
| Binary streams | Consumer sends `cancel` envelope → producer receives cancel event |

### Handler Registration

| Registry | Backing | Conflict behavior | Hot-reload cleanup |
|----------|---------|--------------------|--------------------|
| `invokeHandlers` | `HandlerRegistry` | Throws if different owner registers same key | `removeByOwner(name)` |
| `httpHandlers` | `HandlerRegistry` | Throws if different owner registers same key | `removeByOwner(name)` |
| `webMessageHandlers` | `HandlerRegistry` | Throws if different owner registers same key | `removeByOwner(name)` |
| `binaryHandlers` | `HandlerRegistry` | Throws if different owner registers same key | `removeByOwner(name)` |
| `actionHandlers` | `Map` | Last-writer-wins (key = service name, so conflicts impossible by design) | `delete(name)` |
| `hostPushHandlers` | `Map<string, Array>` | Additive (multiple handlers per channel) | Not cleaned on hot-reload |

### Binary Socket (C# ↔ Bun Streams)

Dedicated Unix domain socket for binary/stream traffic. stdin/stdout stays pure NDJSON.

| Detail | Value |
|--------|-------|
| Socket path | `$TMPDIR/keystone-{pid}-bin.sock` |
| Handshake | C# creates socket before Bun starts, passes path via `KEYSTONE_BINARY_SOCKET` env var |
| Frame format | KS binary frame: `[0x4B 0x53]` magic + `[uint32-BE length]` + `[payload]` |
| Payload | JSON-serialized `KeystoneEnvelope` |
| Lifecycle | Created per app launch, survives Bun restarts (accepts new connections) |
| Backpressure | Per-stream byte tracking: 4MB soft limit (flag), 16MB hard limit (force close) |

Envelope kinds for streams:
- `stream_open` — `{ kind, streamId, op (channel name), source, target }`
- `stream_chunk` — `{ kind, streamId, encoding?, payload }`
- `stream_close` — `{ kind, streamId, error? }`
- `cancel` — `{ kind, streamId }` (consumer-initiated)

C# API: `context.Ipc.Bun.OpenStream(channel)` → `IStreamWriter`, `context.Ipc.Bun.OnStream(channel, handler)` → `IStreamReader`.
Bun API: `svc.openStream(channel, target)` → `StreamWriter`, `svc.onStream(channel, handler)` → `StreamReader`.
Browser API: `ipc.stream.open(channel)` → `BrowserStream` (uses `/ws-bin` WebSocket, not Unix socket).

### Trust Boundaries

- **WS token auth:** All `/ws` and `/ws-bin` upgrade requests require a `?token=` query parameter matching the per-launch session token. The token is generated by C# (`Guid.NewGuid()`), passed to Bun/workers via `KEYSTONE_SESSION_TOKEN` env var, and injected into WebViews as `window.__KEYSTONE_SESSION_TOKEN__`. Connections without a valid token receive HTTP 401.
- **`externalUrl` windows:** `WindowWebHost.LoadExternalUrl` injects `__KEYSTONE_PORT__` + `__KEYSTONE_SESSION_TOKEN__` + message handler into the loaded page, granting it full bridge access (equivalent to trusted-app level). The session token prevents OTHER local processes from connecting, but the loaded page itself has full access. Per-WebView scoped tokens are future work.
- **`/ws-bin` channel validation:** Connections to unknown binary channels (no registered handler) receive HTTP 404.
