# Keystone Desktop

A desktop application framework. Three processes — a C# host, a Bun runtime, and a WebKit renderer — form a triangle with full bidirectional communication across every edge. Build your UI in web tech, your backend in TypeScript, and your native layer in C# with direct GPU access. Or use any subset.

```
              Browser (WebKit)
          ╱                     ╲
     invoke()              invokeBun()
     fetch("/api/…")       query() / send()
     push                  subscribe()
          ╲                     ╱
    C#  Host ◄── stdin/stdout ──► Bun
    (.NET 10)     (NDJSON)      (TypeScript)
```

https://discord.gg/d2GVrZJda7

---

## What you can build

**Web-only** — TypeScript UI + Bun services, zero C#. Declare windows in `keystone.json`, implement them as web components using any framework. Built-in APIs cover file dialogs, window management, clipboard, shell integration, and more.

**Hybrid** — Web UI for your interface, C# for platform integration or GPU rendering. A single window can composite GPU/Skia content and WebKit panels together.

**Native-only** — Pure C# with GPU/Skia rendering and Flex layout via Taffy. No browser overhead. For visualization tools, real-time graphics, or performance-critical workloads.

All three modes compose freely in a single app.

---

## Why these choices

**C# over Rust, Go, or Swift** — .NET 10 has mature platform bindings across macOS, Linux, and Windows, first-class async, and a plugin system (AssemblyLoadContext) that makes hot-reload genuinely good. Swift would mean no Linux/Windows story. Rust would mean writing platform bindings from scratch. C# is the pragmatic choice for a framework that needs to be native-capable and developer-friendly across platforms.

**Bun over Node or Deno** — Bun's built-in bundler eliminates a separate bundler process entirely. The TypeScript → browser-ready JS pipeline runs inside Bun with zero configuration. Startup time matters for subprocess restart recovery, and Bun starts in milliseconds.

**Separate Bun process** — Process isolation means a JS crash doesn't kill the C# host. But the deeper reason is that Bun makes the TypeScript layer fully self-contained — bundler, package manager, runtime, and test runner in one binary. No webpack, no vite, no esbuild config.

**WebKit slots instead of a full-window WebView** — The GPU/Skia layer renders anywhere a web component doesn't occupy. This enables hybrid windows: native chrome, native data visualization, or native toolbars composited with web UI panels in one window. A full-window WebView would surrender the entire pixel budget to the browser.

**Flexbox via Rust FFI** — Taffy is a production-quality flexbox/grid implementation. Layout input goes in, computed rects come out. Writing a flexbox engine in C# would be a significant project for no real gain.

---

## Platform stack

| Layer | macOS | Linux | Windows |
|-------|-------|-------|---------|
| Host process | .NET 10, AppKit | .NET 10, GTK4 | .NET 10, Win32 |
| GPU rendering | SkiaSharp + Metal | SkiaSharp + Vulkan | SkiaSharp + D3D12 |
| Layout engine | Taffy (Rust FFI) | Taffy (Rust FFI) | Taffy (Rust FFI) |
| TypeScript runtime | Bun | Bun | Bun |
| Web renderer | WKWebView | WebKitGTK | WebView2 |

---

## Quick start

```bash
python3 tools/create-app.py my-app
cd my-app
python3 build.py --run
```

**Requirements:** .NET 10 SDK, Bun, Rust toolchain, and platform dependencies (macOS 15+ Apple Silicon, or GTK4 + WebKitGTK on Linux, or Windows 10 1903+).

---

## Documentation

| Document | Contents |
|----------|----------|
| [Architecture & Getting Started](docs/architecture.md) | Process model, IPC, getting started, project modes |
| [Bun Layer](docs/bun-layer.md) | Web components, services, workers, host.ts |
| [C# Layer](docs/csharp-layer.md) | ICorePlugin, plugin system, HTTP router, programmatic bootstrap |
| [SDK Reference](docs/sdk-reference.md) | Bridge API, invoke/subscribe/action, dialog, shell, clipboard, theme |
| [Configuration](docs/configuration.md) | keystone.json, keystone.config.ts, window chrome, build & packaging |

---

## License

MIT
