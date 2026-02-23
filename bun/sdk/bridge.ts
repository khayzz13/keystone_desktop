// @keystone/bridge — Client-side SDK for Keystone web components
// Framework-agnostic. Runs in the browser (WKWebView).
// Connects to the Bun runtime via WebSocket, providing typed access to
// actions, channel subscriptions, service queries, theme tokens, and lifecycle hooks.
// Also exposes invoke() for direct web→C# RPC with typed reply.

// === Types ===

export type Theme = {
  bgBase: string; bgSurface: string; bgElevated: string; bgChrome: string;
  bgStrip: string; bgHover: string; bgPressed: string;
  bgButton: string; bgButtonHover: string; bgButtonDark: string;
  bgMedium: string; bgLight: string; stroke: string; divider: string;
  accent: string; accentBright: string; accentHeader: string;
  success: string; warning: string; danger: string;
  textPrimary: string; textSecondary: string; textMuted: string; textSubtle: string;
  font: string;
};

export type KeystoneClient = {
  /** Dispatch an action to the C# host and all connected Bun services */
  action: (action: string) => void;
  /** Subscribe to a named data channel. Returns an unsubscribe function. */
  subscribe: (channel: string, callback: (data: any) => void) => () => void;
  /** Query a Bun service by name. Returns a promise with the result. */
  query: (service: string, args?: any) => Promise<any>;
  /**
   * Invoke a native C# handler by channel name. Returns a promise with the reply.
   * Sends via window.webkit.messageHandlers.keystone (zero Bun round-trip).
   * C# registers handlers via ManagedWindow.RegisterInvokeHandler(channel, fn).
   */
  invoke: <T = any>(channel: string, args?: any) => Promise<T>;
  /** Publish data to a channel. All subscribers (any window) receive it immediately. */
  publish: (channel: string, data?: any) => void;
  /** Send a raw typed message over the WebSocket bridge */
  send: (type: string, data?: any) => void;
  /** Current theme tokens — updated live when the host pushes theme changes */
  theme: Theme;
  /** Register a callback for theme changes. Returns an unsubscribe function. */
  onThemeChange: (callback: (theme: Theme) => void) => () => void;
  /** Register a callback for when the bridge connects. Returns an unsubscribe function. */
  onConnect: (callback: () => void) => () => void;
  /** Register a callback for incoming actions. Returns an unsubscribe function. */
  onAction: (callback: (action: string) => void) => () => void;
  /** Whether the WebSocket bridge is currently connected */
  connected: boolean;
  /** The Bun HTTP server port */
  port: number;
  /** The window ID this component belongs to (from __KEYSTONE_SLOT_CTX__) */
  windowId: string;
};

// === State ===

let ws: WebSocket | null = null;
let _connected = false;
let _port = 0;
let _queryId = 0;

const channelHandlers = new Map<string, Set<(data: any) => void>>();
const channelCache = new Map<string, any>();
const pendingSubs = new Set<string>();
const queryCallbacks = new Map<number, { resolve: (r: any) => void; reject: (e: Error) => void }>();
const pendingQueries: Array<{ id: number; service: string; args: any }> = [];
const connectCallbacks = new Set<() => void>();
const actionCallbacks = new Set<(action: string) => void>();
const themeCallbacks = new Set<(theme: Theme) => void>();

// === Invoke state ===
// invoke() sends { ks_invoke, id, channel, args } via postMessage to C#.
// C# replies by pushing to the WS channel "window:{windowId}:__reply__:{id}".
// The reply subscription is one-shot: unsubscribed immediately after resolution.
let _invokeId = 0;
const _windowId: string = (window as any).__KEYSTONE_SLOT_CTX__?.windowId ?? '';

// === Default theme (matches C# Theme.cs defaults) ===

const theme: Theme = {
  bgBase: '#1a1a22', bgSurface: '#1e1e23', bgElevated: '#24242e', bgChrome: '#22222c',
  bgStrip: '#252530', bgHover: '#3a3a48', bgPressed: '#32323e',
  bgButton: '#2a2a36', bgButtonHover: '#3a3a48', bgButtonDark: '#32323e',
  bgMedium: '#2a2a32', bgLight: '#3a3a44', stroke: '#33333e', divider: '#2e2e3a',
  accent: '#4a6fa5', accentBright: '#4a9eff', accentHeader: '#6a8abc',
  success: '#26a69a', warning: '#ffca28', danger: '#ef5350',
  textPrimary: '#cccccc', textSecondary: '#888888', textMuted: '#667788', textSubtle: 'rgba(255,255,255,0.67)',
  font: '-apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif',
};

// === Connection ===

function connect(port: number) {
  _port = port;
  ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);

  ws.onopen = () => {
    _connected = true;
    for (const channel of pendingSubs) {
      ws!.send(JSON.stringify({ type: 'subscribe', data: { channel } }));
    }
    pendingSubs.clear();
    // drain queries that were issued before the connection was ready
    for (const q of pendingQueries) {
      ws!.send(JSON.stringify({ type: 'query', data: { id: q.id, service: q.service, args: q.args } }));
    }
    pendingQueries.length = 0;
    connectCallbacks.forEach(cb => { try { cb(); } catch {} });
  };

  ws.onmessage = (e) => {
    try {
      const msg = JSON.parse(e.data);

      if (msg.type === 'action' && msg.action) {
        actionCallbacks.forEach(cb => { try { cb(msg.action); } catch {} });
        return;
      }

      if (msg.type === '__theme__') {
        Object.assign(theme, msg.data);
        applyThemeCSSVars();
        themeCallbacks.forEach(cb => { try { cb(theme); } catch {} });
        return;
      }

      // __hmr__ is handled by the slot host or shell directly — bridge ignores it

      if (msg.type === '__query_result__' && typeof msg.id === 'number') {
        const cb = queryCallbacks.get(msg.id);
        if (cb) {
          queryCallbacks.delete(msg.id);
          if (msg.error) cb.reject(new Error(msg.error));
          else cb.resolve(msg.result);
        }
        return;
      }

      // Channel data push
      if (msg.type && msg.data !== undefined) {
        channelCache.set(msg.type, msg.data);
        channelHandlers.get(msg.type)?.forEach(cb => { try { cb(msg.data); } catch {} });
      }
    } catch {}
  };

  ws.onclose = () => {
    _connected = false;
    setTimeout(() => connect(port), 1500);
  };
}

// === HTTP bridge — fetch() intercept ===
//
// When the browser calls fetch("/api/..."), this intercept catches it and routes
// the request through the invoke() bridge to C# instead of making a real HTTP call.
//
// The C# side has an HttpRouter that matches the method+path and runs the registered
// handler. The response comes back through the standard invoke() reply mechanism.
//
// Streaming: if C# returns a streaming response, chunks arrive as WebSocket pushes
// on a dedicated channel. This intercept assembles them into a ReadableStream so
// the caller's res.body works like a normal fetch response.
//
// The intercept prefix is "/api/" by default. Only paths under that prefix are
// intercepted — everything else falls through to the real fetch().
//
// Usage from any web component:
//   const notes = await fetch("/api/notes").then(r => r.json());
//   const res = await fetch("/api/notes", { method: "POST", body: JSON.stringify({...}) });

const HTTP_INTERCEPT_PREFIX = "/api/";

function installFetchIntercept() {
  const _realFetch = window.fetch.bind(window);

  // Cast to `any` first to avoid Bun's `typeof fetch` requiring `preconnect` and other
  // non-standard properties that don't apply to a patched window.fetch.
  (window as any).fetch = async function keystoneFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const url = typeof input === "string" ? input
      : input instanceof URL ? input.pathname + input.search
      : (input as Request).url;

    // Only intercept paths starting with /api/ — everything else is a real request
    if (!url.startsWith(HTTP_INTERCEPT_PREFIX)) {
      return _realFetch(input, init);
    }

    // invoke() requires the bridge to be initialised (WKWebView context)
    if (!_instance) {
      return new Response(JSON.stringify({ error: "Bridge not initialised" }), {
        status: 500, headers: { "content-type": "application/json" },
      });
    }

    const method = (init?.method ?? "GET").toUpperCase();
    let body: any = undefined;
    if (init?.body) {
      try { body = JSON.parse(init.body as string); } catch { body = init.body; }
    }

    try {
      // Route through C# HttpRouter via the invoke() mechanism
      const result = await _instance.invoke<{
        status: number;
        contentType: string;
        body?: any;
        stream?: boolean;
        streamChannel?: string;
      }>(
        "http:request",
        { method, path: url, body }
      );

      if (result.stream && result.streamChannel) {
        // Streaming response — assemble chunks into a ReadableStream
        const streamChannel = result.streamChannel;
        const readable = new ReadableStream({
          start(controller) {
            const encoder = new TextEncoder();
            const unsub = _instance!.subscribe(streamChannel, (msg: any) => {
              if (msg?.done) {
                unsub();
                controller.close();
              } else if (msg?.chunk !== undefined) {
                const chunk = typeof msg.chunk === "string"
                  ? encoder.encode(msg.chunk)
                  : new Uint8Array(msg.chunk);
                controller.enqueue(chunk);
              }
            });
          }
        });
        return new Response(readable, {
          status: result.status,
          headers: { "content-type": result.contentType },
        });
      }

      // Normal response
      const bodyText = result.body !== undefined
        ? (typeof result.body === "string" ? result.body : JSON.stringify(result.body))
        : "";

      return new Response(bodyText, {
        status: result.status,
        headers: { "content-type": result.contentType },
      });

    } catch (err: any) {
      return new Response(JSON.stringify({ error: err?.message ?? "Bridge error" }), {
        status: 500, headers: { "content-type": "application/json" },
      });
    }
  };
}

// === Theme CSS custom properties ===
// Applied to :root so CSS can reference var(--ks-bg-surface), var(--ks-accent), etc.

function applyThemeCSSVars() {
  const root = document.documentElement;
  for (const [key, value] of Object.entries(theme)) {
    root.style.setProperty(`--ks-${camelToKebab(key)}`, value);
  }
}

function camelToKebab(s: string): string {
  return s.replace(/[A-Z]/g, m => '-' + m.toLowerCase());
}

// === Public API ===

let _instance: KeystoneClient | null = null;

/** Create or return the singleton Keystone bridge client. Auto-connects via __KEYSTONE_PORT__. */
export function keystone(): KeystoneClient {
  if (_instance) return _instance;

  const port = (window as any).__KEYSTONE_PORT__;
  if (port) {
    connect(port);
    applyThemeCSSVars();
  }

  // Patch window.fetch to intercept /api/* calls and route them through C# HttpRouter.
  // Must happen after _instance is assigned below (invoke() references _instance).
  // Using setTimeout(0) defers until after the assignment completes.
  setTimeout(installFetchIntercept, 0);

  _instance = {
    action(action: string) {
      if (_connected) ws?.send(JSON.stringify({ type: 'action', data: { action } }));
    },

    subscribe(channel: string, callback: (data: any) => void) {
      const set = channelHandlers.get(channel) ?? new Set();
      set.add(callback);
      channelHandlers.set(channel, set);

      if (_connected) {
        ws?.send(JSON.stringify({ type: 'subscribe', data: { channel } }));
      } else {
        pendingSubs.add(channel);
      }

      const cached = channelCache.get(channel);
      if (cached !== undefined) callback(cached);

      return () => { set.delete(callback); };
    },

    async query(service: string, args?: any): Promise<any> {
      return new Promise((resolve, reject) => {
        const id = ++_queryId;
        queryCallbacks.set(id, { resolve, reject });
        if (_connected) {
          ws?.send(JSON.stringify({ type: 'query', data: { id, service, args } }));
        } else {
          pendingQueries.push({ id, service, args });
        }
        setTimeout(() => {
          if (queryCallbacks.delete(id)) reject(new Error(`Query timeout: ${service}`));
        }, 10_000);
      });
    },

    invoke<T = any>(channel: string, args?: any): Promise<T> {
      return new Promise((resolve, reject) => {
        const id = ++_invokeId;
        const replyChannel = `window:${_windowId}:__reply__:${id}`;

        // One-shot subscription on the WS reply channel
        const unsub = _instance!.subscribe(replyChannel, (data: any) => {
          unsub();
          if (data?.error) reject(new Error(data.error));
          else resolve(data?.result as T);
        });

        // Send via direct postMessage to C# (no Bun round-trip)
        const wk = (window as any).webkit?.messageHandlers?.keystone;
        if (wk) {
          wk.postMessage(JSON.stringify({ ks_invoke: true, id, channel, args, windowId: _windowId }));
        } else {
          unsub();
          reject(new Error('invoke() requires WKWebView (webkit.messageHandlers not available)'));
          return;
        }

        setTimeout(() => {
          unsub();
          reject(new Error(`invoke timeout: ${channel}`));
        }, 15_000);
      });
    },

    publish(channel: string, data?: any) {
      if (_connected) ws?.send(JSON.stringify({ type: 'publish', data: { channel, payload: data } }));
    },

    send(type: string, data?: any) {
      if (_connected) ws?.send(JSON.stringify({ type, data }));
    },

    get theme() { return theme; },

    onThemeChange(callback: (theme: Theme) => void) {
      themeCallbacks.add(callback);
      return () => { themeCallbacks.delete(callback); };
    },

    onConnect(callback: () => void) {
      connectCallbacks.add(callback);
      if (_connected) callback();
      return () => { connectCallbacks.delete(callback); };
    },

    onAction(callback: (action: string) => void) {
      actionCallbacks.add(callback);
      return () => { actionCallbacks.delete(callback); };
    },

    get connected() { return _connected; },
    get port() { return _port; },
    get windowId() { return _windowId; },
  };

  return _instance;
}

// === Typed native API namespaces ===
// These wrap invoke() and action() with ergonomic, Electron-parity APIs.
// All invoke() calls require WKWebView — they will reject in non-native environments.

export const app = {
  /** Get a well-known filesystem path by name. Names: userData, documents, downloads, desktop, temp, appRoot */
  getPath: (name: string): Promise<string> => keystone().invoke('app:getPath', { name }),
  getVersion: (): Promise<string> => keystone().invoke('app:getVersion'),
  getName: (): Promise<string> => keystone().invoke('app:getName'),
  quit: () => keystone().action('app:quit'),
};

export const nativeWindow = {
  /** Set the title of the current native window */
  setTitle: (title: string): Promise<void> => keystone().invoke('window:setTitle', { title }),
  minimize: () => keystone().action('window:minimize'),
  maximize: () => keystone().action('window:maximize'),
  close: () => keystone().action('window:close'),
  /** Open a new window of the given registered type. Returns the new window's ID. */
  open: (type: string): Promise<string> => keystone().invoke('window:open', { type }),
};

export const dialog = {
  /** Show a native open-file panel. Returns selected paths, or null if cancelled. */
  openFile: (opts?: { title?: string; filters?: string[]; multiple?: boolean }): Promise<string[] | null> =>
    keystone().invoke('dialog:openFile', opts),
  /** Show a native save-file panel. Returns the chosen path, or null if cancelled. */
  saveFile: (opts?: { title?: string; filters?: string[]; defaultName?: string }): Promise<string | null> =>
    keystone().invoke('dialog:saveFile', opts),
  /** Show a native message box. Returns the index of the clicked button. */
  showMessage: (opts: { title: string; message: string; buttons?: string[] }): Promise<number> =>
    keystone().invoke('dialog:showMessage', opts),
};

export const shell = {
  /** Open a URL in the system default browser */
  openExternal: (url: string) => keystone().action(`shell:openExternal:${url}`),
  /** Open a file or directory with its default application. Returns true on success. */
  openPath: (path: string): Promise<boolean> => keystone().invoke('shell:openPath', { path }),
};

// Convenience re-exports
export const action = (a: string) => keystone().action(a);
export const subscribe = (ch: string, cb: (data: any) => void) => keystone().subscribe(ch, cb);
export const query = (svc: string, args?: any) => keystone().query(svc, args);
export const invoke = <T = any>(channel: string, args?: any) => keystone().invoke<T>(channel, args);

/**
 * Invoke a named Bun service handler by channel name. Returns a typed promise.
 * Targets handlers registered with ctx.registerInvokeHandler() or .handle() in defineService().
 * Goes over WebSocket — works in any environment, no WKWebView required.
 *
 * Bun:
 *   defineService("notes").handle("notes:getAll", async (args, svc) => svc.store.get("notes"))
 *
 * Browser:
 *   const notes = await invokeBun<Note[]>("notes:getAll");
 */
let _bunInvokeId = 0;

export function invokeBun<T = any>(channel: string, args?: any): Promise<T> {
  const ks = keystone();
  return new Promise((resolve, reject) => {
    const id = ++_bunInvokeId;
    const replyChannel = `__bun_invoke_reply__:${id}`;

    const unsub = ks.subscribe(replyChannel, (data: any) => {
      unsub();
      if (data?.error) reject(new Error(data.error));
      else resolve(data?.result as T);
    });

    ks.send("invoke", { channel, args, replyChannel });

    setTimeout(() => {
      unsub();
      reject(new Error(`invokeBun timeout: ${channel}`));
    }, 15_000);
  });
}
