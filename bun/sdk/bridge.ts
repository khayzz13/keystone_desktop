/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

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

export interface BrowserStream {
  send(data: ArrayBuffer | Uint8Array): void;
  onMessage(handler: (data: ArrayBuffer) => void): void;
  close(): void;
  readonly ready: Promise<void>;
}

export interface BrowserIpcFacade {
  /** C# host — direct postMessage, fastest path */
  host: {
    call<T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }): Promise<T>;
    action(action: string): void;
  };
  /** Bun services — via WebSocket */
  bun: {
    /** Invoke a named handler (.handle() in defineService) */
    call<T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }): Promise<T>;
    /** Query a service's .query() method */
    query<T = any>(service: string, args?: any, options?: { signal?: AbortSignal }): Promise<T>;
  };
  /** Binary stream — connects to Bun via /ws-bin WebSocket */
  stream: {
    open(channel: string): BrowserStream;
  };
  /** Channel pub/sub — WebSocket topic fan-out */
  subscribe(channel: string, callback: (data: any) => void): () => void;
  publish(channel: string, data?: any): void;
}

export type KeystoneClient = {
  /** Dispatch an action to the C# host and all connected Bun services */
  action: (action: string) => void;
  /** Subscribe to a named data channel. Returns an unsubscribe function. */
  subscribe: (channel: string, callback: (data: any) => void) => () => void;
  /** Query a Bun service by name. Returns a promise with the result. */
  query: (service: string, args?: any, options?: { signal?: AbortSignal }) => Promise<any>;
  /**
   * Invoke a native C# handler by channel name. Returns a promise with the reply.
   * Sends via window.webkit.messageHandlers.keystone (zero Bun round-trip).
   * C# registers handlers via ManagedWindow.RegisterInvokeHandler(channel, fn).
   */
  invoke: <T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }) => Promise<T>;
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

// === Timeout Constants ===

const QUERY_TIMEOUT_MS = 10_000;
const INVOKE_TIMEOUT_MS = 15_000;

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
// C# replies direct via EvaluateJavaScript → __ks_dr__(), or falls back to
// the per-window control channel with { type: "__invoke_reply__", id, result/error }.
// invokeBun() sends { type: "invoke", id, channel, args } via WS.
// Bun replies with { type: "__invoke_reply__", id, result/error } on the same WS connection.
let _invokeId = 0;
const _windowId: string = (window as any).__KEYSTONE_SLOT_CTX__?.windowId ?? '';
const _ctrlChannel = _windowId ? `window:${_windowId}:__ctrl__` : '';
const invokeCallbacks = new Map<number, { resolve: (r: any) => void; reject: (e: Error) => void }>();

function resolveInvokeCallback(id: number, data: any) {
  const cb = invokeCallbacks.get(id);
  if (!cb) return;
  invokeCallbacks.delete(id);
  if (data?.error) {
    const err = new Error(typeof data.error === 'string' ? data.error : data.error.message ?? 'Unknown error');
    if (typeof data.error === 'object' && data.error.code) (err as any).code = data.error.code;
    cb.reject(err);
  } else {
    cb.resolve(data?.result);
  }
}

// Direct invoke reply receiver — C# calls this via EvaluateJavaScript to bypass Bun relay
(window as any).__ks_dr__ = (msg: any) => {
  if (typeof msg === 'string') msg = JSON.parse(msg);
  if (msg.type === '__invoke_reply__' && typeof msg.id === 'number')
    resolveInvokeCallback(msg.id, msg);
};

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
  const token = (window as any).__KEYSTONE_SESSION_TOKEN__ ?? '';
  ws = new WebSocket(`ws://127.0.0.1:${port}/ws?token=${token}`);

  ws.onopen = () => {
    _connected = true;
    // Auto-subscribe to per-window control channel for invoke replies + window events
    if (_windowId) {
      ws!.send(JSON.stringify({ type: 'subscribe', data: { channel: _ctrlChannel } }));
    }
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

      // Invoke reply — from both C# (via ctrl channel) and Bun (direct WS)
      if (msg.type === '__invoke_reply__' && typeof msg.id === 'number') {
        resolveInvokeCallback(msg.id, msg);
        return;
      }

      if (msg.type === '__query_result__' && typeof msg.id === 'number') {
        const cb = queryCallbacks.get(msg.id);
        if (cb) {
          queryCallbacks.delete(msg.id);
          if (msg.error) {
            const err = new Error(typeof msg.error === 'string' ? msg.error : msg.error.message ?? 'Query error');
            if (typeof msg.error === 'object' && msg.error.code) (err as any).code = msg.error.code;
            cb.reject(err);
          } else {
            cb.resolve(msg.result);
          }
        }
        return;
      }

      // Per-window control channel — multiplexed: invoke replies, window events, etc.
      if (_ctrlChannel && msg.type === _ctrlChannel && msg.data) {
        if (msg.data.type === '__invoke_reply__' && typeof msg.data.id === 'number') {
          resolveInvokeCallback(msg.data.id, msg.data);
        }
        // Future: other per-window events can be dispatched here
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
      // Direct to C# via postMessage — works even without Bun WS
      const wk = (window as any).webkit?.messageHandlers?.keystone;
      if (wk) wk.postMessage(JSON.stringify({ ks_action: true, action, windowId: _windowId }));
      // Also broadcast via WS for Bun service action handlers
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

    async query(service: string, args?: any, options?: { signal?: AbortSignal }): Promise<any> {
      return new Promise((resolve, reject) => {
        const id = ++_queryId;
        queryCallbacks.set(id, { resolve, reject });
        if (_connected) {
          ws?.send(JSON.stringify({ type: 'query', data: { id, service, args } }));
        } else {
          pendingQueries.push({ id, service, args });
        }

        if (options?.signal) {
          options.signal.addEventListener('abort', () => {
            if (queryCallbacks.delete(id)) {
              const err = new Error('query cancelled');
              (err as any).code = 'cancelled';
              reject(err);
              if (_connected) ws?.send(JSON.stringify({ type: '__cancel__', data: { id } }));
            }
          }, { once: true });
        }

        setTimeout(() => {
          if (queryCallbacks.delete(id)) reject(new Error(`Query timeout: ${service}`));
        }, QUERY_TIMEOUT_MS);
      });
    },

    invoke<T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }): Promise<T> {
      return new Promise((resolve, reject) => {
        const id = ++_invokeId;

        invokeCallbacks.set(id, { resolve: resolve as any, reject });

        // Send via direct postMessage to C# (no Bun round-trip)
        const wk = (window as any).webkit?.messageHandlers?.keystone;
        if (wk) {
          wk.postMessage(JSON.stringify({ ks_invoke: true, id, channel, args, windowId: _windowId }));
        } else {
          invokeCallbacks.delete(id);
          reject(new Error('invoke() requires WKWebView (webkit.messageHandlers not available)'));
          return;
        }

        // Cancellation via AbortSignal
        if (options?.signal) {
          options.signal.addEventListener('abort', () => {
            if (invokeCallbacks.delete(id)) {
              const err = new Error('invoke cancelled');
              (err as any).code = 'cancelled';
              reject(err);
              // Notify C# of cancellation
              wk.postMessage(JSON.stringify({ ks_cancel: true, id, windowId: _windowId }));
            }
          }, { once: true });
        }

        setTimeout(() => {
          if (invokeCallbacks.delete(id)) {
            const err = new Error(`invoke timeout: ${channel}`);
            (err as any).code = 'timeout';
            reject(err);
          }
        }, INVOKE_TIMEOUT_MS);
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

// === Unified IPC facade ===
// Plane-oriented API matching the Bun service-side and C#-side IPC facades.
// Wraps existing functions — no new wire protocol.

export const ipc: BrowserIpcFacade = {
  host: {
    call: <T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }) =>
      keystone().invoke<T>(channel, args, options),
    action: (action: string) => keystone().action(action),
  },
  bun: {
    call: <T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }) =>
      invokeBun<T>(channel, args, options),
    query: <T = any>(service: string, args?: any, options?: { signal?: AbortSignal }) =>
      keystone().query(service, args, options),
  },
  stream: {
    open(channel: string): BrowserStream {
      const token = (window as any).__KEYSTONE_SESSION_TOKEN__ ?? '';
      const ws = new WebSocket(`ws://127.0.0.1:${_port}/ws-bin?channel=${encodeURIComponent(channel)}&token=${token}`);
      ws.binaryType = "arraybuffer";
      let messageHandler: ((data: ArrayBuffer) => void) | null = null;
      const ready = new Promise<void>((resolve, reject) => {
        ws.onopen = () => resolve();
        ws.onerror = (e) => reject(e);
      });
      ws.onmessage = (e) => { if (messageHandler && e.data instanceof ArrayBuffer) messageHandler(e.data); };
      return {
        send: (data) => ws.send(data),
        onMessage: (handler) => { messageHandler = handler; },
        close: () => ws.close(),
        ready,
      };
    },
  },
  subscribe: (channel: string, callback: (data: any) => void) =>
    keystone().subscribe(channel, callback),
  publish: (channel: string, data?: any) =>
    keystone().publish(channel, data),
};

// === Typed native API namespaces ===
// These wrap invoke() and action() with named, framework-native APIs.
// All invoke() calls require WKWebView — they will reject in non-native environments.

export type AppPaths = {
  /** ~/Library/Application Support/<bundleId> (or platform equivalent) */
  data: string;
  documents: string;
  downloads: string;
  desktop: string;
  temp: string;
  /** Directory containing keystone.json */
  root: string;
};

export const app = {
  /** Returns all well-known filesystem paths in a single call. */
  paths: (): Promise<AppPaths> => keystone().invoke('app:paths'),
  getVersion: (): Promise<string> => keystone().invoke('app:getVersion'),
  getName: (): Promise<string> => keystone().invoke('app:getName'),
  quit: () => keystone().action('app:quit'),

  /** Subscribe to URLs opened by the OS (custom protocols, etc.). Returns unsubscribe. */
  onOpenUrl: (callback: (url: string) => void): (() => void) =>
    keystone().subscribe('__openUrl__', (data: any) => callback(data?.url)),
  /** Subscribe to files opened by the OS. Returns unsubscribe. */
  onOpenFile: (callback: (path: string) => void): (() => void) =>
    keystone().subscribe('__openFile__', (data: any) => callback(data?.path)),
  /** Subscribe to second-instance launches. Returns unsubscribe. */
  onSecondInstance: (callback: (argv: string[], cwd: string) => void): (() => void) =>
    keystone().subscribe('__secondInstance__', (data: any) => callback(data?.argv ?? [], data?.cwd ?? '')),

  /** Register this app as the default handler for a URL scheme. */
  setAsDefaultProtocolClient: (scheme: string): Promise<boolean> =>
    keystone().invoke('app:setAsDefaultProtocolClient', { scheme }),
  /** Remove this app as the default handler for a URL scheme. */
  removeAsDefaultProtocolClient: (scheme: string): Promise<boolean> =>
    keystone().invoke('app:removeAsDefaultProtocolClient', { scheme }),
  /** Check if this app is the default handler for a URL scheme. */
  isDefaultProtocolClient: (scheme: string): Promise<boolean> =>
    keystone().invoke('app:isDefaultProtocolClient', { scheme }),
};

export type WindowEvent =
  | 'focus' | 'blur' | 'minimize' | 'restore'
  | 'enter-full-screen' | 'leave-full-screen'
  | 'moved' | 'resized';

export type ContextMenuInfo = {
  linkUrl: string | null;
  imageUrl: string | null;
  selectedText: string | null;
  isEditable: boolean;
  x: number; y: number;
};

export const nativeWindow = {
  /** Set the title of the current native window */
  setTitle: (title: string): Promise<void> => keystone().invoke('window:setTitle', { title }),
  minimize: () => keystone().action('window:minimize'),
  maximize: () => keystone().action('window:maximize'),
  close: () => keystone().action('window:close'),
  /** Open a new window of the given registered type. Returns the new window's ID. */
  open: (type: string, opts?: { parent?: string }): Promise<string> =>
    keystone().invoke('window:open', { type, ...opts }),
  /** Set whether this window floats above all other windows */
  setFloating: (floating: boolean): Promise<void> =>
    keystone().invoke('window:setFloating', { floating }),
  /** Get the current floating state */
  isFloating: (): Promise<boolean> =>
    keystone().invoke('window:isFloating'),
  /** Get the window's current bounds */
  getBounds: (): Promise<{ x: number; y: number; width: number; height: number }> =>
    keystone().invoke('window:getBounds'),
  /** Set the window's bounds (all fields optional — omitted fields keep current value) */
  setBounds: (bounds: { x?: number; y?: number; width?: number; height?: number }): Promise<void> =>
    keystone().invoke('window:setBounds', bounds),
  /** Center the window on the main screen */
  center: (): Promise<void> =>
    keystone().invoke('window:center'),
  /** Initiate a native window drag. Must be called from a mousedown handler. */
  startDrag: (): Promise<void> =>
    keystone().invoke('window:startDrag'),

  // ── Identity ────────────────────────────────────────────────────────

  /** Get this window's ID */
  getId: (): string => _windowId,
  /** Get this window's title from the native layer */
  getTitle: (): Promise<string> => keystone().invoke('window:getTitle'),
  /** Get parent window ID (null if top-level) */
  getParentId: (): Promise<string | null> => keystone().invoke('window:getParentId'),

  // ── State queries ───────────────────────────────────────────────────

  isFullscreen: (): Promise<boolean> => keystone().invoke('window:isFullscreen'),
  isMinimized: (): Promise<boolean> => keystone().invoke('window:isMinimized'),
  isFocused: (): Promise<boolean> => keystone().invoke('window:isFocused'),

  // ── Fullscreen ──────────────────────────────────────────────────────

  enterFullscreen: (): Promise<void> => keystone().invoke('window:enterFullscreen'),
  exitFullscreen: (): Promise<void> => keystone().invoke('window:exitFullscreen'),

  // ── Constraints + appearance ────────────────────────────────────────

  setMinSize: (width: number, height: number): Promise<void> =>
    keystone().invoke('window:setMinSize', { width, height }),
  setMaxSize: (width: number, height: number): Promise<void> =>
    keystone().invoke('window:setMaxSize', { width, height }),
  /** Set aspect ratio constraint. Pass 0 to clear. */
  setAspectRatio: (ratio: number): Promise<void> =>
    keystone().invoke('window:setAspectRatio', { ratio }),
  /** Set window opacity (0.0–1.0) */
  setOpacity: (opacity: number): Promise<void> =>
    keystone().invoke('window:setOpacity', { opacity }),
  setResizable: (resizable: boolean): Promise<void> =>
    keystone().invoke('window:setResizable', { resizable }),
  /** Prevent screen capture of this window's content */
  setContentProtection: (enabled: boolean): Promise<void> =>
    keystone().invoke('window:setContentProtection', { enabled }),
  /** Make the window transparent to mouse events (click-through) */
  setIgnoreMouseEvents: (ignore: boolean): Promise<void> =>
    keystone().invoke('window:setIgnoreMouseEvents', { ignore }),

  // ── Visibility ──────────────────────────────────────────────────────

  focus: (): Promise<void> => keystone().invoke('window:focus'),
  hide: (): Promise<void> => keystone().invoke('window:hide'),
  show: (): Promise<void> => keystone().invoke('window:show'),

  // ── Events ──────────────────────────────────────────────────────────

  /** Subscribe to window lifecycle events. Returns an unsubscribe function. */
  on(event: WindowEvent, callback: (data?: any) => void): () => void {
    const channel = `window:${_windowId}:event`;
    return keystone().subscribe(channel, (payload: any) => {
      if (payload?.type === event) callback(payload.data);
    });
  },

  // ── DevTools ─────────────────────────────────────────────────────

  /** Enable/disable Safari Web Inspector for this window's WebView */
  setInspectable: (enabled: boolean): Promise<void> =>
    keystone().invoke('webview:setInspectable', { enabled }),

  // ── Context Menu ─────────────────────────────────────────────────

  /** Subscribe to right-click context menu events. Default browser menu is suppressed. */
  onContextMenu(callback: (info: ContextMenuInfo) => void): () => void {
    return keystone().subscribe(`window:${_windowId}:contextmenu`, callback);
  },

  /** Show a native OS context menu (NSMenu on macOS). Actions route through the action system. */
  showContextMenu: (
    items: Array<{ label: string; action: string } | 'separator'>,
    position: { x: number; y: number }
  ): Promise<void> =>
    keystone().invoke('window:showContextMenu', { items, ...position }),
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

export const external = {
  /** Open a URL outside the app — system default browser or handler. */
  url: (url: string) => keystone().action(`external:url:${url}`),
  /** Open a file or directory outside the app with its default application. Returns true on success. */
  path: (path: string): Promise<boolean> => keystone().invoke('external:path', { path }),
};

// === New API namespaces ===

export type DisplayInfo = {
  x: number; y: number; width: number; height: number;
  scaleFactor: number; primary: boolean;
};

export type BatteryStatus = {
  /** true when running on battery (false on AC or when status is unknown) */
  onBattery: boolean;
  /** 0–100, or -1 when unknown (desktop / AC-only hardware) */
  batteryPercent: number;
};

export const clipboard = {
  /** Read plain text from the system clipboard. Returns null if empty. */
  readText: (): Promise<string | null> => keystone().invoke('clipboard:readText'),
  /** Write plain text to the system clipboard. */
  writeText: (text: string): Promise<void> => keystone().invoke('clipboard:writeText', { text }),
  /** Clear the clipboard. */
  clear: (): Promise<void> => keystone().invoke('clipboard:clear'),
  /** Returns true if the clipboard has text content. */
  hasText: (): Promise<boolean> => keystone().invoke('clipboard:hasText'),
};

export const screen = {
  /** Returns info for all connected displays. */
  getAllDisplays: (): Promise<DisplayInfo[]> => keystone().invoke('screen:getAllDisplays'),
  /** Returns the primary (main) display. */
  getPrimaryDisplay: (): Promise<DisplayInfo> => keystone().invoke('screen:getPrimaryDisplay'),
  /** Returns the current cursor position in screen coordinates. */
  getCursorScreenPoint: (): Promise<{ x: number; y: number }> =>
    keystone().invoke('screen:getCursorScreenPoint'),
};

export const notification = {
  /**
   * Show an OS-level notification.
   * macOS: osascript display notification. Linux: notify-send. Windows: MessageBox (full tray support planned).
   */
  show: (title: string, body: string): Promise<void> =>
    keystone().invoke('notification:show', { title, body }),
};

export const darkMode = {
  /** Returns true when the system is in dark mode. */
  isDark: (): Promise<boolean> => keystone().invoke('darkMode:isDark'),
  /**
   * Subscribe to system theme changes.
   * The callback fires immediately with the current value, then on every change.
   * Returns an unsubscribe function.
   */
  onChange: (cb: (dark: boolean) => void): (() => void) =>
    keystone().subscribe('__nativeTheme__', (data: any) => cb(!!data?.dark)),
};

export const battery = {
  /** Returns the current power state: AC/battery and battery percentage. */
  status: (): Promise<BatteryStatus> => keystone().invoke('battery:status'),
  /**
   * Subscribe to power state changes pushed by C#.
   * Returns an unsubscribe function.
   */
  onChange: (cb: (status: BatteryStatus) => void): (() => void) =>
    keystone().subscribe('__powerMonitor__', cb),
};

export const hotkey = {
  /**
   * Register a process-wide keyboard shortcut.
   * Accelerator format: "CommandOrControl+Shift+P", "Alt+F4", "F5", etc.
   * Returns true if registered successfully (false if already taken by another process,
   * or unsupported on this platform).
   */
  register: (accelerator: string): Promise<boolean> =>
    keystone().invoke('hotkey:register', { accelerator }),
  /** Unregister a previously registered shortcut. */
  unregister: (accelerator: string): Promise<void> =>
    keystone().invoke('hotkey:unregister', { accelerator }),
  /** Returns true if this accelerator is registered by this process. */
  isRegistered: (accelerator: string): Promise<boolean> =>
    keystone().invoke('hotkey:isRegistered', { accelerator }),
  /**
   * Subscribe to a specific shortcut firing.
   * Must call register() first.
   * Returns an unsubscribe function.
   */
  on: (accelerator: string, cb: () => void): (() => void) =>
    keystone().subscribe(`hotkey:${accelerator}`, cb),
};

export const headless = {
  /**
   * Open a headless (invisible) window running the given registered component.
   * The window loads its WebKit view but is never shown on screen.
   * Returns the new window's ID for use with evaluate() and close().
   */
  open: (component: string): Promise<string> =>
    keystone().invoke('window:open', { type: component }),
  /**
   * Execute JavaScript in a headless window's WebView context (fire-and-forget).
   * To receive results, have the headless window push to a BunManager channel
   * via `subscribe()` from within the window's web component.
   */
  evaluate: (windowId: string, js: string): Promise<void> =>
    keystone().invoke('headless:evaluate', { windowId, js }),
  /** List all currently running headless window IDs. */
  list: (): Promise<string[]> => keystone().invoke('headless:list'),
  /** Close a headless window by ID. */
  close: (windowId: string): Promise<void> =>
    keystone().invoke('headless:close', { windowId }),
};

// === Web Workers ===
// Headless window workers with structured postMessage/onMessage communication.
// Unlike raw headless windows, WebWorkers provide bidirectional messaging via
// WebSocket pub/sub (no C# round-trip for messages) and evaluate() with return values.

export class WebWorker {
  readonly id: string;
  readonly component: string;
  private _handlers = new Set<(data: any) => void>();
  private _unsub: (() => void) | null = null;
  private _terminated = false;

  /** @internal — use webWorker.spawn() */
  constructor(id: string, component: string) {
    this.id = id;
    this.component = component;
    this._unsub = keystone().subscribe(`worker:${id}:out`, (data: any) => {
      for (const h of this._handlers) h(data);
    });
  }

  /** Send structured data to the worker. */
  postMessage(data: any): void {
    if (this._terminated) return;
    keystone().publish(`worker:${this.id}:in`, data);
  }

  /** Subscribe to messages from the worker. Returns unsubscribe fn. */
  onMessage(callback: (data: any) => void): () => void {
    this._handlers.add(callback);
    return () => this._handlers.delete(callback);
  }

  /** Evaluate JS in the worker's WebView and return the string result. */
  evaluate<T = any>(js: string): Promise<T> {
    return keystone().invoke('worker:evaluate', { workerId: this.id, js });
  }

  /** Terminate the worker. */
  async terminate(): Promise<void> {
    this._terminated = true;
    this._unsub?.();
    this._handlers.clear();
    await keystone().invoke('worker:terminate', { workerId: this.id });
  }
}

export const webWorker = {
  /** Spawn a web worker running the given component in a headless window. */
  spawn: async (component: string): Promise<WebWorker> => {
    const id = await keystone().invoke<string>('worker:spawn', { component });
    return new WebWorker(id, component);
  },
  /** List all active web worker IDs. */
  list: (): Promise<string[]> => keystone().invoke('worker:list'),
};

/** For use inside a worker component — send/receive messages to/from the parent. */
export const workerSelf = {
  /** Send a message to the parent that spawned this worker. */
  postMessage: (data: any): void => {
    keystone().publish(`worker:${keystone().windowId}:out`, data);
  },
  /** Subscribe to messages from the parent. Returns unsubscribe fn. */
  onMessage: (callback: (data: any) => void): () => void => {
    return keystone().subscribe(`worker:${keystone().windowId}:in`, callback);
  },
};

export type ServiceWorkerStatus = {
  active: boolean;
  waiting: boolean;
  installing: boolean;
  scope: string | null;
  scriptURL: string | null;
};

export const platform = {
  /** Current platform identifier */
  get os(): 'macos' | 'linux' | 'windows' {
    const ua = navigator.userAgent.toLowerCase();
    if (ua.includes('mac')) return 'macos';
    if (ua.includes('win')) return 'windows';
    return 'linux';
  },
  /** Check if a capability is available on the current platform */
  isSupported(feature: string): boolean {
    const supported = new Set([
      'fullscreen', 'opacity', 'minMaxSize', 'aspectRatio',
      'contentProtection', 'clickThrough', 'singleInstance',
      'protocolHandler', 'openFile', 'openUrl', 'parentChild',
      'customScheme', 'navigationPolicy', 'requestInterception',
      'diagnostics', 'crashReporting', 'webWorker',
    ]);
    return supported.has(feature);
  },

  // ── Browser Service Worker Management ──────────────────────────────

  /** Get browser service worker registration status for this window's origin */
  swStatus: (): Promise<ServiceWorkerStatus> =>
    keystone().invoke('sw:status'),
  /** Unregister all browser service workers for this origin */
  swUnregister: (): Promise<void> =>
    keystone().invoke('sw:unregister'),
  /** Clear all Cache Storage entries for this origin */
  swClearCaches: (): Promise<void> =>
    keystone().invoke('sw:clearCaches'),
};


// === Diagnostics / Observability ===

export type CrashEvent = {
  kind: string;
  timestamp: string;
  message: string | null;
  stackTrace: string | null;
  processId: number;
  extra: Record<string, string> | null;
};

export type HealthSummary = {
  uptimeMs: number;
  memoryBytes: number;
  bunRunning: boolean;
  windowCount: number;
  recentCrashes: number;
};

export const diagnostics = {
  /** Get recent crash events (kept in memory, last 100) */
  getCrashes: (): Promise<CrashEvent[]> =>
    keystone().invoke('diagnostics:crashes'),
  /** Subscribe to crash events in real-time */
  onCrash: (callback: (event: CrashEvent) => void): (() => void) =>
    keystone().subscribe('diagnostics:crash', callback),
  /** Get current process health summary */
  health: (): Promise<HealthSummary> =>
    keystone().invoke('diagnostics:health'),
};

// === Request Interception ===

export type RequestInterceptRule = {
  /** URL substring to match against */
  pattern: string;
  /** What to do: "block" (403), "redirect" (302 to target), "allow" (pass through) */
  action: 'block' | 'redirect' | 'allow';
  /** Redirect target URL (required when action is "redirect") */
  target?: string;
};

export const webview = {
  /** Set URL patterns to block via navigation policy (blocks all navigation to matching URLs) */
  setNavigationPolicy: (blocked: string[]): Promise<void> =>
    keystone().invoke('webview:setNavigationPolicy', { blocked }),
  /**
   * Set request interception rules (requires customScheme: true in config).
   * Rules are evaluated in order — first match wins. Unmatched requests proxy to Bun normally.
   */
  setRequestInterceptor: (rules: RequestInterceptRule[]): Promise<void> =>
    keystone().invoke('webview:setRequestInterceptor', { rules }),
};

// Convenience re-exports
export const action = (a: string) => keystone().action(a);
export const subscribe = (ch: string, cb: (data: any) => void) => keystone().subscribe(ch, cb);
export const query = (svc: string, args?: any, options?: { signal?: AbortSignal }) => keystone().query(svc, args, options);
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
export function invokeBun<T = any>(channel: string, args?: any, options?: { signal?: AbortSignal }): Promise<T> {
  const ks = keystone();
  return new Promise((resolve, reject) => {
    const id = ++_invokeId;

    invokeCallbacks.set(id, { resolve: resolve as any, reject });

    ks.send("invoke", { id, channel, args });

    if (options?.signal) {
      options.signal.addEventListener('abort', () => {
        if (invokeCallbacks.delete(id)) {
          const err = new Error('invokeBun cancelled');
          (err as any).code = 'cancelled';
          reject(err);
          ks.send("__cancel__", { id });
        }
      }, { once: true });
    }

    setTimeout(() => {
      if (invokeCallbacks.delete(id)) {
        const err = new Error(`invokeBun timeout: ${channel}`);
        (err as any).code = 'timeout';
        reject(err);
      }
    }, INVOKE_TIMEOUT_MS);
  });
}
