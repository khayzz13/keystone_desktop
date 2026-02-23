# Web Components

A web component is a TypeScript/TSX entry point that Bun bundles and serves into a native window slot. The code runs in a full WKWebView browser context — full DOM, CSS, `fetch`, Web APIs, any ESM-compatible framework.

There is no required folder structure, no mandatory boilerplate, no framework opinion. The only thing the slot host needs from your entry file is two named exports:

```typescript
export function mount(root: HTMLElement, ctx: SlotContext) { ... }
export function unmount(root: HTMLElement) { ... }  // optional
```

Everything else — how many files you split it into, what framework you use, how you organize state — is entirely your call.

---

## The Slot Contract

The slot host calls `mount(root, ctx)` when the component is inserted, and `unmount(root)` before it's removed or hot-swapped. If `unmount` is absent the host clears `root.innerHTML` automatically.

```typescript
type SlotContext = {
    slotKey: string;   // component name — "dashboard", "settings", etc.
    windowId: string;  // native window ID — scope channel subscriptions to this
};
```

`windowId` lets you receive pushes targeted at a specific window instance:

```typescript
subscribe(`window:${ctx.windowId}:refresh`, () => reload());
```

```csharp
// C# side
BunManager.Instance.Push($"window:{windowId}:refresh", new { });
```

---

## Registering a Component

There are two ways to register an entry file as a component.

### Filename convention — drop a file in `bun/web/`

Any `.ts` or `.tsx` file in `bun/web/` is auto-discovered. The filename (minus extension) becomes the component name.

```
bun/web/
├── dashboard.ts    → component "dashboard"
├── settings.tsx    → component "settings"
└── onboarding.ts   → component "onboarding"
```

Export `mount` and optionally `unmount` directly:

```typescript
// bun/web/dashboard.ts
import { subscribe } from "@keystone/sdk/bridge";

export function mount(root: HTMLElement, ctx: SlotContext) {
    root.innerHTML = `<h1 style="color: var(--ks-text-primary)">Dashboard</h1>`;

    const unsub = subscribe(`window:${ctx.windowId}:data`, (d) => {
        root.querySelector("h1")!.textContent = d.value;
    });

    // no unmount export needed — runtime clears root.innerHTML on teardown
}
```

### Explicit config — point to any file

If the entry file lives outside `bun/web/`, register it by name in `keystone.config.ts`:

```typescript
// bun/keystone.config.ts
import { defineConfig } from "@keystone/sdk/config";

export default defineConfig({
    web: {
        components: {
            "dashboard": "src/dashboard.ts",
            "settings":  "src/settings/index.ts",
        },
    },
});
```

Both approaches work simultaneously. If the same name appears in both, the explicit entry wins.

---

## Internal Structure

The entry file is just a Bun bundle entrypoint. Internally it can be anything:

```
src/
├── dashboard.ts          ← entry (export mount, unmount)
├── dashboard/
│   ├── ui.ts             ← your internal structure
│   ├── state.ts
│   └── components/
│       ├── Sidebar.tsx
│       └── Header.tsx
└── shared/
    └── utils.ts
```

```typescript
// src/dashboard.ts
import { Sidebar } from "./dashboard/components/Sidebar";
import { initState } from "./dashboard/state";
import { subscribe } from "@keystone/sdk/bridge";

export function mount(root: HTMLElement, ctx: SlotContext) {
    const state = initState(ctx.windowId);
    const sidebar = new Sidebar(root, state);
    // ...
}

export function unmount(root: HTMLElement) {
    // your cleanup
}
```

The slot host sees exactly two exports. How you arrived at them is your business.

---

## What runs in the browser

Everything available in WKWebView (WebKit on macOS):

| | Notes |
|---|---|
| HTML / DOM API | Build it in `mount()` |
| CSS (inline / in JS) | Works everywhere |
| `import "./styles.css"` | Bun resolves at bundle time |
| Standalone `.css` files | Served as static assets — inject a `<link>` tag |
| Static assets (SVG, PNG, fonts) | Served from `bun/` at their relative path |
| `fetch`, WebSocket, Web Workers | All available |
| React, Svelte, Vue, Solid | Any ESM framework bundled by Bun |

---

## The Slot System

C# manages where components appear using **slots** — named rectangular regions within a native window. A slot maps a component name to a position and size in the window.

The native rendering layer (Metal/Skia) draws everything underneath. When a slot is active, a `WKWebView` overlays that region with your component's output. The two layers composite via CoreAnimation.

All slots in a single window share one `WKWebView` (and one WebKit content process). The host page manages slot lifecycle via `window.__addSlot`, `window.__moveSlot`, `window.__removeSlot`, and `window.__hotSwapSlot` — you never call these directly.

---

## Using the Bridge

Import from `@keystone/sdk/bridge`. All exports are tree-shaken at bundle time by Bun.

```typescript
import {
    keystone,     // singleton bridge client
    action,       // fire-and-forget to C#
    invoke,       // request/reply to C# handlers
    invokeBun,    // request/reply to Bun service handlers
    subscribe,    // subscribe to named channels
    query,        // query a Bun service
    app,          // app namespace (getPath, getVersion, getName, quit)
    nativeWindow, // window namespace (setTitle, minimize, maximize, close, open)
    dialog,       // native panels (openFile, saveFile, showMessage)
    shell,        // OS integration (openExternal, openPath)
} from "@keystone/sdk/bridge";
```

### Theme Tokens

The bridge applies CSS custom properties to `:root` on load and updates them live when the host pushes theme changes. Use them in inline styles or CSS:

```typescript
root.style.background = "var(--ks-bg-surface)";
root.style.color = "var(--ks-text-primary)";
```

Full token list:

| Token | Purpose |
|-------|---------|
| `--ks-bg-base` | Deepest background |
| `--ks-bg-surface` | Standard panel/card background |
| `--ks-bg-elevated` | Elevated elements (modals, popovers) |
| `--ks-bg-chrome` | Window chrome / titlebar |
| `--ks-bg-hover` | Hover state |
| `--ks-bg-pressed` | Active/pressed state |
| `--ks-bg-button` | Default button fill |
| `--ks-accent` | Primary accent color |
| `--ks-accent-bright` | Bright accent (links, highlights) |
| `--ks-success` / `--ks-warning` / `--ks-danger` | Status colors |
| `--ks-text-primary` | Main body text |
| `--ks-text-secondary` | Subdued text |
| `--ks-text-muted` | Placeholder / metadata |
| `--ks-stroke` | Border color |
| `--ks-font` | System font stack |

---

## Optional Helper: `defineComponent`

`defineComponent` is a small convenience that auto-wires a cleanup return value from your mount callback to `unmount`. Use it if you want — it's entirely optional.

```typescript
import { defineComponent } from "@keystone/sdk/component";
import { subscribe } from "@keystone/sdk/bridge";

export const { mount, unmount } = defineComponent((root, ctx) => {
    root.style.cssText = "height: 100%; background: var(--ks-bg-surface); padding: 24px;";

    const heading = document.createElement("h1");
    root.appendChild(heading);

    const unsub = subscribe(`window:${ctx.windowId}:title`, (d) => {
        heading.textContent = d.value;
    });

    return () => unsub(); // returned function called on unmount automatically
});
```

This is strictly equivalent to writing `mount` and `unmount` by hand. It only saves you from storing the cleanup reference yourself.

---

## Framework Usage

Any ESM-compatible framework works. Bundle time is negligible with Bun's native bundler.

### React

```bash
cd bun && bun add react react-dom @types/react @types/react-dom
```

```tsx
// src/app.tsx
import React, { useState, useEffect } from "react";
import { createRoot } from "react-dom/client";
import { subscribe, dialog } from "@keystone/sdk/bridge";
import type { SlotContext } from "@keystone/sdk/component";

function App({ ctx }: { ctx: SlotContext }) {
    const [files, setFiles] = useState<string[]>([]);

    useEffect(() => {
        const unsub = subscribe(`window:${ctx.windowId}:files`, setFiles);
        return () => unsub();
    }, [ctx.windowId]);

    return (
        <div style={{ background: "var(--ks-bg-surface)", height: "100vh", padding: 24 }}>
            <button onClick={() => dialog.openFile({ multiple: true })}>Open</button>
            {files.map(f => <div key={f}>{f}</div>)}
        </div>
    );
}

let reactRoot: ReturnType<typeof createRoot> | null = null;

export function mount(root: HTMLElement, ctx: SlotContext) {
    reactRoot = createRoot(root);
    reactRoot.render(<App ctx={ctx} />);
}

export function unmount() {
    reactRoot?.unmount();
    reactRoot = null;
}
```

### Svelte

```bash
cd bun && bun add svelte
```

```typescript
// src/app.ts
import App from "./App.svelte";
import type { SlotContext } from "@keystone/sdk/component";

let instance: any;

export function mount(root: HTMLElement, ctx: SlotContext) {
    instance = new App({ target: root, props: { ctx } });
}

export function unmount() {
    instance?.$destroy();
}
```

For Svelte/Vue file extensions add them to the watch list in `keystone.config.ts` so HMR picks them up:

```typescript
export default defineConfig({
    watch: { extensions: [".ts", ".tsx", ".svelte"] },
    web: {
        components: { "app": "src/app.ts" },
    },
});
```

---

## Hot Module Replacement

HMR is automatic for both auto-discovered files and explicit `web.components` entries. When you save any registered entry file, the runtime:

1. Rebundles the changed file with `Bun.build`.
2. Sends an `__hmr__` message over the WebSocket to all connected windows.
3. The slot host calls `unmount`, clears the root, re-imports the new bundle, calls `mount`.

No page reload. The bridge WebSocket stays open. Subscriptions re-register in the new `mount` call as long as you set them up inside it.

---

## Multiple Components Per Window

A window can show more than one component. Each gets its own named slot. The C# `IWindowPlugin` (or `WebWindowPlugin` from `keystone.json` config) specifies which components and where.

If you need dynamic slot management — different components at runtime — that's done from the C# layer via `FlexNode` with `WebComponent` nodes, which tell the renderer to create slots at specific layout positions.

---

## Accessing Native APIs from Components

Everything native routes through `invoke()`. It's the only channel with direct, synchronous access to C# — no Bun round-trip.

```typescript
import { dialog, app, nativeWindow, shell } from "@keystone/sdk/bridge";

// File dialogs
const paths = await dialog.openFile({
    title: "Select images",
    filters: [".png", ".jpg", ".webp"],
    multiple: true,
});

// Window control
await nativeWindow.setTitle("New Document — Untitled");
nativeWindow.minimize();

// OS integration
shell.openExternal("https://example.com");

// App control
app.quit();
```

---

## Static Assets

Files in the app's `bun/` directory are served statically by the HTTP server. Reference them by path from your component:

```typescript
const img = document.createElement("img");
img.src = "/web/assets/logo.png";   // served from bun/web/assets/logo.png
```

---

## Lifecycle Summary

```
C# spawns window
    ↓
WKWebView created, loads /__host__
    ↓
host page ready (window.__ready = true)
    ↓
C# calls window.__addSlot("app", "/web/app.js", x, y, w, h, windowId)
    ↓
host page imports /web/app.js
    ↓
app.ts mount(root, { slotKey: "app", windowId }) called
    ↓
[component runs, bridge connects, data flows]
    ↓
[file saved]
    ↓
Bun rebundles app.ts → app.js
Bun sends __hmr__ over WebSocket
    ↓
host page calls app.ts unmount(root)
host page imports /web/app.js?t=<timestamp>
host page calls new app.ts mount(root, ctx)
    ↓
[window closes or app quits]
    ↓
C# calls window.__removeSlot("app")
host page calls unmount(root)
```

---

## Next

- [Bridge API Reference](./native-api.md) — full `invoke`, `invokeBun`, `action`, `dialog`, `shell` reference
- [Bun Services](./bun-services.md) — data services, `subscribe`, `query`, named handlers
- [Configuration Reference](./configuration.md) — `keystone.config.ts` options
- [C# App Layer](./csharp-app-layer.md) — custom invoke handlers, native windows
- [Window Chrome](./window-chrome.md) — full-bleed web, traffic-lights-only, frameless windows
