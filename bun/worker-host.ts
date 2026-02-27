// worker-host.ts — Keystone Bun Worker Runtime
// Stripped-down version of host.ts for worker processes. Reads config from env vars
// instead of keystone.config.ts. No web component bundling, no HMR, no static serving.
// Optional WebSocket server for direct browser/worker access when KEYSTONE_BROWSER_ACCESS=true.

import { readdirSync, existsSync, statSync } from "fs";
import { join } from "path";
import type { ServerWebSocket } from "bun";
import type { Request, ServiceContext, WorkerConnection } from "./types";
import { store } from "./lib/store";

const WORKER_NAME = Bun.env.KEYSTONE_WORKER_NAME!;
const SERVICES_DIR = Bun.env.KEYSTONE_SERVICES_DIR!;
const BROWSER_ACCESS = Bun.env.KEYSTONE_BROWSER_ACCESS === "true";
const APP_ROOT = Bun.env.KEYSTONE_APP_ROOT!;
const IS_EXTENSION_HOST = Bun.env.KEYSTONE_EXTENSION_HOST === "true";
const ALLOWED_CHANNELS = (Bun.env.KEYSTONE_ALLOWED_CHANNELS ?? "").split(",").filter(Boolean);

// === Network endpoint allow-list ===

const networkMode = (Bun.env.KEYSTONE_NETWORK_MODE ?? "open") as "open" | "allowlist";
const networkEndpoints = new Set<string>(
  (Bun.env.KEYSTONE_NETWORK_ENDPOINTS ?? "").split(",").filter(Boolean)
);

function isEndpointAllowed(hostPort: string): boolean {
  if (networkMode !== "allowlist") return true;
  if (networkEndpoints.has(hostPort)) return true;
  const lastColon = hostPort.lastIndexOf(":");
  const host = lastColon > 0 ? hostPort.slice(0, lastColon) : hostPort;
  if (networkEndpoints.has(host)) return true;
  for (const ep of networkEndpoints) {
    if (ep.startsWith("*.") && host.endsWith(ep.slice(1))) return true;
  }
  return false;
}

const originalFetch = globalThis.fetch;
(globalThis as any).__KEYSTONE_ORIGINAL_FETCH__ = originalFetch;

if (networkMode === "allowlist") {
  globalThis.fetch = function(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url);
    const hostPort = url.port && url.port !== "443" && url.port !== "80"
      ? `${url.hostname}:${url.port}` : url.hostname;
    if (!isEndpointAllowed(hostPort)) {
      throw new Error(`[network-policy] ${url.hostname} is not in the allowed endpoints list`);
    }
    return originalFetch(input, init);
  };
}

process.title = `keystone-worker:${WORKER_NAME}`;
process.env.KEYSTONE_APP_ROOT = APP_ROOT;

// === Registries ===

const actionHandlers = new Map<string, (action: string) => void>();
const webMessageHandlers = new Map<string, (data: any, ws: ServerWebSocket) => void | Promise<void>>();
const invokeHandlers = new Map<string, (args: any) => any | Promise<any>>();

type ServiceEntry = {
  mod: any;
  query: (args?: any) => any;
  stop?: () => void;
  health?: () => { ok: boolean; [k: string]: any };
};
const services = new Map<string, ServiceEntry>();

// === Service directory ===

const serviceDir = join(APP_ROOT, SERVICES_DIR);

// === Server ref — assigned if browserAccess, otherwise no-op ===

let serverRef: { publish: (topic: string, data: string) => void; port: number } = {
  publish: () => {},
  port: 0,
};

// === Worker port map for direct connections ===

let workerPorts: Record<string, number> = {};

// === ServiceContext ===

function makePush(): ServiceContext["push"] {
  if (IS_EXTENSION_HOST && ALLOWED_CHANNELS.length > 0) {
    return (channel, data) => {
      if (!ALLOWED_CHANNELS.some(p => channel.startsWith(p))) return;
      console.log(JSON.stringify({ type: "service_push", channel, data }));
      serverRef.publish(channel, JSON.stringify({ type: channel, data }));
    };
  }
  return (channel, data) => {
    console.log(JSON.stringify({ type: "service_push", channel, data }));
    serverRef.publish(channel, JSON.stringify({ type: channel, data }));
  };
}

function connectToWorker(name: string): WorkerConnection {
  const port = workerPorts[name];
  if (!port) throw new Error(`Worker "${name}" has no WebSocket (browserAccess=false or not yet discovered)`);

  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
  const pending = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void }>();
  const subscribers = new Map<string, Set<(data: any) => void>>();
  let nextId = 1;
  let ready = false;
  const queue: string[] = [];

  ws.onopen = () => {
    ready = true;
    for (const msg of queue) ws.send(msg);
    queue.length = 0;
  };

  ws.onmessage = (ev) => {
    try {
      const msg = JSON.parse(ev.data as string);
      // Query reply
      if (msg.type === "__query_result__" && msg.id != null) {
        const cb = pending.get(msg.id);
        if (cb) {
          pending.delete(msg.id);
          if (msg.error) cb.reject(new Error(msg.error));
          else cb.resolve(msg.result);
        }
        return;
      }
      // Push channel subscription
      const subs = subscribers.get(msg.type);
      if (subs) for (const cb of subs) cb(msg.data);
    } catch {}
  };

  function send(raw: string) {
    if (ready) ws.send(raw);
    else queue.push(raw);
  }

  return {
    query(service, args) {
      const id = nextId++;
      return new Promise((resolve, reject) => {
        pending.set(id, { resolve, reject });
        send(JSON.stringify({ type: "query", service, args, id }));
      });
    },
    subscribe(channel, cb) {
      let set = subscribers.get(channel);
      if (!set) { set = new Set(); subscribers.set(channel, set); }
      set.add(cb);
      send(JSON.stringify({ type: "subscribe", data: { channel } }));
      return () => { set!.delete(cb); };
    },
    send(type, data) { send(JSON.stringify({ type, data })); },
    close() { ws.close(); },
  };
}

const ctx: ServiceContext = {
  call: async (service, args) => {
    const svc = services.get(service);
    if (!svc) throw new Error(`Unknown service: ${service}`);
    return await svc.query?.(args);
  },
  push: makePush(),
  onWebMessage: (type, handler) => { webMessageHandlers.set(type, handler); },
  registerInvokeHandler: (channel, handler) => { invokeHandlers.set(channel, handler); },
  relay: (target, channel, data) => {
    console.log(JSON.stringify({ type: "relay", target, channel, data }));
  },
  workers: {
    connect: connectToWorker,
    ports: () => workerPorts,
  },
};

// === Built-in services ===

function registerBuiltins() {
  const appId = process.env.KEYSTONE_APP_ID ?? "keystone";
  const home = process.env.HOME ?? "/tmp";
  const paths = {
    appRoot: APP_ROOT,
    userData: `${home}/.keystone/${appId}`,
    documents: `${home}/Documents`,
    downloads: `${home}/Downloads`,
    desktop: `${home}/Desktop`,
    temp: `/tmp`,
  };
  services.set("paths", { mod: null, query: () => paths, health: () => ({ ok: true }) });
}

// === Service discovery ===

async function discoverServices() {
  // Compiled mode — services statically imported into the exe at package time.
  // The packager generates a wrapper that sets this global before importing worker-host.ts.
  // Services are keyed by worker name so one exe serves all workers.
  const compiledAll = (globalThis as any).__KEYSTONE_COMPILED_SERVICES__;
  const compiled = compiledAll?.[WORKER_NAME];
  if (compiled) {
    for (const [name, mod] of Object.entries(compiled) as [string, any][]) {
      mergeServiceNetwork(name, mod);
      if (mod.start) {
        await mod.start(ctx);
        services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
      }
      if (mod.onAction) actionHandlers.set(name, mod.onAction);
    }
    console.error(`[worker:${WORKER_NAME}] compiled mode: ${services.size} services`);
    return;
  }

  if (!existsSync(serviceDir)) return;
  for (const entry of readdirSync(serviceDir)) {
    const entryPath = join(serviceDir, entry);
    const stat = statSync(entryPath);
    let mod: any, name: string;
    if (stat.isFile() && /\.[jt]s$/.test(entry)) {
      mod = require(entryPath);
      name = entry.replace(/\.[jt]s$/, "");
    } else if (stat.isDirectory()) {
      const idx = join(entryPath, "index.ts");
      if (!existsSync(idx)) continue;
      mod = require(idx);
      name = entry;
    } else continue;
    mergeServiceNetwork(name, mod);
    if (mod.start) {
      await mod.start(ctx);
      services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
    }
    if (mod.onAction) actionHandlers.set(name, mod.onAction);
  }
}

function mergeServiceNetwork(name: string, mod: any) {
  if (!mod.network) return;
  if (mod.network.endpoints) {
    for (const ep of mod.network.endpoints) networkEndpoints.add(ep);
    console.error(`[worker:${WORKER_NAME}] ${name}: merged ${mod.network.endpoints.length} network endpoints`);
  }
  if (mod.network.unrestricted) {
    console.error(`[worker:${WORKER_NAME}] ${name}: network unrestricted`);
  }
}

// === WebSocket server (conditional) ===

if (BROWSER_ACCESS) {
  const server = Bun.serve({
    hostname: "127.0.0.1",
    port: 0,
    fetch(req, srv) {
      if (new URL(req.url).pathname === "/ws")
        return srv.upgrade(req) ? undefined : new Response("", { status: 400 });
      return new Response("Not found", { status: 404 });
    },
    websocket: {
      open(ws: ServerWebSocket) { ws.subscribe("all"); },
      message(ws: ServerWebSocket, msg: string | Buffer) {
        try {
          const { type, data } = JSON.parse(msg as string);
          handleWebMessage(ws, type, data);
        } catch {}
      },
      close(ws: ServerWebSocket) { ws.unsubscribe("all"); },
    },
  });
  serverRef = server;
}

async function handleWebMessage(ws: ServerWebSocket, type: string, data: any) {
  if (type === "subscribe") {
    ws.subscribe(data.channel);
    return;
  }
  if (type === "query" && data?.service) {
    try {
      const svc = services.get(data.service);
      if (!svc) throw new Error(`Unknown service: ${data.service}`);
      const result = await svc.query?.(data.args ?? {});
      ws.send(JSON.stringify({ type: "__query_result__", id: data.id, result }));
    } catch (e: any) {
      ws.send(JSON.stringify({ type: "__query_result__", id: data.id, error: e.message }));
    }
    return;
  }
  if (type === "invoke" && data?.channel) {
    const handler = invokeHandlers.get(data.channel);
    const replyChannel = data.replyChannel;
    if (!replyChannel) return;
    try {
      if (!handler) throw new Error(`No invoke handler registered for: ${data.channel}`);
      const result = await handler(data.args ?? {});
      ws.send(JSON.stringify({ type: replyChannel, data: { result } }));
    } catch (e: any) {
      ws.send(JSON.stringify({ type: replyChannel, data: { error: e.message } }));
    }
    return;
  }
  const customHandler = webMessageHandlers.get(type);
  if (customHandler) {
    try { await customHandler(data, ws); } catch (e: any) {
      console.error(`[worker:${WORKER_NAME}] web message handler "${type}" threw: ${e.message}`);
    }
  }
}

// === Boot ===

registerBuiltins();
await discoverServices();

console.log(JSON.stringify({
  status: "ready",
  services: [...services.keys()],
  port: serverRef.port,
}));

// === Health monitor ===

setInterval(async () => {
  for (const [name, svc] of services) {
    try {
      const status = svc.health?.() ?? { ok: true };
      if (!status.ok) {
        console.error(`[worker:${WORKER_NAME}] service ${name} unhealthy, restarting`);
        try { svc.stop?.(); } catch {}
        await svc.mod.start(ctx);
        services.set(name, { mod: svc.mod, query: svc.mod.query, stop: svc.mod.stop, health: svc.mod.health });
      }
    } catch (e: any) {
      console.error(`[worker:${WORKER_NAME}] service ${name} health check failed: ${e.message}`);
    }
  }
}, 30_000);

// === NDJSON request loop (C# → Worker) ===

(async () => {
  const decoder = new TextDecoder();
  let partial = "";

  for await (const chunk of Bun.stdin.stream()) {
    partial += decoder.decode(chunk);
    const lines = partial.split("\n");
    partial = lines.pop()!;

    for (const line of lines) {
      if (!line.trim()) continue;
      try {
        const req = JSON.parse(line) as Request;

        if (req.type === "shutdown") {
          for (const [, svc] of services) svc.stop?.();
          process.exit(0);
        }

        if (req.type === "worker_ports") {
          workerPorts = req.data;
          continue;
        }

        if (req.type === "query") {
          const svc = services.get(req.service);
          if (!svc) {
            console.log(JSON.stringify({ id: req.id, error: `Unknown service: ${req.service}` }));
            continue;
          }
          const result = await svc.query?.(req.args ?? {});
          console.log(JSON.stringify({ id: req.id, result }));
        }

        else if (req.type === "health") {
          const results: Record<string, any> = {};
          for (const [name, svc] of services)
            results[name] = svc.health?.() ?? { ok: true };
          console.log(JSON.stringify({ id: req.id, result: results }));
        }

        else if (req.type === "action") {
          for (const [, handler] of actionHandlers) handler(req.action);
        }

        else if (req.type === "push") {
          serverRef.publish(req.channel, JSON.stringify({ type: req.channel, data: req.data }));
        }

        else if (req.type === "relay_in") {
          // Incoming relay from another worker via C# — dispatch to local services as a push
          serverRef.publish(req.channel, JSON.stringify({ type: req.channel, data: req.data }));
          // Also fire any registered web message handlers for this channel
          const handler = webMessageHandlers.get(req.channel);
          if (handler) handler(req.data, null as any);
        }

        else if (req.type === "eval") {
          if (IS_EXTENSION_HOST) {
            console.log(JSON.stringify({ id: req.id, error: "eval disabled in extension host" }));
          } else {
            const result = await eval(req.code);
            console.log(JSON.stringify({ id: req.id, result }));
          }
        }

      } catch (e: any) {
        console.log(JSON.stringify({ id: 0, error: e.message }));
      }
    }
  }
})();
