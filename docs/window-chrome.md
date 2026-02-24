# Window Chrome

By default Keystone windows use a native macOS titled window with a transparent title bar — your web component fills the entire frame, native traffic lights (close/minimize/zoom) appear at the standard position, and the window gets compositor-level rounded corners. This is the right choice for most apps.

For apps that want a custom GPU-rendered title bar with tabs, float toggle, and tiling support, set `titleBarStyle: "toolkit"`. For fully frameless windows with no chrome at all, use `"none"`.

---

## The Default (`"hidden"`)

Out of the box, a window declared in `keystone.json` gets:

- Native macOS traffic lights (close, minimize, zoom)
- Compositor-level rounded corners
- Standard window shadow
- Normal z-ordering (not floating)
- Your web component fills the full window area, including behind the traffic lights

This is the standard macOS look. No extra config needed.

### Making the window draggable

With the default `"hidden"` style, there's no visible native title bar. Set `-webkit-app-region: drag` on any element you want to act as the drag handle:

```css
.titlebar {
  -webkit-app-region: drag;
  height: 38px;     /* standard macOS title bar height */
}

.titlebar button {
  -webkit-app-region: no-drag;  /* buttons must opt out */
}
```

This tells WebKit to forward mouse events in that region to the window's native drag behavior.

### Traffic light inset

The native traffic lights sit at approximately (12, 12) from the top-left. Your web content should leave space for them — typically `padding-top: 38px` and `padding-left: 78px` on your title bar region, or use a transparent drag region that spans the top of the window.

---

## Toolkit Mode (GPU Title Bar)

For apps that want a managed window chrome with tabs, float toggle, bind/tiling integration, and custom toolbar — set `titleBarStyle` to `"toolkit"`:

```jsonc
// keystone.json
{
  "windows": [
    {
      "component": "app",
      "width": 1200,
      "height": 800,
      "titleBarStyle": "toolkit"
    }
  ]
}
```

With `"toolkit"`:
- A GPU-rendered title bar appears at the top (close, minimize, float toggle, tab strip)
- No native traffic lights (the window is borderless — the toolkit provides its own buttons)
- Web content starts below the title bar
- Supports tab groups, bind/tiling, and the toolkit toolbar system
- Optional `toolbar` config adds a toolbar strip below the title bar

This is a non-native look — similar to how Electron apps style their own chrome. Use it when you want the tiling window manager features or a custom chrome experience.

---

## Frameless (`"none"`)

To go completely frameless — no title bar, no traffic lights, no rounded corners — use `titleBarStyle: "none"`. The window is a plain rectangle. You own the entire surface.

```jsonc
{
  "windows": [
    {
      "component": "app",
      "titleBarStyle": "none",
      "width": 400,
      "height": 300
    }
  ]
}
```

Note: frameless windows on macOS won't have the standard drop shadow unless you add it from the native layer. For most cases the default `"hidden"` is the right choice — `"none"` is for fully custom shapes or floating panels.

---

## Floating Windows

By default, windows use normal z-ordering — they participate in standard macOS window layering and don't float above other apps. To make a window always-on-top, set `floating: true`:

```jsonc
{
  "windows": [
    {
      "component": "player",
      "title": "Mini Player",
      "width": 300,
      "height": 200,
      "floating": true
    }
  ]
}
```

You can also toggle floating at runtime from your web component:

```typescript
import { nativeWindow } from "@keystone/sdk/bridge";

await nativeWindow.setFloating(true);   // pin above other windows
await nativeWindow.setFloating(false);  // return to normal z-order
const isFloating = await nativeWindow.isFloating();
```

---

## Window Shape and Behavior

### Rounded corners

macOS handles rounded corners at the compositor level for titled windows. With the default `"hidden"` style, the native window still has rounded corners — your web content is clipped to that shape automatically.

With `"toolkit"` or `"none"` (borderless), macOS does not provide compositor-level rounding.

### Fullscreen and zoom

The native zoom (green traffic light) button works with the default `"hidden"` style. `nativeWindow.maximize()` from the bridge calls `NSWindow.Zoom`. If you want to intercept or disable zoom, register an invoke handler or override the action in your app layer.

---

## Window Control from the SDK

The bridge provides window control methods:

```typescript
import { nativeWindow } from "@keystone/sdk/bridge";

// Basic controls
nativeWindow.minimize();
nativeWindow.maximize();
nativeWindow.close();

// Title
await nativeWindow.setTitle("New Title");

// Floating
await nativeWindow.setFloating(true);
const floating = await nativeWindow.isFloating();

// Bounds
const bounds = await nativeWindow.getBounds();
// => { x: 100, y: 200, width: 800, height: 600 }

await nativeWindow.setBounds({ width: 1024, height: 768 });  // omitted fields keep current value
await nativeWindow.center();

// Open a new window
const windowId = await nativeWindow.open("settings");
```

---

## Controlling Chrome from C#

For precise control over window style at the `NSWindow` level, use a custom `IWindowPlugin` or register an `OnBeforeRun` hook in your `ICorePlugin`. The `ManagedWindow` exposes the underlying `NSWindow` through the platform layer.

```csharp
// In ICorePlugin.Initialize()
context.OnBeforeRun += () =>
{
    foreach (var win in ApplicationRuntime.Instance!.WindowManager.GetAllWindows())
    {
        NSApplication.SharedApplication.InvokeOnMainThread(() =>
        {
            // Example: remove the standard buttons entirely
            win.NativeWindow?.StandardWindowButton(NSWindowButton.ZoomButton)?.SetHidden(true);
        });
    }
};
```

This is the escape hatch for anything not covered by `titleBarStyle` — vibrancy, custom window levels, sheet attachment, etc.

---

## Summary

| Goal | Setting |
|------|---------|
| Standard macOS look (traffic lights, rounded corners) | Default (no config needed) |
| Web controls everything, traffic lights present | Default (`"hidden"`) |
| GPU title bar with tabs/tiling | `"titleBarStyle": "toolkit"` |
| Completely frameless | `"titleBarStyle": "none"` |
| Always-on-top | `"floating": true` |
| Per-window native NSWindow control | C# app layer via `IWindowPlugin` or `OnBeforeRun` hook |

---

## Next

- [Getting Started](./getting-started.md) — project structure and first run
- [Web Components](./web-components.md) — building the UI that fills the window
- [C# App Layer](./csharp-app-layer.md) — native window control from code
- [Native API Reference](./native-api.md) — all built-in invoke channels
