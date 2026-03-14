/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// IPC facade for Bun services and host module.
// Mirrors the C# IIpcFacade shape: typed sub-APIs per execution plane.
// Implementation is thin wrappers over existing ctx methods — no new wire protocol.

// ── Types ────────────────────────────────────────────────────────────

export interface IpcFacade {
  /** Call the C# host process (via stdout NDJSON). */
  host: IpcHostProxy;
  /** Get a proxy for a named Bun worker. */
  worker(name: string): IpcWorkerProxy;
  /** Push to web surfaces (WebSocket). */
  web: IpcWebProxy;
  /** Inter-service call within the same Bun process. */
  call(service: string, args?: any): Promise<any>;
}

export interface IpcHostProxy {
  /** Request/reply to C# host — query over stdout, response on stdin. */
  call(service: string, args?: any): Promise<any>;
  /** Fire-and-forget action to C# host. */
  action(action: string): void;
}

export interface IpcWorkerProxy {
  /** Query a worker's service (via relay or direct WS). */
  call(service: string, args?: any): Promise<any>;
  /** Push data to a worker's channel. */
  push(channel: string, data: any): void;
  /** Fire-and-forget action to a worker. */
  action(action: string): void;
}

export interface IpcWebProxy {
  /** Broadcast to all WebSocket subscribers on a channel. */
  push(channel: string, data: any): void;
  /** Push with server-side retention for replay to new subscribers. */
  pushValue(channel: string, data: any): void;
}

/** Stream handle — type definition only. Wire implementation is Phase 4. */
export interface IpcStream<T = any> {
  [Symbol.asyncIterator](): AsyncIterableIterator<T>;
  cancel(): void;
  readonly done: boolean;
}

// ── Factory ──────────────────────────────────────────────────────────

export interface IpcDeps {
  call: (service: string, args?: any) => Promise<any>;
  push: (channel: string, data: any) => void;
  pushValue: (channel: string, data: any) => void;
  relay: (target: string, channel: string, data: any) => void;
  hostQuery: (service: string, args?: any) => Promise<any>;
  hostAction: (action: string) => void;
  workerQuery?: (name: string, service: string, args?: any) => Promise<any>;
}

export function createIpcFacade(deps: IpcDeps): IpcFacade {
  const host: IpcHostProxy = {
    call: deps.hostQuery,
    action: deps.hostAction,
  };

  const web: IpcWebProxy = {
    push: deps.push,
    pushValue: deps.pushValue,
  };

  return {
    host,
    web,
    call: deps.call,
    worker(name: string): IpcWorkerProxy {
      return {
        call(service, args) {
          if (!deps.workerQuery)
            throw new Error(`ipc.worker("${name}").call() requires direct WS — worker may not have browserAccess`);
          return deps.workerQuery(name, service, args);
        },
        push(channel, data) {
          deps.relay(name, channel, data);
        },
        action(action) {
          deps.relay(name, "__action__", action);
        },
      };
    },
  };
}
