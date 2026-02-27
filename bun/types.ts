// types.ts — Keystone Bun Runtime protocol types
// Defines the NDJSON message shapes between C# host and Bun subprocess,
// and the contracts for services and web components.

import type { ResolvedConfig } from "./sdk/config";

// === IPC Request (C# → Bun / C# → Worker) ===

export type Request =
  | { id: number; type: "query"; service: string; args?: any }
  | { id: number; type: "health" }
  | { id: number; type: "eval"; code: string }
  | { id: 0; type: "action"; action: string }
  | { id: 0; type: "push"; channel: string; data: any }
  | { id: 0; type: "relay_in"; channel: string; data: any }
  | { type: "worker_ports"; data: Record<string, number> }
  | { type: "shutdown" };

// === IPC Response (Bun → C# / Worker → C#) ===

export type Response =
  | { id: number; result: any }
  | { id: number; error: string }
  | { type: "service_push"; channel: string; data: any }
  | { type: "action_from_web"; action: string }
  | { type: "relay"; target: string; channel: string; data: any }
  | { status: "ready"; services: string[]; webComponents?: string[]; port: number };

// === Service contract ===
// Services live in services/ — either a single .ts file or a directory with index.ts.
// They receive a context with call() (inter-service queries) and push() (broadcast to C# + web).

export type ServiceContext = {
  /** Call another service's query() method */
  call: (service: string, args?: any) => Promise<any>;
  /** Push data to C# host and connected web clients via a named channel */
  push: (channel: string, data: any) => void;
  /** Register a handler for custom web→Bun messages sent via keystone().send(type, data) */
  onWebMessage: (type: string, handler: (data: any) => void | Promise<void>) => void;
  /**
   * Register a named invoke handler. Browser calls invoke("channel", args) and gets a promise reply.
   * Equivalent to Electron's ipcMain.handle() — but targeting Bun instead of the main process.
   */
  registerInvokeHandler: (channel: string, handler: (args: any) => any | Promise<any>) => void;
  /** Relay a message to another worker or main Bun via C# */
  relay: (target: string, channel: string, data: any) => void;
  /** Direct worker connections (available after worker_ports received from C#) */
  workers: WorkerRegistry;
};

export type WorkerRegistry = {
  /** Connect directly to another worker's WebSocket (requires target to have browserAccess) */
  connect: (name: string) => WorkerConnection;
  /** Get the port map of all workers with browserAccess */
  ports: () => Record<string, number>;
};

export type WorkerConnection = {
  /** Query a service on the connected worker */
  query: (service: string, args?: any) => Promise<any>;
  /** Subscribe to a push channel on the connected worker */
  subscribe: (channel: string, cb: (data: any) => void) => () => void;
  /** Send a raw typed message */
  send: (type: string, data?: any) => void;
  /** Close the WebSocket connection */
  close: () => void;
};

export type ServiceModule = {
  start: (ctx: ServiceContext) => Promise<void>;
  query?: (args?: any) => any | Promise<any>;
  stop?: () => void;
  health?: () => { ok: boolean; [k: string]: any };
  onAction?: (action: string) => void;

  /** Network policy overrides — merged during service discovery. */
  network?: {
    /** Additional endpoints this service needs (merged with app allow-list). */
    endpoints?: string[];
    /** Bypass the allow-list entirely for this service. */
    unrestricted?: boolean;
  };
};

// === Web component contract ===
// Web components live in web/ — bundled to ESM by Bun.build, served via HTTP,
// rendered in WKWebView slots managed by C#. Use any framework or vanilla JS.

export type SlotContext = {
  /** The slot key used to identify this component instance within the host WebView */
  slotKey: string;
  /** The native window ID this slot belongs to */
  windowId: string;
};

export type WebComponentModule = {
  mount: (root: HTMLElement, ctx?: SlotContext) => void;
  unmount?: (root: HTMLElement) => void;
};

// === App host module (bun/host.ts) ===
// Apps may export any combination of these named hooks from their bun/host.ts.
// The framework imports it if it exists and calls hooks at defined lifecycle phases.

export type HostContext = {
  /** Register a service module directly — bypasses file discovery. Calls start(ctx) immediately. */
  registerService: (name: string, mod: ServiceModule) => Promise<void>;
  /** Register a named invoke handler not tied to a service. */
  registerInvokeHandler: (channel: string, handler: (args: any) => any | Promise<any>) => void;
  /** Register a web→Bun message handler (browser send() target). */
  onWebMessage: (type: string, handler: (data: any) => void | Promise<void>) => void;
  /** Push data to C# host and connected web clients. */
  push: (channel: string, data: any) => void;
  /** Live view of all registered services. */
  readonly services: ReadonlyMap<string, ServiceModule>;
  /** The resolved runtime config. */
  readonly config: ResolvedConfig;
};

export type HostHook = (ctx: HostContext) => void | Promise<void>;

export type AppHostModule = {
  /** Before service discovery — good for direct service registration, global handler setup. */
  onBeforeStart?: HostHook;
  /** After all services started and HTTP server is live — windows are opening in C#. */
  onReady?: HostHook;
  /** Before service stop() calls — last chance to flush state. */
  onShutdown?: HostHook;
  /** Global action handler — fires alongside per-service onAction handlers. Sync only. */
  onAction?: (action: string, ctx: HostContext) => void;
};
