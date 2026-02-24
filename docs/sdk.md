# Keystone SDK Reference

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

---

### `invoke<T>(channel, args?): Promise<T>`

Invoke a C# handler by channel name. Replies come back via a one-shot WebSocket subscription. Timeout: 15 seconds.

```typescript
const result = await invoke<string>('app:getVersion');
const ok = await invoke<boolean>('clipboard:hasText');
```

Requires WKWebView (`webkit.messageHandlers.keystone` must be available). Rejects immediately in a regular browser context.

---

### `invokeBun<T>(channel, args?): Promise<T>`

Invoke a Bun service handler by channel name. Goes over WebSocket — works in any environment, no WKWebView required. Timeout: 15 seconds.

```typescript
// Bun side
defineService("notes").handle("notes:getAll", async (_, svc) => svc.store.get("notes"));

// Browser side
const notes = await invokeBun<Note[]>("notes:getAll");
```

---

### `subscribe(channel, callback): () => void`

Subscribe to a named data channel pushed from C# or Bun. The last received value is replayed immediately to new subscribers. Returns an unsubscribe function.

```typescript
const unsub = subscribe('myChannel', (data) => console.log(data));
// later:
unsub();
```

---

### `action(name): void`

Dispatch an action string to the C# host and all connected Bun services. Fire-and-forget — no reply.

```typescript
action('app:quit');
action('window:minimize');
```

---

### `query(service, args?): Promise<any>`

Query a Bun service. Returns a promise with the result. Queued if the WebSocket isn't connected yet. Timeout: 10 seconds.

---

## `app`

```typescript
import { app } from "@keystone/sdk/bridge";
```

### `app.getName(): Promise<string>`

Returns the app name from `keystone.json`.

### `app.getVersion(): Promise<string>`

Returns the app version from `keystone.json`.

### `app.getPath(name): Promise<string>`

Returns a well-known filesystem path.

| `name` | Resolves to |
|--------|------------|
| `userData` | `~/Library/Application Support/<app-name>` |
| `documents` | `~/Documents` |
| `downloads` | `~/Downloads` |
| `desktop` | `~/Desktop` |
| `temp` | `/tmp` |
| `appRoot` | Root directory of the app (next to `keystone.json`) |

### `app.quit(): void`

Terminates the application. Fire-and-forget via `action()`.

---

## `nativeWindow`

```typescript
import { nativeWindow } from "@keystone/sdk/bridge";
```

All methods target the **current window** — the native window that owns the WebKit slot this component is mounted in.

### `nativeWindow.setTitle(title): Promise<void>`

### `nativeWindow.minimize(): void` / `maximize(): void` / `close(): void`

Fire-and-forget window controls.

### `nativeWindow.open(type): Promise<string>`

Open a new window of the given registered type. Returns the new window's ID.

```typescript
const id = await nativeWindow.open("settings");
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

### `dialog.openFile(opts?): Promise<string[] | null>`

Show a native open-file panel. Returns selected paths, or `null` if cancelled.

```typescript
const paths = await dialog.openFile({ title: "Open Image", filters: ["png", "jpg"], multiple: false });
```

### `dialog.saveFile(opts?): Promise<string | null>`

Show a native save-file panel. Returns the chosen path, or `null` if cancelled.

```typescript
const path = await dialog.saveFile({ title: "Export", filters: ["pdf"], defaultName: "export.pdf" });
```

### `dialog.showMessage(opts): Promise<number>`

Show a native alert/confirm dialog. Returns the index of the clicked button.

```typescript
const btn = await dialog.showMessage({
  title: "Delete item?",
  message: "This cannot be undone.",
  buttons: ["Cancel", "Delete"],
});
if (btn === 1) /* delete */;
```

---

## `shell`

```typescript
import { shell } from "@keystone/sdk/bridge";
```

### `shell.openExternal(url): void`

Open a URL in the system default browser. Fire-and-forget.

```typescript
shell.openExternal("https://example.com");
```

### `shell.openPath(path): Promise<boolean>`

Open a file or directory with its default application. Returns `true` on success.

```typescript
const ok = await shell.openPath("/Users/me/Documents/report.pdf");
```

---

## `clipboard`

```typescript
import { clipboard } from "@keystone/sdk/bridge";
```

Cross-platform clipboard access. Reads and writes plain text (UTF-8).

| Platform | Mechanism |
|----------|-----------|
| macOS | `NSPasteboard` |
| Windows | Win32 `CF_UNICODETEXT` |
| Linux | `wl-paste`/`wl-copy` (Wayland), `xclip` (X11 fallback) |

### `clipboard.readText(): Promise<string | null>`

Read plain text from the clipboard. Returns `null` if empty or unavailable.

```typescript
const text = await clipboard.readText();
if (text) console.log("Clipboard:", text);
```

### `clipboard.writeText(text): Promise<void>`

Write plain text to the clipboard.

```typescript
await clipboard.writeText("Hello, clipboard!");
```

### `clipboard.clear(): Promise<void>`

Clear the clipboard.

### `clipboard.hasText(): Promise<boolean>`

Returns `true` if the clipboard contains text content.

```typescript
const btn = document.querySelector("#paste-btn");
btn.disabled = !(await clipboard.hasText());
```

---

## `screen`

```typescript
import { screen, DisplayInfo } from "@keystone/sdk/bridge";
```

Multi-monitor display information.

```typescript
export type DisplayInfo = {
  x: number;         // origin x in screen coordinates
  y: number;         // origin y in screen coordinates
  width: number;     // width in logical pixels
  height: number;    // height in logical pixels
  scaleFactor: number; // DPI multiplier (e.g. 2.0 on Retina)
  primary: boolean;  // true for the main/primary display
};
```

| Platform | Mechanism |
|----------|-----------|
| macOS | `NSScreen.Screens` |
| Windows | `EnumDisplayMonitors` |
| Linux | GDK4 `GListModel` monitor enumeration |

### `screen.getAllDisplays(): Promise<DisplayInfo[]>`

Returns an array of all connected displays.

```typescript
const displays = await screen.getAllDisplays();
console.log(`${displays.length} display(s) connected`);
```

### `screen.getPrimaryDisplay(): Promise<DisplayInfo>`

Returns the primary (main) display.

### `screen.getCursorScreenPoint(): Promise<{ x: number; y: number }>`

Returns the current cursor position in screen coordinates.

```typescript
const pos = await screen.getCursorScreenPoint();
console.log(`Cursor at (${pos.x}, ${pos.y})`);
```

---

## `notification`

```typescript
import { notification } from "@keystone/sdk/bridge";
```

OS-level notifications — no UI framework required, appears in the system notification center.

| Platform | Mechanism |
|----------|-----------|
| macOS | `osascript display notification` |
| Linux | `notify-send` |
| Windows | `MessageBox` (tray balloon planned) |

### `notification.show(title, body): Promise<void>`

```typescript
await notification.show("Build complete", "Your app compiled in 2.3s");
```

On macOS the notification appears in Notification Center. On Linux, `notify-send` must be installed (available by default on GNOME/KDE). The promise resolves once the system call returns (delivery is async — the OS queues it).

---

## `nativeTheme`

```typescript
import { nativeTheme } from "@keystone/sdk/bridge";
```

System dark/light mode detection.

| Platform | Mechanism |
|----------|-----------|
| macOS | `NSApplication.SharedApplication.EffectiveAppearance` |
| Windows | Registry `AppsUseLightTheme` |
| Linux | `gsettings get org.gnome.desktop.interface color-scheme` |

### `nativeTheme.isDarkMode(): Promise<boolean>`

One-shot check — returns the current system appearance.

```typescript
const dark = await nativeTheme.isDarkMode();
document.documentElement.classList.toggle("dark", dark);
```

### `nativeTheme.onChange(callback): () => void`

Subscribe to system theme changes. The callback fires **immediately** with the current value, then on every subsequent change. Returns an unsubscribe function.

```typescript
const unsub = nativeTheme.onChange((dark) => {
  document.documentElement.classList.toggle("dark", dark);
});
// on component unmount:
unsub();
```

> The C# host must push to the `__nativeTheme__` channel (e.g. from an `NSDistributedNotificationCenter` observer) for `onChange` to fire. `isDarkMode()` always reflects the current value.

---

## `powerMonitor`

```typescript
import { powerMonitor, PowerStatus } from "@keystone/sdk/bridge";
```

Battery and power state.

```typescript
export type PowerStatus = {
  onBattery: boolean;    // true when on battery; false on AC or unknown
  batteryPercent: number; // 0–100, or -1 when unknown (desktop / AC-only)
};
```

| Platform | Mechanism |
|----------|-----------|
| macOS | `pmset -g ps` subprocess |
| Windows | `GetSystemPowerStatus` |
| Linux | `/sys/class/power_supply` |

### `powerMonitor.getStatus(): Promise<PowerStatus>`

One-shot check.

```typescript
const status = await powerMonitor.getStatus();
if (status.onBattery && status.batteryPercent < 20) {
  await notification.show("Low battery", `${status.batteryPercent}% remaining`);
}
```

### `powerMonitor.onChange(callback): () => void`

Subscribe to power state changes pushed by C#. Returns an unsubscribe function.

```typescript
const unsub = powerMonitor.onChange((status) => {
  setBatteryIcon(status.onBattery ? "battery" : "plug");
});
```

> Change events require the C# host to push to the `__powerMonitor__` channel (e.g. on a polling timer or OS power notification). `getStatus()` always reflects the current value.

---

## `globalShortcut`

```typescript
import { globalShortcut } from "@keystone/sdk/bridge";
```

Process-wide keyboard shortcuts that fire even when your windows don't have focus. Mirrors Electron's `globalShortcut` module.

**Accelerator format** — modifiers joined with `+`, then the key:

```
CommandOrControl+Shift+P
Alt+F4
Ctrl+Shift+K
F5
```

Supported modifiers: `Control`/`Ctrl`, `Shift`, `Alt`/`Option`, `Meta`/`Command`/`Cmd`, `CommandOrControl`/`CtrlOrCmd`

Supported keys: `A`–`Z`, `0`–`9`, `F1`–`F12`, `Enter`/`Return`, `Escape`/`Esc`, `Space`, `Tab`, `Delete`/`Backspace`, `Left`, `Right`, `Up`, `Down`

| Platform | Mechanism |
|----------|-----------|
| macOS | Carbon `RegisterEventHotKey` |
| Windows | Win32 `RegisterHotKey` + `WM_HOTKEY` message thread |
| Linux | Stub (returns `false` — Wayland GlobalShortcuts portal deferred) |

### `globalShortcut.register(accelerator): Promise<boolean>`

Register a global shortcut. Returns `true` if successful, `false` if the accelerator is already claimed by another process or unsupported on this platform.

```typescript
const ok = await globalShortcut.register("CommandOrControl+Shift+P");
if (!ok) console.warn("Shortcut already taken");
```

### `globalShortcut.unregister(accelerator): Promise<void>`

Release a previously registered shortcut.

```typescript
await globalShortcut.unregister("CommandOrControl+Shift+P");
```

### `globalShortcut.isRegistered(accelerator): Promise<boolean>`

Returns `true` if this process has registered the given accelerator.

### `globalShortcut.onFired(accelerator, callback): () => void`

Subscribe to a shortcut firing. The callback is called each time the OS delivers the hotkey event. Must call `register()` first. Returns an unsubscribe function.

```typescript
await globalShortcut.register("CommandOrControl+Shift+P");
const unsub = globalShortcut.onFired("CommandOrControl+Shift+P", () => {
  openCommandPalette();
});

// cleanup
unsub();
await globalShortcut.unregister("CommandOrControl+Shift+P");
```

---

## `headless`

```typescript
import { headless } from "@keystone/sdk/bridge";
```

Headless windows are invisible native windows with a full WebKit view — never shown on screen. Use them for:
- Background JavaScript execution
- Scripted rendering / PDF capture
- Test harnesses
- Pre-rendering content before a visible window opens

A headless window is a normal Keystone window with `headless: true` in its config entry. It runs the full web component stack but `Show()` is never called. The window has full access to the bridge — it can push to channels, call services, and be coordinated from other windows.

```json
// keystone.json
{
  "windows": [
    { "type": "renderer", "headless": true, "width": 1280, "height": 720 }
  ]
}
```

### `headless.open(component): Promise<string>`

Open a registered headless component. Returns the new window's ID.

```typescript
const id = await headless.open("renderer");
```

### `headless.evaluate(windowId, js): Promise<void>`

Fire-and-forget: execute JavaScript in a headless window's WebView context.

To get results back, have the headless window push to a channel:

```typescript
// in the headless component
const result = computeSomething();
subscribe("__result__", () => {}); // ensure bridge is alive
keystone().publish("__result__", { value: result });

// in the calling window
subscribe("__result__", ({ value }) => console.log(value));
await headless.evaluate(id, `keystone().publish('__result__', { value: doWork() })`);
```

### `headless.list(): Promise<string[]>`

Returns IDs of all currently running headless windows.

### `headless.close(windowId): Promise<void>`

Close a headless window by ID.

```typescript
await headless.close(id);
```

---

## Theme CSS Variables

The host pushes the active theme to every window on connect and whenever it changes. Theme tokens are automatically applied as CSS custom properties on `:root`:

```css
/* Usage in stylesheets */
background: var(--ks-bg-surface);
color: var(--ks-text-primary);
border-color: var(--ks-stroke);
accent-color: var(--ks-accent);
```

Full token list:

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

Subscribe to theme changes from JavaScript:

```typescript
keystone().onThemeChange((theme) => {
  console.log("New accent:", theme.accent);
});
```

---

## `fetch("/api/...")` Intercept

The bridge patches `window.fetch` to intercept calls to paths starting with `/api/`. These are routed through the C# `HttpRouter` via `invoke()` rather than making a real network request.

```typescript
// Works anywhere in your web component — no imports needed
const data = await fetch("/api/notes").then(r => r.json());

const res = await fetch("/api/notes", {
  method: "POST",
  body: JSON.stringify({ title: "New note" }),
});
```

C# side:

```csharp
// In your plugin or ApplicationRuntime
HttpRouter.Get("/api/notes", async ctx => {
    var notes = await _store.GetAllAsync("notes");
    return HttpResponse.Json(notes);
});

HttpRouter.Post("/api/notes", async ctx => {
    var note = ctx.Body<Note>();
    await _store.SetAsync($"note:{note.Id}", note);
    return HttpResponse.Ok();
});
```

Streaming responses (when C# returns a streaming body) are automatically assembled into a `ReadableStream`, so `response.body` works like a regular fetch response.

Non-`/api/` paths fall through to the real `fetch()`.

---

## `invokeBun` vs `invoke`

| | `invoke` | `invokeBun` |
|---|---|---|
| Target | C# `RegisterInvokeHandler` | Bun `defineService().handle()` |
| Transport | Direct postMessage → WKWebView message handler | WebSocket |
| Round-trip | Zero Bun hops | One Bun hop |
| Requires WKWebView | Yes | No |
| Timeout | 15s | 15s |
| Use for | Native OS APIs, window control | Business logic, data access |
