# Known Limitations

Keystone is in early development. This is a transparent list of what's not done yet and what has rough edges.

## Not Built Yet

- **No scaffolding CLI beyond the basics.** `tools/create-app.py` bootstraps new projects, but there's no `keystone add service` / `keystone add plugin` style generator yet.
- **Logic plugin render attachment.** `ILogicPlugin` and `LogicRegistry` exist — plugins load, metadata is tracked, and hot-reload works. But nothing in the render loop actually calls them. The attachment point from `LogicRegistry` into `ManagedWindow` render passes is not wired.
- **Native text rendering.** Metal/Skia windows use Skia's text stack. CoreText integration for system font rendering on macOS isn't done.
- **Accessibility.** Web components inherit WebKit's accessibility tree. Native Metal/Skia/Vulkan windows have no platform accessibility implementation (`NSAccessibility`, AT-SPI, UIA).
- **App Store distribution.** Direct distribution packaging supports signing and notarization. MAS entitlements and sandbox configuration are untested.
- **Linux global shortcuts.** `LinuxGlobalShortcut` is a stub — `Register()` always returns false. A proper Wayland implementation requires the GlobalShortcuts portal (xdg-desktop-portal 1.7+) via DBus; X11 `XGrabKey` isn't viable under Wayland.
- **Linux main loop event integration.** GTK4 uses signal-based event dispatch. The main loop `ProcessEvents()` path for Linux isn't fully wired — GTK signals handle window events but the engine's own event pump doesn't yet drive the GTK loop in lockstep.
- **Linux window drag.** `StartDrag()` on GTK4 windows requires `GdkToplevel.BeginMove()` which isn't yet called. Dragging via the native title bar works; programmatic drag initiation does not.
- **Wayland window positioning.** Absolute window positioning is unavailable under Wayland by protocol design. `GetPosition()` returns zeroes; `SetPosition()` is a no-op. X11 sessions are unaffected.
- **Theming.** CSS custom property tokens are pushed to web components on connect. A full design token schema, live theme switching, and dark/light mode integration are partial.

## Rough Edges

- Window chrome defaults to a plain look. `titleBarStyle` config exists but isn't as fine-grained as it could be.
- Windows default to square corners with no vibrancy — rounded corners and material backgrounds require explicit C# setup.
- The `@keystone/sdk` module resolution uses a file copy at build time (`vendor_engine_bun()`). Hot changes to the engine SDK won't reflect in apps without re-running the vendor step.
- No sandboxing. Apps run with full filesystem and network access.
