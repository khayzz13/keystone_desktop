# Keystone Documentation

Native macOS desktop runtime. Metal/Skia rendering, Bun-powered web components, hot-reloadable C# plugins — or any subset of those.

---

## Start here

- **[Getting Started](./getting-started.md)** — scaffold a project, build, run, write your first component
- **[Process Model](./process-model.md)** — the three-process architecture, how they communicate, comparison with Electron

## Core concepts

- **[Web Components](./web-components.md)** — TypeScript UI modules, the slot system, HMR, framework usage
- **[Bun Services](./bun-services.md)** — background services, `query`/`subscribe`/`push`, the `defineService` builder, SQLite store
- **[C# App Layer](./csharp-app-layer.md)** — `ICorePlugin`, custom `invoke()` handlers, native Metal/Skia windows, lifecycle events
- **[Plugin System](./plugin-system.md)** — hot-reloadable DLL plugins: service, logic, library, and window types
- **[HTTP Router](./http-router.md)** — optional `fetch()`-compatible router over the invoke() bridge; or use `invoke()`/`RegisterInvokeHandler` directly

## Reference

- **[Native API Reference](./native-api.md)** — all built-in `invoke` channels, `action` strings, bridge API
- **[Configuration Reference](./configuration.md)** — `keystone.json` and `bun/keystone.config.ts` full schema
- **[Window Chrome](./window-chrome.md)** — controlling the native window surface: full-bleed web, traffic lights only, or native chrome

## Examples

- `examples/docs-viewer/` — web-only app (no C#) that renders markdown documentation
- `examples/my-app/` — app with a C# layer and hot-reloadable plugins
