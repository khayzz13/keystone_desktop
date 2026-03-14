/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// types.ts — Keystone Bun Runtime protocol types
// Defines the NDJSON message shapes between C# host and Bun subprocess,
// and the contracts for services and web components.

import type { ResolvedConfig } from "./sdk/config";
import type { IpcFacade } from "./lib/ipc";

// === IPC Request (C# → Bun / C# → Worker) ===

export type Request =
  | { id: number; type: "query"; service: string; args?: any }
  | { id: number; type: "health" }
  | { id: number; type: "eval"; code: string }
  | { id: 0; type: "action"; action: string }
  | { id: 0; type: "push"; channel: string; data: any }
  | { id: 0; type: "push_value"; channel: string; data: any }
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
  /** Push with server-side retention — new WebSocket clients get the last value on connect */
  pushValue: (channel: string, data: any) => void;
  /** Register a handler for custom web→Bun messages sent via keystone().send(type, data) */
  onWebMessage: (type: string, handler: (data: any) => void | Promise<void>) => void;
  /** Subscribe to pushes from the C# host process. Multiple handlers per channel allowed. */
  onHostPush: (channel: string, handler: (data: any) => void) => void;
  /**
   * Register a named invoke handler. Browser calls invoke("channel", args) and gets a promise reply.
   * Equivalent to Electron's ipcMain.handle() — but targeting Bun instead of the main process.
   */
  registerInvokeHandler: (channel: string, handler: (args: any, signal?: AbortSignal) => any | Promise<any>) => void;
  /** Relay a message to another worker or main Bun via C# */
  relay: (target: string, channel: string, data: any) => void;
  /**
   * Register an HTTP handler for a path prefix on the main Bun server.
   * The handler receives the raw Request and returns a Response (or null to fall through).
   * Matched by longest-prefix-first — e.g. "/vscode" matches "/vscode/out/foo.js".
   */
  registerHttpHandler: (prefix: string, handler: (req: globalThis.Request, url: URL) => globalThis.Response | Promise<globalThis.Response | null> | null) => void;
  /** Register a binary WebSocket handler. Connections to /ws-bin?channel=name get raw ArrayBuffer frames. */
  registerBinaryWebSocket: (channel: string, handler: {
    onOpen?: (ws: import("bun").ServerWebSocket) => void;
    onMessage?: (ws: import("bun").ServerWebSocket, data: Buffer) => void;
    onClose?: (ws: import("bun").ServerWebSocket) => void;
  }) => void;
  /** Open a binary stream to the C# host or a web client */
  openStream: (channel: string, target: "host" | "web") => import("./lib/stream").StreamWriter;
  /** Register a handler for incoming binary streams on a channel */
  onStream: (channel: string, handler: (reader: import("./lib/stream").StreamReader) => void) => void;
  /** Direct worker connections (available after worker_ports received from C#) */
  workers: WorkerRegistry;
  /** Unified IPC facade — typed access to host, workers, and web surfaces */
  ipc: IpcFacade;
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
  query?: (args?: any, signal?: AbortSignal) => any | Promise<any>;
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

// === Keystone ABI — Unified Envelope ===
// Canonical message model shared across all IPC lanes (NDJSON, WebSocket, binary frames).
// Current NDJSON messages will migrate to this format incrementally (Phase 2).
// Binary frames use 0x4B53 magic + uint32 BE length + JSON payload.

export type KeystoneEnvelope = {
  v: 1;
  kind:
    | "hello"
    | "request"
    | "response"
    | "event"
    | "cancel"
    | "stream_open"
    | "stream_chunk"
    | "stream_close"
    | "error";
  id?: number;
  streamId?: number;
  op?: string;
  source?: string;
  target?: string;
  windowId?: string;
  capability?: string;
  deadlineMs?: number;
  encoding?: "json" | "binary";
  payload?: unknown;
  error?: {
    code: string;
    message: string;
    details?: unknown;
    stack?: string;
  };
};

// === Binary Frame Constants ===
// Wire: [0x4B 0x53] [uint32-BE length] [payload bytes]

export const BINARY_FRAME_MAGIC = 0x4b53; // "KS"
export const BINARY_FRAME_HEADER_SIZE = 6;

// === App host module (bun/host.ts) ===
// Apps may export any combination of these named hooks from their bun/host.ts.
// The framework imports it if it exists and calls hooks at defined lifecycle phases.

export type HostContext = {
  /** Register a service module directly — bypasses file discovery. Calls start(ctx) immediately. */
  registerService: (name: string, mod: ServiceModule) => Promise<void>;
  /** Register a named invoke handler not tied to a service. */
  registerInvokeHandler: (channel: string, handler: (args: any, signal?: AbortSignal) => any | Promise<any>) => void;
  /** Register a web→Bun message handler (browser send() target). */
  onWebMessage: (type: string, handler: (data: any) => void | Promise<void>) => void;
  /** Push data to C# host and connected web clients. */
  push: (channel: string, data: any) => void;
  /** Push with server-side retention — new WebSocket clients get the last value on connect */
  pushValue: (channel: string, data: any) => void;
  /** Live view of all registered services. */
  readonly services: ReadonlyMap<string, ServiceModule>;
  /** The resolved runtime config. */
  readonly config: ResolvedConfig;
  /** Unified IPC facade — typed access to host, workers, and web surfaces */
  readonly ipc: IpcFacade;
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
