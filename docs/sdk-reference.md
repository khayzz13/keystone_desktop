# SDK Reference

> Last updated: 2026-03-07

The `@keystone/sdk/bridge` module is the client-side bridge between your web components (running in WKWebView) and the C# + Bun host process. It gives you typed access to native OS APIs, pub/sub channels, service queries, and lifecycle hooks.

```typescript
import { ipc, app, nativeWindow, dialog, /* ... */ } from "@keystone/sdk/bridge";
```

---

## Unified IPC Facade (`ipc`)

The `ipc` export is the primary API for all cross-process communication. It organizes calls by which process you're targeting:

```typescript
import { ipc } from "@keystone/sdk/bridge";

// C# host — direct postMessage, fastest path
await ipc.host.call<string>("app:getVersion");
ipc.host.action("app:quit");

// Bun services — via WebSocket
const notes = await ipc.bun.call<Note[]>("notes:getAll");
const weather = await ipc.bun.query("weather", { city: "SF" });

// Channel pub/sub
const unsub = ipc.subscribe("metrics:cpu", (data) => { ... });
ipc.publish("chat:message", { text: "Hello" });
```

### `ipc.host.call<T>(channel, args?, options?): Promise<T>`

Invoke a C# handler by channel name. Direct postMessage to WKWebView — no Bun round-trip. Timeout: 15 seconds. Supports `AbortSignal` for cancellation.

### `ipc.host.action(action): void`

Dispatch an action string to the C# host and all connected Bun services. Fire-and-forget.

### `ipc.bun.call<T>(channel, args?, options?): Promise<T>`

Invoke a Bun service handler by channel name. Goes over WebSocket. Timeout: 15 seconds. Supports `AbortSignal` for cancellation.

```typescript
// Bun side
defineService("notes").handle("notes:getAll", async (_, svc) => svc.store.get("notes"));

// Browser side
const notes = await ipc.bun.call<Note[]>("notes:getAll");
```

### `ipc.bun.query<T>(service, args?): Promise<T>`

Query a Bun service's `.query()` handler. Returns a promise with the result. Timeout: 10 seconds.

### `ipc.subscribe(channel, callback): () => void`

Subscribe to a named data channel pushed from C# or Bun. The last received value is replayed immediately to new subscribers. Returns an unsubscribe function.

### `ipc.publish(channel, data?): void`

Broadcast data to a channel. All subscribers (any window) receive it immediately.

### `ipc.host.call` vs `ipc.bun.call`

| | `ipc.host.call` | `ipc.bun.call` |
|---|---|---|
| Target | C# `RegisterInvokeHandler` | Bun `defineService().handle()` |
| Transport | Direct postMessage → WKWebView | WebSocket |
| Round-trip | Zero Bun hops | One Bun hop |
| Requires WKWebView | Yes | No |
| Use for | Native OS APIs, window control | Business logic, data access |

### Legacy Aliases

The individual function exports remain for backward compatibility:

```typescript
import { invoke, invokeBun, query, subscribe, action, publish } from "@keystone/sdk/bridge";
```

| Legacy | Equivalent |
|--------|-----------|
| `invoke(ch, args)` | `ipc.host.call(ch, args)` |
| `invokeBun(ch, args)` | `ipc.bun.call(ch, args)` |
| `query(svc, args)` | `ipc.bun.query(svc, args)` |
| `subscribe(ch, cb)` | `ipc.subscribe(ch, cb)` |
| `action(a)` | `ipc.host.action(a)` |
| `publish(ch, data)` | `ipc.publish(ch, data)` |

### `keystone(): KeystoneClient`

Returns the singleton bridge client. Auto-connects to the Bun WebSocket on first call. All namespace helpers (`app`, `clipboard`, etc.) call this internally. The `ipc` export is generally preferred over calling `keystone()` directly.

```typescript
const ks = keystone();
ks.connected; // boolean — whether WebSocket is live
ks.windowId;  // string — ID of the native window this component belongs to
ks.port;      // number — Bun HTTP server port
```

---

## `app`

```typescript
import { app } from "@keystone/sdk/bridge";
```

### `app.getName(): Promise<string>`

Returns the app name from `keystone.json`.

### `app.getVersion(): Promise<string>`

Returns the app version from `keystone.json`.

### `app.paths(): Promise<AppPaths>`

Returns all well-known filesystem paths in a single call.

```typescript
type AppPaths = {
  data: string;       // ~/Library/Application Support/<app-name>
  documents: string;  // ~/Documents
  downloads: string;  // ~/Downloads
  desktop: string;    // ~/Desktop
  temp: string;       // /tmp
  root: string;       // app root directory (next to keystone.json)
};

const { data, downloads, root } = await app.paths();
```

### `app.quit(): void`

Terminates the application. Triggers `ICorePlugin.OnShutdown` before exit.

### `app.onOpenUrl(callback): () => void`

Subscribe to URLs opened by the OS (custom protocol handlers, etc.). Fires when the app is activated via a registered URL scheme. Returns an unsubscribe function.

```typescript
const unsub = app.onOpenUrl((url) => {
  console.log("Opened URL:", url); // e.g. "myapp://settings/theme"
});
```

### `app.onOpenFile(callback): () => void`

Subscribe to file-open events from the OS (double-click, drag-to-dock). Returns an unsubscribe function.

```typescript
const unsub = app.onOpenFile((path) => {
  console.log("Opened file:", path);
});
```

### `app.onSecondInstance(callback): () => void`

Subscribe to second-instance launch events. When another instance of the app is launched, it forwards its argv and working directory to the running instance, then exits. Returns an unsubscribe function.

```typescript
const unsub = app.onSecondInstance((argv, cwd) => {
  console.log("Second instance launched:", argv, "from", cwd);
});
```

### `app.setAsDefaultProtocolClient(scheme): Promise<boolean>`

Register this app as the default handler for a URL scheme. Returns `true` on success.

```typescript
await app.setAsDefaultProtocolClient("myapp");
// Now opening "myapp://..." in the browser will activate this app
```

### `app.removeAsDefaultProtocolClient(scheme): Promise<boolean>`

Unregister this app as the default handler for a URL scheme.

### `app.isDefaultProtocolClient(scheme): Promise<boolean>`

Check if this app is the default handler for a URL scheme.

---

## `nativeWindow`

```typescript
import { nativeWindow } from "@keystone/sdk/bridge";
```

All methods target the **current window** — the native window that owns the WebKit slot this component is mounted in.

### `nativeWindow.setTitle(title): Promise<void>`

```typescript
await nativeWindow.setTitle("My App — Untitled");
```

### `nativeWindow.minimize(): void` / `maximize(): void` / `close(): void`

Fire-and-forget window controls.

### `nativeWindow.open(type, opts?): Promise<string>`

Spawns a new window of the given registered type. Returns the new window's ID. `type` must match a component in `keystone.json` `windows[]` or a native `IWindowPlugin.WindowType`.

```typescript
const settingsWindowId = await nativeWindow.open("settings");

// Open as child of current window
const childId = await nativeWindow.open("details", { parent: nativeWindow.getId() });
```

### `nativeWindow.setFloating(floating): Promise<void>`

Toggle always-on-top.

### `nativeWindow.isFloating(): Promise<boolean>`

### `nativeWindow.getBounds(): Promise<{ x, y, width, height }>`

### `nativeWindow.setBounds(bounds): Promise<void>`

All fields optional — omitted fields keep current value.

```typescript
await nativeWindow.setBounds({ width: 1024, height: 768 });
```

### `nativeWindow.startDrag(): void`

Initiate a native window drag from the current mouse position. Call this from a `mousedown` handler on your custom title bar or drag region. Fire-and-forget — the OS takes over drag tracking until the user releases the mouse button.

```typescript
titleBar.addEventListener("mousedown", () => {
    nativeWindow.startDrag();
});
```

Uses `RegisterMainThreadInvokeHandler` internally — the drag must begin synchronously during the current event's run loop iteration.

### `nativeWindow.center(): Promise<void>`

Center the window on the main display.

### Identity

#### `nativeWindow.getId(): string`

Returns the window ID synchronously (read from the slot context — no round-trip).

#### `nativeWindow.getTitle(): Promise<string>`

#### `nativeWindow.getParentId(): Promise<string | null>`

Returns the parent window's ID, or `null` for top-level windows.

### State Queries

#### `nativeWindow.isFullscreen(): Promise<boolean>`
#### `nativeWindow.isMinimized(): Promise<boolean>`
#### `nativeWindow.isFocused(): Promise<boolean>`

### Fullscreen

#### `nativeWindow.enterFullscreen(): Promise<void>`
#### `nativeWindow.exitFullscreen(): Promise<void>`

```typescript
await nativeWindow.enterFullscreen();
// ... later
await nativeWindow.exitFullscreen();
```

### Constraints & Appearance

#### `nativeWindow.setMinSize(width, height): Promise<void>`
#### `nativeWindow.setMaxSize(width, height): Promise<void>`

```typescript
await nativeWindow.setMinSize(400, 300);
await nativeWindow.setMaxSize(1920, 1080);
```

#### `nativeWindow.setAspectRatio(ratio): Promise<void>`

Lock the window's aspect ratio. Pass `0` to clear.

```typescript
await nativeWindow.setAspectRatio(16/9);
```

#### `nativeWindow.setOpacity(opacity): Promise<void>`

Set window opacity (`0.0` fully transparent, `1.0` fully opaque).

```typescript
await nativeWindow.setOpacity(0.85);
```

#### `nativeWindow.setResizable(resizable): Promise<void>`

#### `nativeWindow.setContentProtection(enabled): Promise<void>`

Prevent screen capture / screenshots of this window's content.

#### `nativeWindow.setIgnoreMouseEvents(ignore): Promise<void>`

Make the window transparent to mouse events (click-through). Useful for overlay windows.

### Visibility

#### `nativeWindow.focus(): Promise<void>`
#### `nativeWindow.hide(): Promise<void>`
#### `nativeWindow.show(): Promise<void>`

### DevTools

#### `nativeWindow.setInspectable(enabled): Promise<void>`

Enable Safari Web Inspector for this window's WebView. Once enabled, the WebView appears under Safari > Develop menu.

```typescript
await nativeWindow.setInspectable(true);
```

Auto-enabled at startup when the `KEYSTONE_INSPECTABLE=1` environment variable is set. Also available via the Window > Web Inspector menu item (Cmd+Alt+I).

### Context Menu

The default browser context menu is suppressed on all windows. Apps control what appears on right-click.

#### `nativeWindow.onContextMenu(callback): () => void`

Subscribe to right-click events. Returns an unsubscribe function.

```typescript
type ContextMenuInfo = {
  linkUrl: string | null;
  imageUrl: string | null;
  selectedText: string | null;
  isEditable: boolean;
  x: number; y: number;
};
```

Two approaches:

**Custom HTML menu** (Discord-style) — render your own menu component at the click coordinates:

```typescript
nativeWindow.onContextMenu(info => {
  showMyCustomMenu(info.x, info.y, info);
});
```

**Native OS menu** (VS Code-style) — pop a platform context menu:

```typescript
nativeWindow.onContextMenu(info => {
  nativeWindow.showContextMenu([
    { label: 'Copy', action: 'copy' },
    'separator',
    { label: 'Select All', action: 'select-all' },
  ], { x: info.x, y: info.y });
});
```

#### `nativeWindow.showContextMenu(items, position): Promise<void>`

Show a native OS context menu (NSMenu on macOS). Each item's `action` string routes through the action system. Use `'separator'` for divider lines.

```typescript
await nativeWindow.showContextMenu([
  { label: 'Cut', action: 'edit:cut' },
  { label: 'Copy', action: 'edit:copy' },
  { label: 'Paste', action: 'edit:paste' },
  'separator',
  { label: 'Delete', action: 'edit:delete' },
], { x: 100, y: 200 });
```

### Window Events

#### `nativeWindow.on(event, callback): () => void`

Subscribe to window lifecycle events. Returns an unsubscribe function.

```typescript
type WindowEvent =
  | 'focus' | 'blur' | 'minimize' | 'restore'
  | 'enter-full-screen' | 'leave-full-screen'
  | 'moved' | 'resized';
```

```typescript
const unsub = nativeWindow.on('resized', ({ width, height }) => {
  console.log(`Window resized to ${width}x${height}`);
});

nativeWindow.on('focus', () => console.log('Window focused'));
nativeWindow.on('blur', () => console.log('Window lost focus'));
```

Event data:
- `moved`: `{ x: number, y: number }`
- `resized`: `{ width: number, height: number }`
- All others: no data

---

## `platform`

```typescript
import { platform } from "@keystone/sdk/bridge";
```

Platform detection and capability queries.

### `platform.os: 'macos' | 'linux' | 'windows'`

Current platform identifier. Read-only.

### `platform.isSupported(feature): boolean`

Check if a capability is available on the current platform.

Available features: `fullscreen`, `opacity`, `minMaxSize`, `aspectRatio`, `contentProtection`, `clickThrough`, `singleInstance`, `protocolHandler`, `openFile`, `openUrl`, `parentChild`, `customScheme`, `navigationPolicy`, `requestInterception`, `diagnostics`, `crashReporting`, `webWorker`

```typescript
if (platform.isSupported('contentProtection')) {
  await nativeWindow.setContentProtection(true);
}
```

### Browser Service Worker Management

These methods control the browser-native `navigator.serviceWorker` API — useful for managing offline caching and Cache Storage. They live under `platform` because they're platform-level maintenance operations.

#### `platform.swStatus(): Promise<ServiceWorkerStatus>`

Get the browser service worker registration status for this window's origin.

```typescript
type ServiceWorkerStatus = {
  active: boolean;
  waiting: boolean;
  installing: boolean;
  scope: string | null;
  scriptURL: string | null;
};

const sw = await platform.swStatus();
if (sw.active) console.log(`SW active: ${sw.scriptURL}`);
```

**Requires `customScheme: true`** for service workers to persist across restarts. Without it, the origin is `http://127.0.0.1:{random_port}` — different every launch, orphaning registrations.

#### `platform.swUnregister(): Promise<void>`

Unregister all browser service worker registrations for this origin.

#### `platform.swClearCaches(): Promise<void>`

Delete all Cache Storage entries for this origin.

```typescript
// Typical cache-bust flow
const sw = await platform.swStatus();
if (sw.active) {
  await platform.swClearCaches();
  await platform.swUnregister();
  location.reload();
}
```

---

## `webWorker`

```typescript
import { webWorker, WebWorker, workerSelf } from "@keystone/sdk/bridge";
```

Headless window workers with structured `postMessage`/`onMessage` communication. Unlike raw headless windows, web workers provide bidirectional messaging via WebSocket pub/sub (no C# round-trip for messages) and `evaluate()` with return values.

### `webWorker.spawn(component): Promise<WebWorker>`

Spawn a worker running the given component in a headless window. Returns a `WebWorker` instance.

```typescript
const worker = await webWorker.spawn('my-worker-component');
```

### `webWorker.list(): Promise<string[]>`

Returns IDs of all active web workers.

### `WebWorker` class

```typescript
class WebWorker {
  readonly id: string;
  readonly component: string;
  postMessage(data: any): void;
  onMessage(callback: (data: any) => void): () => void;
  evaluate<T = any>(js: string): Promise<T>;
  terminate(): Promise<void>;
}
```

#### `worker.postMessage(data): void`

Send structured data to the worker. Published to `worker:{id}:in` channel.

#### `worker.onMessage(callback): () => void`

Subscribe to messages from the worker. Returns an unsubscribe function.

#### `worker.evaluate(js): Promise<T>`

Evaluate JavaScript in the worker's WebView and return the result. Unlike `headless.evaluate()`, this returns a value.

```typescript
const title = await worker.evaluate('document.title');
const result = await worker.evaluate('computeExpensiveThing()');
```

#### `worker.terminate(): Promise<void>`

Terminate the worker. Cleans up subscriptions and closes the headless window.

### `workerSelf`

For use inside the worker component — provides the worker's side of the message channel.

```typescript
import { workerSelf } from "@keystone/sdk/bridge";

workerSelf.onMessage((data) => {
  const result = processData(data);
  workerSelf.postMessage(result);
});
```

#### `workerSelf.postMessage(data): void`

Send a message to the parent that spawned this worker.

#### `workerSelf.onMessage(callback): () => void`

Subscribe to messages from the parent. Returns an unsubscribe function.

### Example

```typescript
// Parent component
import { webWorker } from "@keystone/sdk/bridge";

const worker = await webWorker.spawn('data-processor');

worker.onMessage((result) => {
  console.log('Processed:', result);
});

worker.postMessage({ type: 'process', data: largeDataSet });

// Later
await worker.terminate();
```

```typescript
// Worker component (data-processor)
import { workerSelf } from "@keystone/sdk/bridge";

export function mount(root: HTMLElement) {
  workerSelf.onMessage((msg) => {
    if (msg.type === 'process') {
      const result = heavyComputation(msg.data);
      workerSelf.postMessage(result);
    }
  });
}
```

### Communication Architecture

Messages flow through WebSocket pub/sub — C# is only involved for lifecycle operations.

| Operation | Transport | C# involved? |
|---|---|---|
| `postMessage` (either direction) | WebSocket pub/sub | No |
| `spawn` | C# invoke | Yes |
| `evaluate` | C# invoke | Yes |
| `terminate` | C# invoke | Yes |
| `list` | C# invoke | Yes |

Channels: `worker:{id}:in` (parent → worker), `worker:{id}:out` (worker → parent).

---

## `diagnostics`

```typescript
import { diagnostics } from "@keystone/sdk/bridge";
```

Unified crash/event telemetry and process health monitoring. All crash sources — C# unhandled exceptions, unobserved `Task` faults, Bun subprocess crashes, WebKit content process terminations, and render thread exceptions — funnel through a single pipeline. Crash events are both kept in memory (last 100) and written to disk as structured JSON artifacts.

### Crash Event Types

| `kind` | Source | When |
|--------|--------|------|
| `unhandled_exception` | `AppDomain.UnhandledException` | An exception propagates out of all user code |
| `unobserved_task` | `TaskScheduler.UnobservedTaskException` | A faulted `Task` is garbage collected without observation |
| `bun_crash` | Bun subprocess exits | Bun process terminates unexpectedly. `extra.exitCode` available |
| `webview_crash` | `WKNavigationDelegate.ContentProcessDidTerminate` | WebKit content process OOM or watchdog kill. `extra.windowId` available |
| `render_exception` | `WindowRenderThread` catch block | Uncaught exception during Skia/Metal render. `extra.windowId` available |

```typescript
type CrashEvent = {
  kind: string;
  timestamp: string;       // ISO 8601 UTC
  message: string | null;  // Exception.Message (null for process crashes)
  stackTrace: string | null;
  processId: number;       // C# host PID
  extra: Record<string, string> | null; // kind-specific metadata
};
```

### `diagnostics.getCrashes(): Promise<CrashEvent[]>`

Returns recent crash events (last 100, kept in C# memory). Crash JSON artifacts are also written to `~/.keystone/crashes/` with filenames like `crash-20260303-142159-bun_crash.json`. Old artifacts are pruned to the last 50 on each write.

```typescript
const crashes = await diagnostics.getCrashes();
crashes.forEach(c => console.log(`[${c.kind}] ${c.message} @ ${c.timestamp}`));
```

### `diagnostics.onCrash(callback): () => void`

Subscribe to crash events in real-time via WebSocket push. Fires the moment a crash is reported — before the artifact is written to disk. Returns an unsubscribe function.

```typescript
const unsub = diagnostics.onCrash(event => {
  console.error(`[${event.kind}] ${event.message}`);
  if (event.extra?.windowId) console.error(`  Window: ${event.extra.windowId}`);
  if (event.stackTrace) console.error(event.stackTrace);
});
```

### `diagnostics.health(): Promise<HealthSummary>`

Returns a point-in-time health snapshot of the running C# host process. Memory is the physical footprint (RSS), not managed heap.

```typescript
const h = await diagnostics.health();
console.log(`Uptime: ${h.uptimeMs}ms, Memory: ${(h.memoryBytes / 1e6).toFixed(1)}MB`);
```

```typescript
type HealthSummary = {
  uptimeMs: number;       // Milliseconds since ApplicationRuntime.Initialize()
  memoryBytes: number;    // Physical memory footprint (phys_footprint on macOS)
  bunRunning: boolean;    // Whether the Bun subprocess is alive
  windowCount: number;    // Number of active ManagedWindows
  recentCrashes: number;  // CrashReporter.Recent.Count
};
```

### C# Integration

On the C# side, `CrashReporter` is a static class in `Keystone.Core`. Apps can subscribe to crashes directly:

```csharp
// Via ICoreContext (in your plugin)
ctx.OnCrash += evt => Log.Error($"Crash: {evt.Kind} — {evt.Message}");

// Or directly
CrashReporter.OnCrash += evt => SendToTelemetry(evt);
CrashReporter.Report("custom_error", ex, new() { ["context"] = "my-feature" });
```

---

## `webview`

```typescript
import { webview } from "@keystone/sdk/bridge";
```

WebView-level navigation policy and request interception. Navigation policy works on all configurations (blocks in-page navigations like `<a href="...">` clicks). Request interception requires the custom URL scheme, since it operates on the `WKURLSchemeHandler` that mediates all `keystone://` resource loads.

### `webview.setNavigationPolicy(blocked): Promise<void>`

Block the WebView from navigating to URLs that contain any of the given substrings. Internal URLs (`keystone://` and `http://127.0.0.1`) are always allowed regardless of the policy. The policy applies to all navigation types — link clicks, `window.location` assignments, form submissions, etc.

```typescript
// Block navigation to external sites
await webview.setNavigationPolicy(['facebook.com', 'tracking.net', 'ads.']);

// Allow everything (clear the blocklist)
await webview.setNavigationPolicy([]);
```

Implemented via `WKNavigationDelegate.DecidePolicy` — returning `WKNavigationActionPolicy.Cancel` for blocked URLs. This only prevents navigation; it does not block subresource loads (scripts, images, etc.) — use request interception for that.

### `webview.setRequestInterceptor(rules): Promise<void>`

Set request interception rules on the `KeystoneSchemeHandler`. **Requires `customScheme: true`** — returns an error if the scheme handler isn't active. Rules are evaluated in order against the request URL; first match wins. Unmatched requests proxy to the Bun HTTP server as usual.

```typescript
type RequestInterceptRule = {
  pattern: string;                          // URL substring to match
  action: 'block' | 'redirect' | 'allow';  // What to do on match
  target?: string;                          // Redirect destination (required for 'redirect')
};
```

```typescript
// Block analytics, redirect old API paths
await webview.setRequestInterceptor([
  { pattern: 'analytics.js', action: 'block' },
  { pattern: 'tracker.min.js', action: 'block' },
  { pattern: '/api/v1/', action: 'redirect', target: '/api/v2/' },
  { pattern: '/legacy/', action: 'redirect', target: '/modern/' },
]);

// Clear all rules (everything proxies to Bun normally)
await webview.setRequestInterceptor([]);
```

| Action | Behavior |
|--------|----------|
| `block` | Returns HTTP 403 with body "Blocked by request interceptor" |
| `redirect` | Returns HTTP 302 with `Location` header set to `target` |
| `allow` | Explicitly passes through (useful for whitelisting specific URLs before broader rules) |

**How it works under the hood:** The `KeystoneSchemeHandler` (C#) intercepts every `{schemeName}://app/*` request via `IWKUrlSchemeHandler.StartUrlSchemeTask`. Before proxying to Bun, it checks the `OnIntercept` callback. Rules set via this API configure that callback to match URL substrings and return the appropriate `SchemeResponse` (403, 302, or passthrough). Unmatched requests are forwarded to `http://127.0.0.1:{bunPort}/{path}` via `HttpClient`.

### C# Integration

For more complex interception logic (custom response bodies, authentication injection, content rewriting), use the scheme handler's `OnIntercept` hook directly in C#:

```csharp
// In your plugin's Initialize():
#if MACOS
if (ctx is ApplicationRuntime runtime)
{
    // Access the scheme handler's OnIntercept for full control
    // Return a SchemeResponse to override, or null to proxy to Bun
    schemeHandler.OnIntercept = (url, method) =>
    {
        if (url.Contains("/admin/") && !IsAuthenticated())
            return SchemeResponse.Redirect($"{_schemeHandler.Origin}/login");
        if (url.EndsWith(".map"))
            return SchemeResponse.Blocked(); // no source maps in production
        return null; // proxy to Bun
    };
}
#endif
```

`SchemeResponse` helpers:

| Factory | Response |
|---------|----------|
| `SchemeResponse.Html(html, statusCode?)` | HTML body, `text/html` |
| `SchemeResponse.Json(json, statusCode?)` | JSON body, `application/json` |
| `SchemeResponse.Redirect(url)` | 302 with `Location` header |
| `SchemeResponse.Blocked()` | 403 "Blocked by request interceptor" |

---

## Custom URL Scheme

Set `customScheme: true` in `keystone.config.json` to serve web content via a custom URL scheme instead of `http://127.0.0.1:{port}/`. This registers a `WKURLSchemeHandler` (macOS) that intercepts all requests for the app's scheme.

The scheme name defaults to a sanitized version of the app's `id` field (dots → hyphens). For example, `"id": "com.myapp.desktop"` produces the origin `com-myapp-desktop://app`. You can override it with the `schemeName` field.

```json
{
  "id": "com.myapp.desktop",
  "customScheme": true,
  "schemeName": "myapp"
}
```

With the above config, the origin is `myapp://app`. If `schemeName` is omitted, it would be `com-myapp-desktop://app`.

### What it enables

| Capability | Without custom scheme | With custom scheme |
|---|---|---|
| **Origin** | `http://127.0.0.1:{random_port}` — changes every launch | `{schemeName}://app` — stable across restarts |
| **Service workers** | Orphaned on restart (origin changed) | Persist across restarts |
| **Cache Storage** | Lost on restart | Persists |
| **Request interception** | Not available | All resource loads flow through C# |
| **Navigation policy** | Available | Available |
| **Origin isolation** | Shared port — any local process can access | Only accessible inside the WKWebView |

### How requests flow

```
Browser requests {schemeName}://app/dashboard.js
  → WKURLSchemeHandler.StartUrlSchemeTask(task)
    → Check OnIntercept callback
      → If intercepted: respond with SchemeResponse (block/redirect/custom)
      → If not intercepted: proxy to http://127.0.0.1:{bunPort}/dashboard.js via HttpClient
    → task.DidReceiveResponse(NSHttpUrlResponse)
    → task.DidReceiveData(NSData)
    → task.DidFinish()
```

### WebSocket connections

WebSocket connections are unaffected by the custom scheme. They continue using absolute URLs (`ws://127.0.0.1:{port}/ws`), which work regardless of the page origin. WebSocket is not subject to CORS, and Bun's server accepts connections from any origin.

### MIME type resolution

The scheme handler resolves MIME types from the proxied response's `Content-Type` header. If missing, it falls back to extension-based guessing: `.html` → `text/html`, `.js`/`.mjs` → `application/javascript`, `.css` → `text/css`, `.wasm` → `application/wasm`, etc.

---

## `dialog`

```typescript
import { dialog } from "@keystone/sdk/bridge";
```

All dialogs are native platform panels. They run on the main thread and block until dismissed.

### `dialog.openFile(opts?): Promise<string[] | null>`

Shows a native open-file panel. Returns selected paths, or `null` if cancelled.

```typescript
const paths = await dialog.openFile({
    title: "Select images",
    filters: [".png", ".jpg", ".webp"],
    multiple: true,
});
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `title` | `string` | `""` | Panel message text |
| `filters` | `string[]` | none | File extensions to allow |
| `multiple` | `boolean` | `false` | Allow selecting multiple files |

### `dialog.saveFile(opts?): Promise<string | null>`

Shows a native save-file panel. Returns the chosen path, or `null` if cancelled.

```typescript
const path = await dialog.saveFile({
    title: "Export as",
    defaultName: "export.json",
});
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `title` | `string` | `""` | Panel message text |
| `defaultName` | `string` | `""` | Pre-filled filename |

### `dialog.showMessage(opts): Promise<number>`

Shows a native alert dialog. Returns the zero-based index of the button clicked.

```typescript
const clicked = await dialog.showMessage({
    title: "Delete file?",
    message: "This cannot be undone.",
    buttons: ["Delete", "Cancel"],
});
if (clicked === 0) deleteFile();
```

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `title` | `string` | — | Alert message text (large, bold) |
| `message` | `string` | — | Informative text (smaller, below title) |
| `buttons` | `string[]` | `["OK"]` | Button labels, left to right |

---

## `external`

```typescript
import { external } from "@keystone/sdk/bridge";
```

Opens files and URLs outside the app — in whatever the OS considers the default handler.

### `external.url(url): void`

Opens a URL outside the app. Fire-and-forget. Any scheme the OS can handle — `https://`, `mailto:`, `file://`, etc.

### `external.path(path): Promise<boolean>`

Opens a file or directory outside the app with its default application. Returns `true` on success.

---

## `clipboard`

```typescript
import { clipboard } from "@keystone/sdk/bridge";
```

Cross-platform clipboard access (plain text UTF-8).

| Platform | Mechanism |
|----------|-----------|
| macOS | `NSPasteboard` |
| Windows | Win32 `CF_UNICODETEXT` |
| Linux | `wl-paste`/`wl-copy` (Wayland), `xclip` (X11 fallback) |

### `clipboard.readText(): Promise<string | null>`
### `clipboard.writeText(text): Promise<void>`
### `clipboard.clear(): Promise<void>`
### `clipboard.hasText(): Promise<boolean>`

---

## `screen`

```typescript
import { screen, DisplayInfo } from "@keystone/sdk/bridge";
```

Multi-monitor display information.

```typescript
type DisplayInfo = {
  x: number;         // origin x in screen coordinates
  y: number;
  width: number;     // logical pixels
  height: number;
  scaleFactor: number; // DPI multiplier (e.g. 2.0 on Retina)
  primary: boolean;
};
```

| Platform | Mechanism |
|----------|-----------|
| macOS | `NSScreen.Screens` |
| Windows | `EnumDisplayMonitors` |
| Linux | GDK4 `GListModel` monitor enumeration |

### `screen.getAllDisplays(): Promise<DisplayInfo[]>`
### `screen.getPrimaryDisplay(): Promise<DisplayInfo>`
### `screen.getCursorScreenPoint(): Promise<{ x: number; y: number }>`

---

## `notification`

```typescript
import { notification } from "@keystone/sdk/bridge";
```

OS-level notifications — appears in the system notification center.

| Platform | Mechanism |
|----------|-----------|
| macOS | `osascript display notification` |
| Linux | `notify-send` |
| Windows | `MessageBox` (tray balloon planned) |

### `notification.show(title, body): Promise<void>`

```typescript
await notification.show("Build complete", "Your app compiled in 2.3s");
```

---

## `darkMode`

```typescript
import { darkMode } from "@keystone/sdk/bridge";
```

System dark/light mode detection.

| Platform | Mechanism |
|----------|-----------|
| macOS | `NSApplication.SharedApplication.EffectiveAppearance` |
| Windows | Registry `AppsUseLightTheme` |
| Linux | `gsettings get org.gnome.desktop.interface color-scheme` |

### `darkMode.isDark(): Promise<boolean>`

### `darkMode.onChange(callback): () => void`

Fires immediately with the current value, then on every subsequent change. Returns an unsubscribe function.

```typescript
const unsub = darkMode.onChange((dark) => {
  document.documentElement.classList.toggle("dark", dark);
});
```

---

## `battery`

```typescript
import { battery, BatteryStatus } from "@keystone/sdk/bridge";
```

```typescript
type BatteryStatus = {
  onBattery: boolean;
  batteryPercent: number; // 0–100, or -1 when unknown
};
```

| Platform | Mechanism |
|----------|-----------|
| macOS | `pmset -g ps` subprocess |
| Windows | `GetSystemPowerStatus` |
| Linux | `/sys/class/power_supply` |

### `battery.status(): Promise<BatteryStatus>`
### `battery.onChange(callback): () => void`

---

## `hotkey`

```typescript
import { hotkey } from "@keystone/sdk/bridge";
```

Process-wide keyboard shortcuts that fire even when your windows don't have focus.

**Accelerator format:** modifiers joined with `+`, then the key: `CommandOrControl+Shift+P`, `Alt+F4`, `F5`

Supported modifiers: `Control`/`Ctrl`, `Shift`, `Alt`/`Option`, `Meta`/`Command`/`Cmd`, `CommandOrControl`/`CtrlOrCmd`

Supported keys: `A`–`Z`, `0`–`9`, `F1`–`F12`, `Enter`/`Return`, `Escape`/`Esc`, `Space`, `Tab`, `Delete`/`Backspace`, arrow keys

| Platform | Mechanism |
|----------|-----------|
| macOS | Carbon `RegisterEventHotKey` |
| Windows | Win32 `RegisterHotKey` + `WM_HOTKEY` message thread |
| Linux | Stub (Wayland GlobalShortcuts portal deferred) |

### `hotkey.register(accelerator): Promise<boolean>`

Returns `true` if successful, `false` if already claimed or unsupported.

### `hotkey.unregister(accelerator): Promise<void>`

### `hotkey.isRegistered(accelerator): Promise<boolean>`

### `hotkey.on(accelerator, callback): () => void`

Must call `register()` first. Returns an unsubscribe function.

```typescript
await hotkey.register("CommandOrControl+Shift+P");
const unsub = hotkey.on("CommandOrControl+Shift+P", () => {
  openCommandPalette();
});
```

---

## `headless`

```typescript
import { headless } from "@keystone/sdk/bridge";
```

Headless windows are invisible native windows with a full WebKit view — never shown on screen. Use for background JS execution, scripted rendering, test harnesses, or pre-rendering.

```json
{ "windows": [{ "type": "renderer", "headless": true, "width": 1280, "height": 720 }] }
```

### `headless.open(component): Promise<string>`

Returns the new window's ID.

### `headless.evaluate(windowId, js): Promise<void>`

Execute JavaScript in a headless window's WebView context. Fire-and-forget — use channel pushes to get results back.

### `headless.list(): Promise<string[]>`

Returns IDs of all running headless windows.

### `headless.close(windowId): Promise<void>`

---

## `fetch("/api/...")` Intercept

The bridge patches `window.fetch` to intercept paths starting with `/api/`. These route through the C# `HttpRouter` via `invoke()` — no real network request.

```typescript
const data = await fetch("/api/notes").then(r => r.json());

const res = await fetch("/api/notes", {
  method: "POST",
  body: JSON.stringify({ title: "New note" }),
});
```

Streaming responses are automatically assembled into a `ReadableStream`. Non-`/api/` paths fall through to real `fetch()`.

---

## Theme CSS Variables

The host pushes the active theme to every window on connect and whenever it changes. Tokens are applied as CSS custom properties on `:root`:

| Variable | Purpose |
|----------|---------|
| `--ks-bg-base` | Deepest background layer |
| `--ks-bg-surface` | Default panel/card background |
| `--ks-bg-elevated` | Raised elements (dropdowns, modals) |
| `--ks-bg-chrome` | Window chrome / sidebar |
| `--ks-bg-strip` | Table rows, list items |
| `--ks-bg-hover` | Hover state |
| `--ks-bg-pressed` | Active/pressed state |
| `--ks-bg-button` | Button background |
| `--ks-bg-button-hover` | Button hover |
| `--ks-bg-button-dark` | Dark variant button |
| `--ks-bg-medium` | Mid-tone fill |
| `--ks-bg-light` | Light fill |
| `--ks-stroke` | Border/outline |
| `--ks-divider` | Separator lines |
| `--ks-accent` | Brand accent color |
| `--ks-accent-bright` | High-visibility accent |
| `--ks-accent-header` | Header accent |
| `--ks-success` | Success state |
| `--ks-warning` | Warning state |
| `--ks-danger` | Destructive / error state |
| `--ks-text-primary` | Primary body text |
| `--ks-text-secondary` | Secondary / subdued text |
| `--ks-text-muted` | Muted labels |
| `--ks-text-subtle` | Barely-visible hint text |
| `--ks-font` | System font stack |

```typescript
keystone().onThemeChange((theme) => {
  console.log("New accent:", theme.accent);
});
```

---

## Low-level Bridge API

The `keystone()` singleton provides low-level access. Prefer `ipc` for cross-process calls.

```typescript
const ks = keystone();
```

| Method | Description |
|--------|-------------|
| `ks.invoke<T>(channel, args?, options?)` | C# invoke — use `ipc.host.call()` instead |
| `ks.action(action)` | Fire-and-forget action — use `ipc.host.action()` instead |
| `ks.subscribe(channel, cb)` | Channel subscribe — use `ipc.subscribe()` instead |
| `ks.query(service, args?)` | Bun service query — use `ipc.bun.query()` instead |
| `ks.send(type, data?)` | Raw typed WebSocket message |
| `ks.onConnect(cb)` | Fires on WebSocket connect (or immediately if already connected) |
| `ks.onAction(cb)` | Fires when action dispatched to web layer from C# |
| `ks.onThemeChange(cb)` | Fires on theme update |

State properties:

```typescript
ks.connected   // boolean — WebSocket connection state
ks.port        // number — Bun HTTP server port
ks.windowId    // string — ID of the native window
ks.theme       // Theme — current theme token values
```

---

## Registering Custom Handlers (C#)

```csharp
window.RegisterInvokeHandler("myapp:readFile", async args =>
{
    var path = args.GetProperty("path").GetString()!;
    return await File.ReadAllTextAsync(path);
});

window.RegisterInvokeHandler("myapp:writeFile", async args =>
{
    var path = args.GetProperty("path").GetString()!;
    var content = args.GetProperty("content").GetString()!;
    await File.WriteAllTextAsync(path, content);
    return null;
});
```

Custom handlers follow the same rules as built-in ones:
- `JsonElement` input (the `args` from JS, or `default` if none)
- Return value is JSON-serialized as `reply.result`
- Throwing delivers `reply.error` (rejects the JS promise)
- Handlers run on a thread pool thread — dispatch to main thread for platform UI APIs

---

## Built-in Actions

These action strings are handled directly by the C# runtime — no registration needed:

| Action | Effect |
|--------|--------|
| `window:minimize` | Minimize current window |
| `window:maximize` | Maximize current window |
| `window:close` | Close current window |
| `app:quit` | Quit the application |
| `external:url:<url>` | Open URL in default browser |
