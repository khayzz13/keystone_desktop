/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// @keystone/service — Service authoring SDK for Keystone Bun services
// Provides typed helpers for building services with lifecycle, state, queries, and scheduling.
//
// Services live in your app's services/ directory (single .ts file or directory with index.ts).
// The runtime calls start(), query(), stop(), and health() on your exported module.
// This SDK provides a builder pattern that wires everything up cleanly.

import type { ServiceContext, ServiceModule } from "../types";
import type { StreamReader } from "../lib/stream";
import { store as createStore } from "../lib/store";

export type { ServiceContext } from "../types";

// === Service builder ===

type QueryHandler<TArgs = any, TResult = any> = (args: TArgs, signal?: AbortSignal) => TResult | Promise<TResult>;
type ActionHandler = (action: string) => void;
type StopHandler = () => void;
type HealthHandler = () => { ok: boolean; [k: string]: any };

type NetworkOptions = { endpoints?: string[]; unrestricted?: boolean };

type StreamHandler = (reader: StreamReader, svc: ServiceHandle) => void | Promise<void>;

type ServiceConfig = {
  name: string;
  queryHandler?: QueryHandler;
  actionHandler?: ActionHandler;
  stopHandler?: StopHandler;
  healthHandler?: HealthHandler;
  intervals: Array<{ ms: number; fn: () => void | Promise<void>; timer?: ReturnType<typeof setInterval> }>;
  invokeHandlers: Array<{ channel: string; handler: (args: any, signal?: AbortSignal) => any | Promise<any> }>;
  streamHandlers: Array<{ channel: string; handler: StreamHandler }>;
  network?: NetworkOptions;
};

export type ServiceHandle = {
  /** The service context provided by the runtime */
  ctx: ServiceContext;
  /** Namespaced key-value store backed by SQLite — survives hot-reloads */
  store: ReturnType<typeof createStore>;
  /** Call another service's query method */
  call: ServiceContext['call'];
  /** Push data to a named channel (C# host + connected web clients) */
  push: ServiceContext['push'];
  /** Push with server-side retention — new WebSocket clients get the last value on connect */
  pushValue: ServiceContext['pushValue'];
  /** Register a named invoke handler callable from the browser via invoke("channel", args) */
  registerInvokeHandler: ServiceContext['registerInvokeHandler'];
  /** Register a binary WebSocket handler on /ws-bin?channel=name */
  registerBinaryWebSocket: ServiceContext['registerBinaryWebSocket'];
  /** Register an HTTP handler for a path prefix on the main Bun server */
  registerHttpHandler: ServiceContext['registerHttpHandler'];
  /** Open a binary stream to the C# host or a web client */
  openStream: ServiceContext['openStream'];
  /** Register a handler for incoming binary streams on a channel */
  onStream: ServiceContext['onStream'];
  /** Subscribe to pushes from the C# host process */
  onHostPush: ServiceContext['onHostPush'];
  /** Worker connections */
  workers: ServiceContext['workers'];
  /** Unified IPC facade — typed access to host, workers, and web surfaces */
  ipc: ServiceContext['ipc'];
  /** Fetch — policy-enforced by default; unrestricted if service declares network.unrestricted */
  fetch: typeof globalThis.fetch;
};

export type ServiceBuilder = {
  /** Define the query handler — the entry point for queries from C#, web clients, and other services */
  query: <TArgs = any, TResult = any>(handler: (args: TArgs, svc: ServiceHandle, signal?: AbortSignal) => TResult | Promise<TResult>) => ServiceBuilder;
  /** Define an action handler — receives actions dispatched from C#, web, or other services */
  onAction: (handler: (action: string, svc: ServiceHandle) => void) => ServiceBuilder;
  /** Define a cleanup handler — called when the service is stopped or hot-reloaded */
  onStop: (handler: (svc: ServiceHandle) => void) => ServiceBuilder;
  /** Define a health check — return { ok: true/false } for the runtime health monitor */
  health: (handler: (svc: ServiceHandle) => { ok: boolean; [k: string]: any }) => ServiceBuilder;
  /** Schedule a recurring task. Timers are automatically cleaned up on stop. */
  every: (ms: number, fn: (svc: ServiceHandle) => void | Promise<void>) => ServiceBuilder;
  /**
   * Register a named invoke handler. Browser calls invoke("channel", args) and gets a typed reply.
   * Equivalent to Electron's ipcMain.handle() — but targeting Bun instead of the main process.
   */
  handle: <TArgs = any, TResult = any>(channel: string, handler: (args: TArgs, svc: ServiceHandle, signal?: AbortSignal) => TResult | Promise<TResult>) => ServiceBuilder;
  /** Register a handler for incoming binary streams on a channel */
  stream: (channel: string, handler: (reader: StreamReader, svc: ServiceHandle) => void | Promise<void>) => ServiceBuilder;
  /** Declare network endpoint requirements. Endpoints merge into the app allow-list; unrestricted bypasses it. */
  network: (opts: NetworkOptions) => ServiceBuilder;
  /** Build and return the service module for export */
  build: (init?: (svc: ServiceHandle) => void | Promise<void>) => ServiceModule;
};

/** Create a new service with the builder pattern. */
export function defineService(name: string): ServiceBuilder {
  const config: ServiceConfig = { name, intervals: [], invokeHandlers: [], streamHandlers: [] };
  let handle: ServiceHandle | null = null;

  const builder: ServiceBuilder = {
    query(handler) {
      config.queryHandler = (args, signal) => handler(args, handle!, signal);
      return builder;
    },

    onAction(handler) {
      config.actionHandler = (action) => handler(action, handle!);
      return builder;
    },

    onStop(handler) {
      config.stopHandler = () => handler(handle!);
      return builder;
    },

    health(handler) {
      config.healthHandler = () => handler(handle!);
      return builder;
    },

    every(ms, fn) {
      config.intervals.push({ ms, fn: () => fn(handle!) });
      return builder;
    },

    handle(channel, handler) {
      config.invokeHandlers.push({ channel, handler: (args: any, signal?: AbortSignal) => handler(args, handle!, signal) });
      return builder;
    },

    stream(channel, handler) {
      config.streamHandlers.push({ channel, handler });
      return builder;
    },

    network(opts) {
      config.network = opts;
      return builder;
    },

    build(init) {
      const mod: ServiceModule = {
        async start(ctx: ServiceContext) {
          // Unrestricted services get the original unpatched fetch; normal services get the policy-enforced one
          const fetchRef = config.network?.unrestricted
            ? ((globalThis as any).__KEYSTONE_ORIGINAL_FETCH__ ?? globalThis.fetch)
            : globalThis.fetch;

          handle = {
            ctx,
            store: createStore(name),
            call: ctx.call,
            push: ctx.push,
            pushValue: ctx.pushValue,
            registerInvokeHandler: ctx.registerInvokeHandler,
            registerBinaryWebSocket: ctx.registerBinaryWebSocket,
            registerHttpHandler: ctx.registerHttpHandler,
            openStream: ctx.openStream,
            onStream: ctx.onStream,
            onHostPush: ctx.onHostPush,
            workers: ctx.workers,
            ipc: ctx.ipc,
            fetch: fetchRef,
          };

          // Register named invoke handlers
          for (const { channel, handler } of config.invokeHandlers) {
            ctx.registerInvokeHandler(channel, handler);
          }

          // Register stream handlers
          for (const { channel, handler } of config.streamHandlers) {
            ctx.onStream(channel, (reader) => handler(reader, handle!));
          }

          // Start intervals
          for (const interval of config.intervals) {
            interval.timer = setInterval(interval.fn, interval.ms);
          }

          if (init) await init(handle);
        },

        query: config.queryHandler,
        onAction: config.actionHandler,
        health: config.healthHandler,

        stop() {
          for (const interval of config.intervals) {
            if (interval.timer) clearInterval(interval.timer);
          }
          config.stopHandler?.();
          handle = null;
        },
      };

      if (config.network) mod.network = config.network;
      return mod;
    },
  };

  return builder;
}
