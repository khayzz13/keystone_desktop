# SDK Reference

> Last updated: 2026-02-28

The `@keystone/sdk/bridge` module is the client-side bridge between your web components (running in WKWebView) and the C# + Bun host process. It gives you typed access to native OS APIs, pub/sub channels, service queries, and lifecycle hooks.

```typescript
import { keystone, invoke, subscribe, app, nativeWindow, /* ... */ } from "@keystone/sdk/bridge";
```

---

## Core Primitives

### `keystone(): KeystoneClient`

Returns the singleton bridge client. Auto-connects to the Bun WebSocket on first call. All namespace helpers (`app`, `clipboard`, etc.) call this internally.

```typescript
const ks = keystone();
ks.connected; // boolean — whether WebSocket is live
ks.windowId;  // string — ID of the native window this component belongs to
ks.port;      // number — Bun HTTP server port
```

### `invoke<T>(channel, args?): Promise<T>`

Invoke a C# handler by channel name. Direct postMessage to WKWebView — no Bun round-trip. Timeout: 15 seconds.

```typescript
const result = await invoke<string>('app:getVersion');
const ok = await invoke<boolean>('clipboard:hasText');
```

### `invokeBun<T>(channel, args?): Promise<T>`

Invoke a Bun service handler by channel name. Goes over WebSocket. Timeout: 15 seconds.

```typescript
// Bun side
defineService("notes").handle("notes:getAll", async (_, svc) => svc.store.get("notes"));

// Browser side
const notes = await invokeBun<Note[]>("notes:getAll");
```

### `subscribe(channel, callback): () => void`

Subscribe to a named data channel pushed from C# or Bun. The last received value is replayed immediately to new subscribers. Returns an unsubscribe function.

```typescript
const unsub = subscribe('myChannel', (data) => console.log(data));
unsub();
```

### `action(name): void`

Dispatch an action string to the C# host and all connected Bun services. Fire-and-forget.

```typescript
action('app:quit');
action('window:minimize');
```

### `query(service, args?): Promise<any>`

Query a Bun service. Returns a promise with the result. Timeout: 10 seconds.

### `invoke` vs `invokeBun`

| | `invoke` | `invokeBun` |
|---|---|---|
| Target | C# `RegisterInvokeHandler` | Bun `defineService().handle()` |
| Transport | Direct postMessage → WKWebView | WebSocket |
| Round-trip | Zero Bun hops | One Bun hop |
| Requires WKWebView | Yes | No |
| Use for | Native OS APIs, window control | Business logic, data access |

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

### `nativeWindow.open(type): Promise<string>`

Spawns a new window of the given registered type. Returns the new window's ID. `type` must match a component in `keystone.json` `windows[]` or a native `IWindowPlugin.WindowType`.

```typescript
const settingsWindowId = await nativeWindow.open("settings");
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

### `nativeWindow.center(): Promise<void>`

Center the window on the main display.

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

Directly on the `KeystoneClient` instance:

```typescript
const ks = keystone();
```

| Method | Description |
|--------|-------------|
| `ks.invoke<T>(channel, args?)` | Raw invoke call — namespace helpers wrap this |
| `ks.action(action)` | Fire-and-forget action string |
| `ks.subscribe(channel, cb)` | Subscribe to named WebSocket channel |
| `ks.query(service, args?)` | Query a Bun service |
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
