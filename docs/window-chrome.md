# Window Chrome

By default Keystone windows are square, floating, and have a visible title bar. None of that is required — you can make the native window invisible to the UI entirely, leaving only what macOS always shows (traffic lights), or take full control of the window surface from your web component.

---

## The Default

Out of the box, a window declared in `keystone.json` gets:

- Standard macOS rounded corners
- A native title bar with the configured `title` text
- Traffic light buttons (close/minimize/zoom) in the standard position
- Standard window shadow and float behavior

This is the fastest path if you're building a utility or productivity app and want to match the platform immediately.

---

## Full-Bleed Web (Seamless Look)

If your design calls for a fully custom appearance — your own title bar, custom traffic light positioning, or a UI that bleeds to the very edges of the window — set `titleBarStyle` to `hidden` in the window config.

```jsonc
// keystone.json
{
  "windows": [
    {
      "component": "app",
      "width": 1200,
      "height": 800,
      "titleBarStyle": "hidden"
    }
  ]
}
```

With `hidden`:
- The native title bar disappears
- Traffic lights remain in their standard top-left position (macOS always owns them)
- Your web component fills the entire window frame, including the area behind where the title bar was
- You're responsible for a drag region (see below)

### Making the window draggable

When the title bar is hidden, there's no native drag region. Set `-webkit-app-region: drag` on any element you want to act as the drag handle:

```css
#titlebar {
  -webkit-app-region: drag;
  height: 38px;     /* standard macOS title bar height */
}

#titlebar button {
  -webkit-app-region: no-drag;  /* buttons must opt out */
}
```

This tells WebKit to forward mouse events in that region to the window's native drag behavior.

---

## Traffic Lights Only

For a minimal chrome that disappears into the background, use `titleBarStyle: "hidden"` and style your own title bar area to be transparent or match your content:

```typescript
// In your mount function
root.style.cssText = `
  height: 100%;
  /* No background in the traffic-light zone — shows through to the window */
  padding-top: 38px;
`;

const titlebar = document.createElement("div");
titlebar.style.cssText = `
  position: fixed;
  top: 0; left: 0; right: 0;
  height: 38px;
  -webkit-app-region: drag;
  /* transparent — traffic lights show at native position */
`;
root.appendChild(titlebar);
```

The traffic lights always render at the system level — you cannot remove or reposition them without disabling them entirely via a native app layer change.

---

## Removing Window Decoration (Frameless)

To go completely frameless — no title bar, no standard rounded corners from the window frame — use `titleBarStyle: "none"`. The window becomes a plain rectangle. You own the entire surface.

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

Note: frameless windows on macOS won't have the standard drop shadow unless you add it from the native layer. For most cases `"hidden"` is the right choice — `"none"` is for fully custom shapes or floating panels.

---

## Window Shape and Behavior

### Rounded corners

macOS handles rounded corners at the compositor level for standard windows. If you use `titleBarStyle: "hidden"`, the native window still has rounded corners — your web content will be clipped to that shape automatically.

### Non-floating (standard behavior)

The default window level is `normal` — it participates in standard window z-ordering and doesn't float above other apps. This matches macOS conventions. To get the standard behavior you don't need to do anything.

### Fullscreen and zoom

The native zoom (green traffic light) button works by default. `nativeWindow.maximize()` from the bridge calls `NSWindow.Zoom`. If you want to intercept or disable zoom, register an invoke handler or override the action in your app layer.

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
| Standard macOS look | Default (no extra config needed) |
| Web controls the title bar area | `"titleBarStyle": "hidden"` |
| Traffic lights only, no native title | `"titleBarStyle": "hidden"` + transparent drag region |
| Completely frameless | `"titleBarStyle": "none"` |
| Per-window native NSWindow control | C# app layer via `IWindowPlugin` or `OnBeforeRun` hook |

---

## Next

- [Getting Started](./getting-started.md) — project structure and first run
- [Web Components](./web-components.md) — building the UI that fills the window
- [C# App Layer](./csharp-app-layer.md) — native window control from code
