# Native API Reference

The native API surface is accessible from web components via `invoke()` and `action()`. These are built-in handlers registered on every window by the C# runtime — no additional setup required.

All `invoke()` calls communicate directly with C# via `WKScriptMessageHandler`. There is no Bun round-trip. Handlers run on a thread pool thread so they don't block the main run loop.

---

## `app`

```typescript
import { app } from "@keystone/sdk/bridge";
```

### `app.getName(): Promise<string>`

Returns the app name from `keystone.json`.

```typescript
const name = await app.getName(); // "My App"
```

### `app.getVersion(): Promise<string>`

Returns the app version from `keystone.json`.

```typescript
const version = await app.getVersion(); // "1.0.0"
```

### `app.getPath(name): Promise<string>`

Returns a well-known filesystem path.

```typescript
const dataDir = await app.getPath("userData");
```

| `name` | Resolves to |
|--------|------------|
| `userData` | `~/Library/Application Support/<app-name>` |
| `documents` | `~/Documents` |
| `downloads` | `~/Downloads` |
| `desktop` | `~/Desktop` |
| `temp` | `/tmp` |
| `appRoot` | Root directory of the app (next to `keystone.json`) |

### `app.quit(): void`

Terminates the application. Triggers `ICorePlugin.OnShutdown` before exit.

```typescript
app.quit();
```

---

## `nativeWindow`

```typescript
import { nativeWindow } from "@keystone/sdk/bridge";
```

All window actions target the **current window** — the native `NSWindow` that owns the `WKWebView` slot this component is mounted in.

### `nativeWindow.setTitle(title): Promise<void>`

Sets the `NSWindow` title bar text.

```typescript
await nativeWindow.setTitle("My App — Untitled");
```

### `nativeWindow.minimize(): void`

Minimizes the window to the Dock. Fire-and-forget via `action()`.

### `nativeWindow.maximize(): void`

Zooms/maximizes the window (`NSWindow.Zoom`). Fire-and-forget.

### `nativeWindow.close(): void`

Closes the window. If it's the last window, the app continues running (macOS convention). Fire-and-forget.

### `nativeWindow.open(type): Promise<string>`

Spawns a new window of the given registered type. Returns the new window's ID.

```typescript
const settingsWindowId = await nativeWindow.open("settings");
```

`type` must match a component registered in `keystone.json` `windows[]` or a native `IWindowPlugin.WindowType`.

---

## `dialog`

```typescript
import { dialog } from "@keystone/sdk/bridge";
```

All dialogs are native macOS panels (`NSOpenPanel`, `NSSavePanel`, `NSAlert`). They run on the main thread and block until dismissed.

### `dialog.openFile(opts?): Promise<string[] | null>`

Shows a native open-file panel. Returns the selected file paths, or `null` if cancelled.

```typescript
const paths = await dialog.openFile({
    title: "Select images",
    filters: [".png", ".jpg", ".webp"],
    multiple: true,
});

if (paths) {
    for (const path of paths) { /* ... */ }
}
```

**Options:**

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `title` | `string` | `""` | Panel message text (appears above file browser) |
| `filters` | `string[]` | none | File extensions to allow, with or without leading dot |
| `multiple` | `boolean` | `false` | Allow selecting multiple files |

Returns `string[]` on confirmation, `null` on cancel.

### `dialog.saveFile(opts?): Promise<string | null>`

Shows a native save-file panel. Returns the chosen path, or `null` if cancelled.

```typescript
const path = await dialog.saveFile({
    title: "Export as",
    defaultName: "export.json",
});

if (path) await saveToFile(path, content);
```

**Options:**

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `title` | `string` | `""` | Panel message text |
| `defaultName` | `string` | `""` | Pre-filled filename in the name field |

Returns `string` on confirmation, `null` on cancel.

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

**Options:**

| Key | Type | Default | Description |
|-----|------|---------|-------------|
| `title` | `string` | — | Alert message text (large, bold) |
| `message` | `string` | — | Informative text (smaller, below title) |
| `buttons` | `string[]` | `["OK"]` | Button labels, left to right |

Returns `0` for the first button, `1` for second, etc. If no buttons are specified, shows a single "OK" button and returns `0`.

---

## `shell`

```typescript
import { shell } from "@keystone/sdk/bridge";
```

### `shell.openExternal(url): void`

Opens a URL in the system default browser. Fire-and-forget via `action()`.

```typescript
shell.openExternal("https://docs.example.com");
```

The URL is passed as-is to the OS. Any scheme the OS can handle works — `https://`, `mailto:`, `file://`, etc.

### `shell.openPath(path): Promise<boolean>`

Opens a file or directory with its default application (`NSWorkspace.OpenFile`). Returns `true` on success.

```typescript
const ok = await shell.openPath("/Users/me/Documents/report.pdf");
```

---

## Low-level bridge API

Directly on the `KeystoneClient` instance from `keystone()`:

```typescript
const ks = keystone();
```

### `ks.invoke<T>(channel, args?): Promise<T>`

Raw invoke call. The typed namespace helpers (`app`, `dialog`, etc.) wrap this. Use it directly for custom C# handlers:

```typescript
const result = await ks.invoke<{ count: number }>("myapp:scan", { dir: "/tmp" });
```

Internally:
1. Generates a unique request ID
2. Subscribes one-shot to `window:{windowId}:__reply__:{id}` over WebSocket
3. Posts `{ ks_invoke: true, id, channel, args, windowId }` via `webkit.messageHandlers.keystone`
4. C# dispatches to the registered handler, awaits result, pushes reply to the channel
5. Bridge resolves the promise

Times out after 15 seconds if no reply arrives.

### `ks.action(action): void`

Dispatches a fire-and-forget action string. Routes to C# `ActionRouter` and all Bun service `onAction` handlers.

```typescript
ks.action("myapp:new-document");
ks.action("window:minimize");
```

### `ks.subscribe(channel, callback): () => void`

Subscribes to a named WebSocket channel. Returns an unsubscribe function. If the channel already has cached data, the callback fires immediately with the last value.

```typescript
const unsub = ks.subscribe("prices:btc", (data) => {
    priceEl.textContent = `$${data.usd.toLocaleString()}`;
});

// Later:
unsub();
```

### `ks.query(service, args?): Promise<any>`

Queries a Bun service by name. Times out after 10 seconds.

```typescript
const files = await ks.query("file-scanner", { dir: "/tmp", extensions: [".log"] });
```

### `ks.send(type, data?): void`

Sends a raw typed message over the WebSocket. Services can register custom handlers with `ctx.onWebMessage(type, fn)`.

```typescript
ks.send("chat:send", { text: "Hello", room: "general" });
```

### `ks.onConnect(callback): () => void`

Fires when the WebSocket connects (or immediately if already connected). Use to re-subscribe after reconnect.

```typescript
ks.onConnect(() => {
    console.log("Bridge connected");
});
```

### `ks.onAction(callback): () => void`

Fires when an action is dispatched to the web layer from C# (e.g. a menu bar item or keyboard shortcut).

```typescript
ks.onAction((action) => {
    if (action === "myapp:new-document") showNewDocumentDialog();
});
```

### `ks.onThemeChange(callback): () => void`

Fires when the C# host pushes a theme update. The theme object is also available as `ks.theme`.

```typescript
ks.onThemeChange((theme) => {
    document.documentElement.style.setProperty("--my-accent", theme.accent);
});
```

### State properties

```typescript
ks.connected   // boolean — WebSocket connection state
ks.port        // number — Bun HTTP server port
ks.windowId    // string — ID of the native window this component belongs to
ks.theme       // Theme — current theme token values
```

---

## Registering Custom Handlers (C#)

Add your own invoke channels from the C# app layer:

```csharp
// In ICorePlugin.Initialize(), called once before windows spawn.
// To register on specific windows, hook into window creation:

// Via ApplicationRuntime directly (avoid the singleton where possible):
ApplicationRuntime.Instance!.OnBeforeRun += () =>
{
    foreach (var window in ApplicationRuntime.Instance.WindowManager.GetAllWindows())
        RegisterHandlers(window);
};

private void RegisterHandlers(ManagedWindow window)
{
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
}
```

Custom handlers follow the same rules as built-in ones:
- Handler receives a `JsonElement` (the `args` object from JS, or `default` if no args were passed)
- Return value is JSON-serialized and delivered as `reply.result`
- Throwing an `Exception` delivers `reply.error` and causes the JS promise to reject
- Handlers run on a thread pool thread — dispatch to main thread for AppKit APIs

---

## Built-in Actions (via `action()`)

These action strings are handled directly by the C# runtime — no service registration needed:

| Action | Effect |
|--------|--------|
| `window:minimize` | Minimize current window to Dock |
| `window:maximize` | Zoom/maximize current window |
| `window:close` | Close current window |
| `app:quit` | Quit the application |
| `shell:openExternal:<url>` | Open URL in default browser |

The `shell:openExternal` action encodes the URL in the action string itself. The typed `shell.openExternal(url)` helper wraps this.

---

## Next

- [Web Components](./web-components.md) — using the bridge in components
- [C# App Layer](./csharp-app-layer.md) — registering custom invoke handlers
- [Bun Services](./bun-services.md) — `query`, `subscribe`, service authoring
