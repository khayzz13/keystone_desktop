# Window Chrome

> **Platform note:** This document describes window chrome behavior with a focus on macOS (AppKit/NSWindow). On Linux (GTK4) and Windows (Win32), Keystone provides equivalent window management — native window decorations, title bar styles, floating window levels, and GPU-rendered chrome. The configuration surface (`titleBarStyle`, `floating`, `renderless`) is the same on all platforms; visual details follow each platform's native conventions.

By default Keystone windows use a native titled window with a transparent title bar — your web component fills the entire frame, native window controls (close/minimize/zoom on macOS, GTK4 decorations on Linux) appear at the standard position, and the window gets compositor-level rounded corners where the platform supports it. This is the right choice for most apps.

For apps that want a custom GPU-rendered title bar with tabs, float toggle, and tiling support, set `titleBarStyle: "toolkit"`. For fully frameless windows with no chrome at all, use `"none"`.

---

## The Default (`"hidden"`)

Out of the box, a window declared in `keystone.json` gets:

- Native window controls (traffic lights on macOS, GTK4 header bar buttons on Linux)
- Compositor-level rounded corners (macOS; Linux follows the compositor/window manager)
- Standard window shadow
- Normal z-ordering (not floating)
- Your web component fills the full window area, including behind the native controls

This is the standard platform look. No extra config needed.

### Making the window draggable

With the default `"hidden"` style, there's no visible native title bar. Set `-webkit-app-region: drag` on any element you want to act as the drag handle (supported on both macOS and Linux via WebKit):

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

### Window control inset

On macOS, the native traffic lights sit at approximately (12, 12) from the top-left. On Linux with GTK4, window controls are typically on the right side of the header. Leave space for them in your layout — typically `padding-top: 38px` on macOS, or adjust for the platform's control placement. Using a transparent drag region that spans the full top of the window works on both platforms.

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

Note: frameless windows on macOS won't have the standard drop shadow unless you add it from the native layer. On Linux, shadow behavior depends on the compositor. For most cases the default `"hidden"` is the right choice — `"none"` is for fully custom shapes or floating panels.

---

## Floating Windows

By default, windows use normal z-ordering and don't float above other apps. To make a window always-on-top, set `floating: true`:

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

On macOS, the compositor handles rounded corners at the native level for titled windows. With the default `"hidden"` style, the native window still has rounded corners — your web content is clipped to that shape automatically. With `"toolkit"` or `"none"` (borderless), macOS does not provide compositor-level rounding.

On Linux, rounded corners depend on the compositor (e.g. Mutter/GNOME, KWin/KDE). Keystone doesn't override compositor-managed rounding.

On Windows, rounded corners are controlled by the DWM (Desktop Window Manager) and are on by default for modern windows (Windows 11+). Borderless windows (`"none"`) are sharp-cornered unless you explicitly apply a DWM corner preference.

#### GPU-rendered corner radius (`Theme.CornerRadius`)

For windows that use GPU/Skia rendering (`titleBarStyle: "toolkit"` or native `IWindowPlugin` windows), the Toolkit layer applies rounded corners to all drawn elements via `Theme.CornerRadius`. This is separate from the OS compositor corner — it controls the radius used for panels, buttons, cards, and other nodes rendered by the Flex/Skia pipeline.

The default is `4f`. Override it at app startup to retheme all Toolkit components globally:

```csharp
Theme.CornerRadius = 8f;  // rounder — set before first frame
```

This affects every Toolkit component that uses `BgRadius` (panels, cards, title bar buttons, etc.). It does not affect web content or the OS-level window corner.

### Fullscreen and zoom

On macOS, the native zoom (green traffic light) button works with the default `"hidden"` style. `nativeWindow.maximize()` from the bridge triggers the platform maximize behavior. If you want to intercept or disable it, register an invoke handler or override the action in your app layer.

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

For precise control over window style at the native window level, use a custom `IWindowPlugin` or register an `OnBeforeRun` hook in your `ICorePlugin`. The `ManagedWindow` exposes the underlying native window through the platform layer.

**macOS (NSWindow):**

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

**Linux (GTK4):**

```csharp
context.OnBeforeRun += () =>
{
    foreach (var win in ApplicationRuntime.Instance!.WindowManager.GetAllWindows())
    {
        // Access GTK window via the platform layer for GTK4-specific customizations
        win.PlatformWindow?.SetDecorated(false);
    }
};
```

This is the escape hatch for anything not covered by `titleBarStyle` — custom window levels, vibrancy (macOS), GTK CSS overrides (Linux), etc.

---

## Summary

| Goal | Setting |
|------|---------|
| Standard platform look (native window controls, rounded corners) | Default (no config needed) |
| Web controls everything, native controls present | Default (`"hidden"`) |
| GPU title bar with tabs/tiling | `"titleBarStyle": "toolkit"` |
| Completely frameless | `"titleBarStyle": "none"` |
| Always-on-top | `"floating": true` |
| Per-window native window control | C# app layer via `IWindowPlugin` or `OnBeforeRun` hook |

---

## Next

- [Getting Started](./getting-started.md) — project structure and first run
- [Web Components](./web-components.md) — building the UI that fills the window
- [C# App Layer](./csharp-app-layer.md) — native window control from code
- [Native API Reference](./native-api.md) — all built-in invoke channels
