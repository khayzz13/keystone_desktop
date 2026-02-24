# Known Limitations

Keystone is in early development. This is a transparent list of what's not done yet and what has rough edges.

## Not Built Yet

- **macOS only.** The rendering pipeline, window system, and process model are all AppKit/Metal-specific. Windows and Linux support is planned but not started.
- **No scaffolding CLI beyond the basics.** `tools/create-app.py` bootstraps new projects, but there's no `keystone add service` / `keystone add plugin` style generator yet.
- **Workspace persistence.** The plugin interfaces for save/restore exist, but the full cycle isn't wired end-to-end.
- **Theming.** CSS custom property tokens are pushed to web components on connect. A full design token schema, live theme switching, and dark/light mode integration are partial.
- **Multi-window management polish.** Tab groups and split/bind containers exist, but advanced paneling/overlay behavior and overall UX are still rough in edge cases.
- **Logic plugin render attachment.** The `ILogicPlugin` interface exists but the render thread integration for attaching logic to specific window render passes is incomplete.
- **Native text rendering.** Metal/Skia windows fall back to Skia's text stack. CoreText integration for system font rendering isn't done.
- **Accessibility.** Web components inherit WebKit's accessibility. Native Metal/Skia windows have no `NSAccessibility` implementation.
- **App Store distribution.** Packaging supports signing, verification, and optional notarization for direct distribution. MAS entitlements and sandbox configuration remain untested.
- **Overlay windows.** Floating overlays (dropdowns, panels) are defined at the interface level but the positioning and lifecycle management isn't complete.

## Rough Edges

- Window chrome defaults to a plain look. `titleBarStyle` config exists but isn't as fine-grained as it could be.
- Windows default to square corners with no vibrancy — rounded corners and material backgrounds require explicit C# setup.
- The `@keystone/sdk` module resolution uses a runtime symlink. It works but is sensitive to changes in the app's directory structure.
- No sandboxing. Apps run with full filesystem and network access.
