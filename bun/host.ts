/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

// host.ts — Keystone Bun Runtime
// Framework subprocess: services (backend logic), web components (frontend), actions, hot-reload.
// Users build their UI entirely in web/ (any framework — vanilla, React, Vue, Svelte, etc.)
// and their backend logic in services/ (data fetching, APIs, persistent state, background work).
// C# handles native window chrome, GPU rendering, and system integration.
// Communication: NDJSON stdin/stdout (control) + Unix socket (binary/streams) + WebSocket bridge (web clients).
//
// All behavior is driven by the app's keystone.config.ts (or defaults if absent).

process.title = process.env.KEYSTONE_APP_NAME ?? process.env.KEYSTONE_APP_ID ?? "keystone";

import { readdirSync, existsSync, statSync, watch, realpathSync, readFileSync } from "fs";
import { join } from "path";
import type { ServerWebSocket } from "bun";
import type { ServiceContext, WorkerConnection, AppHostModule, HostContext } from "./types";
import { HandlerRegistry } from "./lib/handler-registry";
import { createIpcFacade, type IpcFacade } from "./lib/ipc";
import { StreamRegistry } from "./lib/stream";
import { BinarySocket } from "./lib/binary-socket";
import type { KeystoneEnvelope } from "./types";
import { resolveConfig, type ResolvedConfig } from "./sdk/config";

// Engine runtime root (host.ts, types.ts, lib/)
const ENGINE_ROOT = import.meta.dir;
// App root — where the app's web/, services/ live.
// Passed as first CLI arg by ApplicationRuntime, defaults to ENGINE_ROOT for dev.
const APP_ROOT = process.argv[2] || ENGINE_ROOT;
process.env.KEYSTONE_APP_ROOT = APP_ROOT;

// Per-launch session token — all WS upgrades must present this in ?token= query param
const WS_TOKEN = process.env.KEYSTONE_SESSION_TOKEN ?? '';

// === Load app config ===

async function loadAppConfig(): Promise<ResolvedConfig> {
  // 1. Pre-resolved JSON config — written by the packager for distribution.
  //    Contains the fully resolved config so no .ts evaluation is needed at runtime.
  const resolvedPath = join(APP_ROOT, "keystone.resolved.json");
  if (existsSync(resolvedPath)) {
    try {
      const resolved = JSON.parse(readFileSync(resolvedPath, "utf-8"));
      console.error(`[host] loaded pre-resolved config from ${resolvedPath}`);
      return resolved as ResolvedConfig;
    } catch (e: any) {
      console.error(`[host] failed to load resolved config: ${e.message}, falling back`);
    }
  }

  // 2. Dev mode — evaluate keystone.config.ts
  let userConfig: any = {};
  const tsConfigPath = join(APP_ROOT, "keystone.config.ts");
  if (existsSync(tsConfigPath)) {
    try {
      const raw = await import(tsConfigPath);
      userConfig = raw.default ?? raw;
      console.error(`[host] loaded config from ${tsConfigPath}`);
    } catch (e: any) {
      console.error(`[host] failed to load config: ${e.message}, using defaults`);
    }
  }

  // 3. Check parent keystone.config.json for packager-injected flags (preBuiltWeb)
  //    In a packaged .app: APP_ROOT = Resources/bun/, JSON config = Resources/keystone.config.json
  for (const jsonPath of [
    join(APP_ROOT, "..", "keystone.config.json"),
    join(APP_ROOT, "keystone.config.json"),
  ]) {
    if (existsSync(jsonPath)) {
      try {
        const json = JSON.parse(readFileSync(jsonPath, "utf-8"));
        const bunCfg = json.bun;
        if (bunCfg?.preBuiltWeb) {
          userConfig.web = { ...userConfig.web, preBuilt: true };
          console.error(`[host] pre-built mode enabled from ${jsonPath}`);
        }
        break;
      } catch {}
    }
  }

  return resolveConfig(userConfig);
}

const config = await loadAppConfig();

// === Security policy ===

const DEFAULT_ALLOWED_ACTION_RULES = [
  "window:*",
  "app:quit",
  "shell:openExternal:*",
];

function matchActionRule(action: string, rule: string): boolean {
  return rule.endsWith("*")
    ? action.startsWith(rule.slice(0, -1))
    : action === rule;
}

function resolveSecurityMode(): "open" | "allowlist" {
  const mode = config.security.mode;
  if (mode === "open" || mode === "allowlist") return mode;
  // Auto mode: keep dev friction low, tighten packaged builds by default.
  return config.web.preBuilt ? "allowlist" : "open";
}

function resolveAllowedActionRules(): string[] {
  return config.security.allowedActions.length > 0
    ? config.security.allowedActions
    : DEFAULT_ALLOWED_ACTION_RULES;
}

function resolveEvalEnabled(): boolean {
  const allowEval = config.security.allowEval;
  if (allowEval === true || allowEval === false) return allowEval;
  // Auto mode: eval enabled for local development, off by default for packaged builds.
  return !config.web.preBuilt;
}

const effectiveSecurityMode = resolveSecurityMode();
const effectiveAllowedActionRules = resolveAllowedActionRules();
const effectiveAllowEval = resolveEvalEnabled();
const usingDefaultActionRules = config.security.allowedActions.length === 0;

// === Network endpoint allow-list ===

const networkMode = (Bun.env.KEYSTONE_NETWORK_MODE ?? config.security.networkMode ?? "open") as "open" | "allowlist";
const networkEndpoints = new Set<string>(
  (Bun.env.KEYSTONE_NETWORK_ENDPOINTS ?? "").split(",").filter(Boolean)
);
// Merge any declared in bun config
for (const ep of config.security.networkEndpoints ?? []) networkEndpoints.add(ep);

function isEndpointAllowed(hostPort: string): boolean {
  if (networkMode !== "allowlist") return true;
  if (networkEndpoints.has(hostPort)) return true;
  // Strip port and check hostname-only
  const lastColon = hostPort.lastIndexOf(":");
  const host = lastColon > 0 ? hostPort.slice(0, lastColon) : hostPort;
  if (networkEndpoints.has(host)) return true;
  // Check wildcard suffixes
  for (const ep of networkEndpoints) {
    if (ep.startsWith("*.") && host.endsWith(ep.slice(1))) return true;
  }
  return false;
}

// Store original fetch before patching — unrestricted services get this reference
const originalFetch = globalThis.fetch;
(globalThis as any).__KEYSTONE_ORIGINAL_FETCH__ = originalFetch;

if (networkMode === "allowlist") {
  globalThis.fetch = Object.assign(function(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
    const url = new URL(typeof input === "string" ? input : input instanceof URL ? input.href : input.url);
    const hostPort = url.port && url.port !== "443" && url.port !== "80"
      ? `${url.hostname}:${url.port}` : url.hostname;
    if (!isEndpointAllowed(hostPort)) {
      throw new Error(`[network-policy] ${url.hostname} is not in the allowed endpoints list`);
    }
    return originalFetch(input, init);
  }, { preconnect: originalFetch.preconnect }) as typeof fetch;
}

console.error(
  `[host] security mode=${effectiveSecurityMode} ` +
  `eval=${effectiveAllowEval ? "enabled" : "disabled"} ` +
  `actions=${usingDefaultActionRules ? "framework-defaults" : "config"} ` +
  `network=${networkMode}${networkMode === "allowlist" ? ` (${networkEndpoints.size} endpoints)` : ""}`
);

// === Registries ===

const actionHandlers = new Map<string, (action: string) => void>();
// Custom web message handlers — ownership-tracked, conflicts fail fast.
// Services register these via ctx.onWebMessage(type, fn).
const webMessageHandlers = new HandlerRegistry<(data: any, ws: ServerWebSocket) => void | Promise<void>>();
// Host push handlers — services subscribe via ctx.onHostPush(channel, fn) to receive C# push messages
const hostPushHandlers = new Map<string, Array<(data: any) => void>>();
// Named invoke handlers — ownership-tracked, conflicts fail fast at registration.
// Browser calls via invoke("channel", args) and gets a promise-based reply. Electron ipcMain.handle parity.
const invokeHandlers = new HandlerRegistry<(args: any, signal?: AbortSignal) => any | Promise<any>>();
// HTTP handlers — ownership-tracked, conflicts fail fast.
// Matched by longest-prefix-first in the main fetch handler before 404.
const httpHandlers = new HandlerRegistry<(req: globalThis.Request, url: URL) => globalThis.Response | Promise<globalThis.Response | null> | null>();
// In-flight invoke/query operations — keyed by request id, aborted on __cancel__ from browser
const inflightOps = new Map<number, AbortController>();
// Binary WebSocket handlers — ownership-tracked, conflicts fail fast.
// Raw ArrayBuffer frames, no JSON. Connections via /ws-bin?channel=name. Used for stream lane, VS Code compat, etc.
type BinaryHandler = {
  onOpen?: (ws: ServerWebSocket) => void;
  onMessage?: (ws: ServerWebSocket, data: Buffer) => void;
  onClose?: (ws: ServerWebSocket) => void;
};
const binaryHandlers = new HandlerRegistry<BinaryHandler>();
// Binary socket — dedicated Unix domain socket for stream/binary IPC with C# host.
// Path injected via KEYSTONE_BINARY_SOCKET env var. stdin/stdout stays pure NDJSON.
const binarySocket = new BinarySocket();
const binarySocketPath = Bun.env.KEYSTONE_BINARY_SOCKET;

// Stream registry — tracks open streams (host↔bun, browser↔bun) with lifecycle and backpressure.
// Transport: envelopes sent over the binary socket (not stdout).
const streams = new StreamRegistry((env) => binarySocket.send(env));

type ServiceEntry = {
  mod: any;
  query: (args?: any, signal?: AbortSignal) => any;
  stop?: () => void;
  health?: () => { ok: boolean; [k: string]: any };
};
const services = new Map<string, ServiceEntry>();

// === Directories (from config) ===

const serviceDir = join(APP_ROOT, config.services.dir);
const webDir = join(APP_ROOT, config.web.dir);

// === Service context (passed to service start()) ===

// Server reference — assigned after Bun.serve() if http is enabled, otherwise a no-op publisher
let serverRef: { publish: (topic: string, data: string) => void; port: number } = {
  publish: () => {},
  port: 0,
};

// Server-side value retention — last value per channel, replayed to new WS clients on connect
const valueCache = new Map<string, string>();

// === Worker direct connections ===

let workerPorts: Record<string, number> = {};

function connectToWorker(name: string): WorkerConnection {
  const port = workerPorts[name];
  if (!port) throw new Error(`Worker "${name}" has no WebSocket (browserAccess=false or not yet discovered)`);

  const ws = new WebSocket(`ws://127.0.0.1:${port}/ws?token=${WS_TOKEN}`);
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
      if (msg.type === "__query_result__" && msg.id != null) {
        const cb = pending.get(msg.id);
        if (cb) {
          pending.delete(msg.id);
          if (msg.error) cb.reject(new Error(msg.error));
          else cb.resolve(msg.result);
        }
        return;
      }
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

// Cached worker connections for ipc.worker(name).call() round-trip
const workerConnections = new Map<string, WorkerConnection>();
function getWorkerConnection(name: string): WorkerConnection {
  let conn = workerConnections.get(name);
  if (!conn) {
    conn = connectToWorker(name);
    workerConnections.set(name, conn);
  }
  return conn;
}

// Tracks which service is currently being started — used as handler ownership key.
let currentServiceOwner = "__host__";

// Host query callbacks — Bun→C# request/reply (B6: bidirectional call primitive)
let hostQueryNextId = 1;
const hostQueryCallbacks = new Map<number, { resolve: (v: any) => void; reject: (e: Error) => void }>();

function hostQuery(service: string, args?: any): Promise<any> {
  const id = hostQueryNextId++;
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      hostQueryCallbacks.delete(id);
      reject(new Error(`hostQuery("${service}") timed out after 30s`));
    }, 30_000);
    hostQueryCallbacks.set(id, {
      resolve(v) { clearTimeout(timer); resolve(v); },
      reject(e) { clearTimeout(timer); reject(e); },
    });
    console.log(JSON.stringify({ id, type: "query_host", service, args }));
  });
}

const ipc: IpcFacade = createIpcFacade({
  call: async (service, args) => {
    const svc = services.get(service);
    if (!svc) throw new Error(`Unknown service: ${service}`);
    return await svc.query?.(args);
  },
  push: (channel, data) => {
    const msg = JSON.stringify({ type: channel, data });
    console.log(JSON.stringify({ type: "service_push", channel, data }));
    serverRef.publish(channel, msg);
  },
  pushValue: (channel, data) => {
    const msg = JSON.stringify({ type: channel, data });
    valueCache.set(channel, msg);
    console.log(JSON.stringify({ type: "service_push", channel, data }));
    serverRef.publish(channel, msg);
  },
  relay: (target, channel, data) => {
    console.log(JSON.stringify({ type: "relay", target, channel, data }));
  },
  hostQuery,
  hostAction: (action) => {
    console.log(JSON.stringify({ type: "action_from_web", action }));
  },
  workerQuery: (name, service, args) => getWorkerConnection(name).query(service, args),
});

const ctx: ServiceContext = {
  call: ipc.call,
  push: ipc.web.push,
  pushValue: ipc.web.pushValue,
  onWebMessage: (type, handler) => {
    webMessageHandlers.register(type, handler, currentServiceOwner);
  },
  onHostPush: (channel, handler) => {
    const arr = hostPushHandlers.get(channel);
    if (arr) arr.push(handler);
    else hostPushHandlers.set(channel, [handler]);
  },
  registerInvokeHandler: (channel, handler) => {
    invokeHandlers.register(channel, handler, currentServiceOwner);
  },
  relay: (target, channel, data) => {
    console.log(JSON.stringify({ type: "relay", target, channel, data }));
  },
  registerBinaryWebSocket: (channel, handler) => {
    binaryHandlers.register(channel, handler, currentServiceOwner);
  },
  registerHttpHandler: (prefix, handler) => {
    httpHandlers.register(prefix, handler, currentServiceOwner);
  },
  openStream: (channel, target) => streams.open(channel, target),
  onStream: (channel, handler) => streams.onStream(channel, handler),
  workers: {
    connect: connectToWorker,
    ports: () => workerPorts,
  },
  ipc,
};

// === Built-in engine services ===
// Registered before app service discovery — always available, never overwritten by app services.

function registerBuiltins() {
  // paths — equivalent to Electron's app.getPath(). Returns well-known filesystem paths.
  // APP_ROOT is the app's bun directory; userData is ~/.keystone/<app-id> (matches KeystoneDb).
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

  // security — expose effective policy so apps can inspect what is active at runtime.
  services.set("security", {
    mod: null,
    query: () => ({
      mode: effectiveSecurityMode,
      allowEval: effectiveAllowEval,
      usingDefaultActionRules,
      allowedActions: effectiveAllowedActionRules,
      preBuiltWeb: config.web.preBuilt,
      networkMode,
      networkEndpoints: [...networkEndpoints],
    }),
    health: () => ({ ok: true }),
  });
}

// === Discovery ===

async function discoverServices(bustCache = false) {
  // Compiled mode — services statically imported into the exe at package time.
  // The packager generates a wrapper that sets this global before importing host.ts.
  const compiled = (globalThis as any).__KEYSTONE_COMPILED_SERVICES__;
  if (compiled) {
    for (const [name, raw] of Object.entries(compiled) as [string, any][]) {
      const mod = raw.default ?? raw;
      mergeServiceNetwork(name, mod);
      if (mod.start) {
        currentServiceOwner = name;
        try { await mod.start(ctx); } finally { currentServiceOwner = "__host__"; }
        services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
      }
      if (mod.onAction) actionHandlers.set(name, mod.onAction);
    }
    console.error(`[host] compiled mode: ${services.size} services`);
    return;
  }

  if (!existsSync(serviceDir)) return;
  const cacheSuffix = bustCache ? `?t=${Date.now()}` : "";
  for (const entry of readdirSync(serviceDir)) {
    const entryPath = join(serviceDir, entry);
    const stat = statSync(entryPath);
    let mod: any, name: string;
    if (stat.isFile() && /\.[jt]s$/.test(entry)) {
      const imported = await import(entryPath + cacheSuffix);
      mod = imported.default ?? imported;
      name = entry.replace(/\.[jt]s$/, "");
    } else if (stat.isDirectory()) {
      const idx = join(entryPath, "index.ts");
      if (!existsSync(idx)) continue;
      const imported = await import(idx + cacheSuffix);
      mod = imported.default ?? imported;
      name = entry;
    } else continue;
    mergeServiceNetwork(name, mod);
    if (mod.start) {
      currentServiceOwner = name;
      try { await mod.start(ctx); } finally { currentServiceOwner = "__host__"; }
      services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
    }
    if (mod.onAction) actionHandlers.set(name, mod.onAction);
  }
}

/** Merge a service's declared network endpoints into the global allow-list. */
function mergeServiceNetwork(name: string, mod: any) {
  if (!mod.network) return;
  if (mod.network.endpoints) {
    for (const ep of mod.network.endpoints) networkEndpoints.add(ep);
    console.error(`[host] ${name}: merged ${mod.network.endpoints.length} network endpoints`);
  }
  if (mod.network.unrestricted) {
    console.error(`[host] ${name}: network unrestricted`);
  }
}

// === Reload ===

async function reloadService(name: string) {
  const existing = services.get(name);
  if (existing) {
    try { existing.stop?.(); } catch (e: any) {
      console.error(`[host] ${name} stop failed: ${e.message}`);
    }
  }

  // Clear ownership-tracked handlers before re-registering
  invokeHandlers.removeByOwner(name);
  httpHandlers.removeByOwner(name);
  webMessageHandlers.removeByOwner(name);
  binaryHandlers.removeByOwner(name);

  const dirPath = join(serviceDir, name);
  const filePath = join(serviceDir, name + ".ts");

  let entryPath: string;
  if (existsSync(dirPath) && statSync(dirPath).isDirectory()) {
    entryPath = join(dirPath, "index.ts");
  } else if (existsSync(filePath)) {
    entryPath = filePath;
  } else {
    console.error(`[host] service ${name} not found on disk, removing`);
    services.delete(name);
    actionHandlers.delete(name);
    return;
  }

  try {
    const imported = await import(entryPath + `?t=${Date.now()}`);
    const mod = imported.default ?? imported;
    if (mod.start) {
      currentServiceOwner = name;
      try { await mod.start(ctx); } finally { currentServiceOwner = "__host__"; }
      services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
      console.error(`[host] reloaded service: ${name}`);
    }
    if (mod.onAction) actionHandlers.set(name, mod.onAction);
    else actionHandlers.delete(name);
  } catch (e: any) {
    console.error(`[host] failed to reload service ${name}: ${e.message}`);
    services.delete(name);
  }
}

async function reloadAll() {
  console.error("[host] shared file changed, full reload");

  for (const [, svc] of services) {
    try { svc.stop?.(); } catch {}
  }

  actionHandlers.clear();
  invokeHandlers.clear();
  httpHandlers.clear();
  webMessageHandlers.clear();
  binaryHandlers.clear();
  services.clear();

  await discoverServices(true);
  if (config.web.autoBundle) await bundleWebComponents();

  emitReady();
}

// === File watcher — hot-reload (conditional) ===

const watchEnabled = !config.web.preBuilt && (config.services.hotReload || config.web.hotReload);

if (watchEnabled) {
  const SHARED_FILES = new Set([join(ENGINE_ROOT, "types.ts")]);
  const SHARED_DIRS = [join(ENGINE_ROOT, "lib")];
  const DEBOUNCE_MS = config.watch.debounceMs;
  const debounceTimers = new Map<string, Timer>();

  const watchPattern = new RegExp(
    config.watch.extensions.map(e => e.replace(".", "\\.")).join("|") + "$"
  );

  function isSharedFile(abs: string): boolean {
    if (SHARED_FILES.has(abs)) return true;
    for (const dir of SHARED_DIRS) {
      if (abs.startsWith(dir + "/")) return true;
    }
    return false;
  }

  function handleFileChange(abs: string) {
    const rel = abs.slice(APP_ROOT.length + 1);

    if (abs === join(ENGINE_ROOT, "host.ts")) {
      console.error("[host] host.ts changed — restart process to apply");
      return;
    }
    if (abs === join(APP_ROOT, "host.ts")) {
      console.error("[host] app host.ts changed — restart process to apply");
      return;
    }

    if (isSharedFile(abs)) {
      reloadAll();
      return;
    }

    // Web component changed — rebundle
    if (config.web.hotReload && /\.tsx?$/.test(rel)) {
      // Check explicit entry map first (file can live anywhere in APP_ROOT)
      const explicitName = explicitEntryMap.get(abs);
      if (explicitName) {
        Bun.build(buildOpts([abs], explicitName)).then((result: any) => {
          if (!result.success) {
            for (const log of result.logs) console.error(`[host] ${explicitName}: ${log.message}`);
            return;
          }
          bundledComponents.add(explicitName);
          console.error(`[host] rebundled component "${explicitName}"`);
          serverRef.publish("all", JSON.stringify({ type: "__hmr__", component: explicitName }));
        }).catch((e: any) => console.error(`[host] failed to rebundle "${explicitName}": ${e.message}`));
        return;
      }
      // Check if file is a dep inside a known component's directory → rebuild via entry
      // Pick the deepest (longest entryDir) match so subdirectory components
      // aren't shadowed by a shallower entry in a parent directory
      let bestEntry: string | undefined, bestName: string | undefined, bestLen = 0;
      for (const [entryAbs, name] of explicitEntryMap) {
        const entryDir = entryAbs.slice(0, entryAbs.lastIndexOf('/') + 1);
        if (abs.startsWith(entryDir) && entryDir.length > bestLen) {
          bestEntry = entryAbs; bestName = name; bestLen = entryDir.length;
        }
      }
      if (bestEntry && bestName) {
        const _entry = bestEntry, _name = bestName;
        Bun.build(buildOpts([_entry], _name)).then((result: any) => {
          if (!result.success) {
            for (const log of result.logs) console.error(`[host] ${_name}: ${log.message}`);
            return;
          }
          bundledComponents.add(_name);
          console.error(`[host] rebundled component "${_name}" (dep changed: ${rel})`);
          serverRef.publish("all", JSON.stringify({ type: "__hmr__", component: _name }));
        }).catch((e: any) => console.error(`[host] failed to rebundle "${_name}": ${e.message}`));
        return;
      }
      // Fall back to auto-discovered web.dir files
      if (rel.startsWith(config.web.dir + "/")) {
        const file = rel.replace(config.web.dir + "/", "");
        const name = file.replace(/\.[jt]sx?$/, "");
        Bun.build(buildOpts([abs], name)).then((result: any) => {
          if (!result.success) {
            for (const log of result.logs) console.error(`[host] ${name}: ${log.message}`);
            return;
          }
          bundledComponents.add(name);
          console.error(`[host] rebundled web component: ${name}`);
          serverRef.publish("all", JSON.stringify({ type: "__hmr__", component: name }));
        }).catch((e: any) => console.error(`[host] failed to rebundle ${name}: ${e.message}`));
        return;
      }
    }

    // Service changed — reload
    if (config.services.hotReload && rel.startsWith(config.services.dir + "/")) {
      const parts = rel.split("/");
      if (parts.length === 2 && /\.[jt]s$/.test(parts[1])) {
        reloadService(parts[1].replace(/\.[jt]s$/, ""));
      } else if (parts.length >= 2) {
        reloadService(parts[1]);
      }
    }
  }

  watch(APP_ROOT, { recursive: true }, (_event, filename) => {
    if (!filename || !watchPattern.test(filename) || filename.includes("node_modules")) return;
    const abs = join(APP_ROOT, filename);
    const existing = debounceTimers.get(abs);
    if (existing) clearTimeout(existing);
    debounceTimers.set(abs, setTimeout(() => {
      debounceTimers.delete(abs);
      handleFileChange(abs);
    }, DEBOUNCE_MS));
  });
}

// === Web component bundling ===

const bundledComponents = new Set<string>();

// Resolve @keystone/sdk/* imports to the SDK directory at bundle time.
// Prefer APP_ROOT's node_modules/@keystone/sdk when present — its path is
// clean. ENGINE_ROOT may be a .bun/ copy whose path contains '@' and '+'
// characters, which Bun's onResolve rejects as invalid absolute paths.
const sdkDir = (() => {
  const appSdk = join(APP_ROOT, "node_modules", "@keystone", "sdk");
  if (existsSync(appSdk)) {
    try { return realpathSync(appSdk); } catch { return appSdk; }
  }
  try { return realpathSync(join(ENGINE_ROOT, "sdk")); }
  catch { return join(ENGINE_ROOT, "sdk"); }
})();
const keystoneResolvePlugin = {
  name: "keystone-sdk",
  setup(build: any) {
    build.onResolve({ filter: /^@keystone\/sdk/ }, (args: any) => {
      const sub = args.path.replace("@keystone/sdk", "");
      return { path: join(sdkDir, sub ? sub + ".ts" : "index.ts") };
    });
  },
};

function buildOpts(entrypoints: string[], name: string) {
  return {
    entrypoints,
    outdir: webDir,
    target: "browser" as const,
    format: "esm" as const,
    naming: `${name}.[ext]`,
    plugins: [keystoneResolvePlugin],
  };
}

// Reverse map: absolute entry path → component name, for HMR of explicit entries
const explicitEntryMap = new Map<string, string>();

async function bundleWebComponents() {
  // Pre-built mode: skip Bun.build(), just register existing .js files.
  // Set by the packager for distribution bundles where web components are pre-bundled.
  if (config.web.preBuilt) {
    if (!existsSync(webDir)) return;
    for (const file of readdirSync(webDir)) {
      if (!file.endsWith(".js")) continue;
      const name = file.replace(/\.js$/, "");
      bundledComponents.add(name);
    }
    console.error(`[host] pre-built mode: registered ${bundledComponents.size} components`);
    return;
  }

  // 1. Explicit entries from config.web.components — name → relative entry path
  for (const [name, relPath] of Object.entries(config.web.components)) {
    const abs = join(APP_ROOT, relPath);
    explicitEntryMap.set(abs, name);
    try {
      const result = await Bun.build(buildOpts([abs], name));
      if (result.success) {
        bundledComponents.add(name);
      } else {
        for (const log of result.logs) console.error(`[host] ${name}: ${log.message}`);
      }
    } catch (e: any) {
      console.error(`[host] Failed to bundle component "${name}" from ${relPath}: ${e.message}`);
    }
  }

  // 2. Auto-discover .ts/.tsx files in web.dir (skip any already covered by explicit entries)
  if (!existsSync(webDir)) return;
  const explicitFiles = new Set(explicitEntryMap.keys());
  for (const file of readdirSync(webDir)) {
    if (!file.endsWith(".tsx") && !file.endsWith(".ts")) continue;
    const abs = join(webDir, file);
    if (explicitFiles.has(abs)) continue; // already registered by name above
    const name = file.replace(/\.[jt]sx?$/, "");
    try {
      const result = await Bun.build(buildOpts([abs], name));
      if (result.success) {
        bundledComponents.add(name);
      } else {
        for (const log of result.logs) console.error(`[host] ${name}: ${log.message}`);
      }
    } catch (e: any) {
      console.error(`[host] Failed to bundle ${config.web.dir}/${file}: ${e.message}`);
    }
  }
}

if (config.web.autoBundle) await bundleWebComponents();

// === HTTP server (conditional) ===

if (config.http.enabled) {
  const server = Bun.serve<{ binary?: boolean; channel?: string }>({
    hostname: config.http.hostname,
    port: 0,
    async fetch(req, server) {
      const url = new URL(req.url);

      if (url.pathname === "/ws") {
        if (url.searchParams.get("token") !== WS_TOKEN)
          return new Response("", { status: 401 });
        return server.upgrade(req, { data: {} }) ? undefined : new Response("", { status: 400 });
      }

      // Binary WebSocket — raw ArrayBuffer frames, no JSON wrapping.
      // Used by stream lane, Electron compatibility layer, and any app needing binary IPC.
      if (url.pathname === "/ws-bin") {
        if (url.searchParams.get("token") !== WS_TOKEN)
          return new Response("", { status: 401 });
        const channel = url.searchParams.get("channel") || "default";
        if (!binaryHandlers.has(channel))
          return new Response(`Unknown binary channel: ${channel}`, { status: 404 });
        return server.upgrade(req, { data: { binary: true, channel } })
          ? undefined : new Response("", { status: 400 });
      }

      if (url.pathname === "/__host__") {
        return new Response(generateHostShell(server.port), { headers: { "Content-Type": "text/html" } });
      }

      // Static assets from app directory
      const filePath = join(APP_ROOT, url.pathname.slice(1));
      const file = Bun.file(filePath);
      if (await file.exists()) return new Response(file, {
        headers: { "Content-Type": guessMime(filePath) },
      });

      // Component route: /dashboard?windowId=1 → generate HTML shell dynamically
      const component = url.pathname.slice(1);
      if (bundledComponents.has(component)) {
        const windowId = url.searchParams.get("windowId") ?? "";
        return new Response(generateShell(component, server.port, windowId), {
          headers: { "Content-Type": "text/html" },
        });
      }

      // Service-registered HTTP handlers — longest prefix match first
      if (httpHandlers.size > 0) {
        const prefixes = [...httpHandlers.keys()].sort((a, b) => b.length - a.length);
        for (const prefix of prefixes) {
          if (url.pathname === prefix || url.pathname.startsWith(prefix + '/') || url.pathname.startsWith(prefix)) {
            const result = await httpHandlers.get(prefix)!(req, url);
            if (result) return result;
          }
        }
      }

      return new Response("Not found", { status: 404 });
    },

    websocket: {
      open(ws: ServerWebSocket) {
        if ((ws.data as any)?.binary) {
          const channel = (ws.data as any).channel as string;
          binaryHandlers.get(channel)?.onOpen?.(ws);
          return;
        }
        ws.subscribe("all");
        for (const cached of valueCache.values()) ws.send(cached);
      },
      message(ws: ServerWebSocket, msg: string | Buffer) {
        if ((ws.data as any)?.binary) {
          const channel = (ws.data as any).channel as string;
          binaryHandlers.get(channel)?.onMessage?.(ws, msg as Buffer);
          return;
        }
        try {
          const { type, data } = JSON.parse(msg as string);
          handleWebMessage(ws, type, data);
        } catch {}
      },
      close(ws: ServerWebSocket) {
        if ((ws.data as any)?.binary) {
          const channel = (ws.data as any).channel as string;
          binaryHandlers.get(channel)?.onClose?.(ws);
          return;
        }
        ws.unsubscribe("all");
      },
    },
  });

  serverRef = server;
}

function guessMime(path: string): string {
  if (path.endsWith(".js")) return "application/javascript";
  if (path.endsWith(".css")) return "text/css";
  if (path.endsWith(".html")) return "text/html";
  if (path.endsWith(".json")) return "application/json";
  if (path.endsWith(".svg")) return "image/svg+xml";
  if (path.endsWith(".png")) return "image/png";
  if (path.endsWith(".woff2")) return "font/woff2";
  return "application/octet-stream";
}

function generateShell(component: string, port: number, windowId: string = ''): string {
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8">
<style>*{margin:0;padding:0;box-sizing:border-box}body{font-family:-apple-system,BlinkMacSystemFont,sans-serif;background:#1e1e23;overflow:hidden}#root{width:100vw;height:100vh}</style>
<link rel="stylesheet" href="/${config.web.dir}/${component}.css">
</head><body><div id="root"></div>
<script>
window.__KEYSTONE_PORT__=${port};
window.__KEYSTONE_SLOT_CTX__={slotKey:'${component}',windowId:'${windowId}'};
(()=>{
  const root=document.getElementById('root');
  let mod=null;
  const ctx=window.__KEYSTONE_SLOT_CTX__;
  const scriptBase='/${config.web.dir}/${component}.js';
  async function loadAndMount(url){
    try{
      if(mod&&mod.unmount)mod.unmount(root);
      root.innerHTML='';
      mod=await import(url);
      if(mod.mount)mod.mount(root,ctx);
    }catch(e){console.error('[shell] mount failed',e)}
  }
  loadAndMount(scriptBase);
  const ws=new WebSocket('ws://127.0.0.1:${port}/ws');
  ws.onmessage=e=>{
    try{
      const msg=JSON.parse(e.data);
      if(msg.type==='__hmr__'&&msg.component==='${component}')
        loadAndMount(scriptBase+'?t='+Date.now());
    }catch{}
  };
})();
</script>
</body></html>`;
}

function generateHostShell(port: number): string {
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8">
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:transparent;overflow:hidden;font-family:-apple-system,BlinkMacSystemFont,sans-serif}
.slot{position:absolute;overflow:hidden}
</style>
</head><body>
<script>
window.__KEYSTONE_PORT__=${port};
const slots={};
window.__addSlot=async(key,scriptUrl,x,y,w,h,windowId)=>{
  if(slots[key])return;
  const cssUrl=scriptUrl.replace(/\.js$/,'.css');
  const link=document.createElement('link');
  link.rel='stylesheet';link.href=cssUrl;
  document.head.appendChild(link);
  const div=document.createElement('div');
  div.className='slot';
  div.style.cssText='position:absolute;overflow:hidden;left:'+x+'px;top:'+y+'px;width:'+w+'px;height:'+h+'px';
  const root=document.createElement('div');
  root.style.cssText='width:100%;height:100%';
  div.appendChild(root);
  document.body.appendChild(div);
  slots[key]={div,root,mod:null,windowId:windowId||''};
  try{
    const mod=await import(scriptUrl);
    slots[key].mod=mod;
    if(mod.mount)mod.mount(root,{slotKey:key,windowId:windowId||''});
  }catch(e){console.error('[host] Failed to load',key,e)}
};
window.__moveSlot=(key,x,y,w,h)=>{
  const s=slots[key];
  if(!s)return;
  s.div.style.left=x+'px';
  s.div.style.top=y+'px';
  s.div.style.width=w+'px';
  s.div.style.height=h+'px';
};
window.__removeSlot=(key)=>{
  const s=slots[key];
  if(!s)return;
  try{if(s.mod&&s.mod.unmount)s.mod.unmount(s.root)}catch(e){console.error('[host] unmount failed',key,e)}
  s.div.remove();
  delete slots[key];
};
window.__hotSwapSlot=async(key,scriptUrl)=>{
  const s=slots[key];
  if(!s)return;
  try{if(s.mod&&s.mod.unmount)s.mod.unmount(s.root)}catch(e){}
  s.root.innerHTML='';
  try{
    const mod=await import(scriptUrl+'?t='+Date.now());
    s.mod=mod;
    if(mod.mount)mod.mount(s.root,{slotKey:key,windowId:s.windowId});
  }catch(e){console.error('[host] hot-swap failed',key,e)}
};
window.__ready=true;
</script>
</body></html>`;
}

function isActionAllowed(action: string): boolean {
  if (effectiveSecurityMode === "open") return true;
  return effectiveAllowedActionRules.some(rule => matchActionRule(action, rule));
}

async function handleWebMessage(ws: ServerWebSocket, type: string, data: any) {
  if (type === "subscribe") {
    ws.subscribe(data.channel);
    return;
  }
  if (type === "publish" && data?.channel) {
    serverRef.publish(data.channel, JSON.stringify({ type: data.channel, data: data.payload }));
    return;
  }
  if (type === "action" && data?.action) {
    if (!isActionAllowed(data.action)) {
      console.error(`[host] blocked web action (${effectiveSecurityMode}): ${data.action}`);
      return;
    }
    for (const [, handler] of actionHandlers) handler(data.action);
    appHost?.onAction?.(data.action, makeHostContext());
    console.log(JSON.stringify({ type: "action_from_web", action: data.action }));
    return;
  }
  if (type === "query" && data?.service) {
    const id = data.id;
    const ac = new AbortController();
    inflightOps.set(id, ac);
    try {
      const svc = services.get(data.service);
      if (!svc) throw new Error(`Unknown service: ${data.service}`);
      const result = await svc.query?.(data.args ?? {}, ac.signal);
      if (!ac.signal.aborted)
        ws.send(JSON.stringify({ type: "__query_result__", id, result }));
    } catch (e: any) {
      if (!ac.signal.aborted)
        ws.send(JSON.stringify({ type: "__query_result__", id, error: { code: e.name === 'AbortError' ? "cancelled" : "handler_error", message: e.message } }));
    } finally {
      inflightOps.delete(id);
    }
    return;
  }
  // Named invoke handler — registered by services via ctx.registerInvokeHandler(channel, fn)
  // Browser: invokeBun("channel", args) → promise reply. Electron ipcMain.handle() parity.
  if (type === "invoke" && data?.channel) {
    const handler = invokeHandlers.get(data.channel);
    const id = data.id;
    if (!handler) {
      ws.send(JSON.stringify({ type: "__invoke_reply__", id, error: { code: "handler_not_found", message: `No invoke handler: ${data.channel}` } }));
      return;
    }
    const ac = new AbortController();
    inflightOps.set(id, ac);
    try {
      const result = await handler(data.args ?? {}, ac.signal);
      if (!ac.signal.aborted)
        ws.send(JSON.stringify({ type: "__invoke_reply__", id, result }));
    } catch (e: any) {
      if (!ac.signal.aborted)
        ws.send(JSON.stringify({ type: "__invoke_reply__", id, error: { code: e.name === 'AbortError' ? "cancelled" : "handler_error", message: e.message } }));
    } finally {
      inflightOps.delete(id);
    }
    return;
  }
  // Cancellation from browser — abort in-flight handler if still running
  if (type === "__cancel__" && data?.id) {
    const ac = inflightOps.get(data.id);
    if (ac) { ac.abort(); inflightOps.delete(data.id); }
    return;
  }
  // Custom message handler — registered by services via ctx.onWebMessage(type, fn)
  const customHandler = webMessageHandlers.get(type);
  if (customHandler) {
    try { await customHandler(data, ws); } catch (e: any) {
      console.error(`[host] web message handler "${type}" threw: ${e.message}`);
    }
    return;
  }
  console.error(`[host] unhandled web message type: ${type}`);
}

// === App host module ===

async function loadAppHost(): Promise<AppHostModule | null> {
  const hostPath = join(APP_ROOT, "host.ts");
  if (!existsSync(hostPath)) return null;
  try {
    const raw = await import(hostPath);
    return (raw.default ?? raw) as AppHostModule;
  } catch (e: any) {
    console.error(`[host] failed to load app host.ts: ${e.message}`);
    return null;
  }
}

function makeHostContext(): HostContext {
  return {
    async registerService(name, mod) {
      if (mod.start) {
        currentServiceOwner = name;
        try { await mod.start(ctx); } finally { currentServiceOwner = "__host__"; }
      }
      services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
    },
    registerInvokeHandler(channel, handler) {
      invokeHandlers.register(channel, handler, "__host__");
    },
    onWebMessage(type, handler) {
      webMessageHandlers.register(type, handler, "__host__");
    },
    push: ipc.web.push,
    pushValue: ipc.web.pushValue,
    get services() { return services as ReadonlyMap<string, any>; },
    get config() { return config; },
    get ipc() { return ipc; },
  };
}

// === Boot ===

const appHost = await loadAppHost();
registerBuiltins();

if (appHost?.onBeforeStart) {
  await appHost.onBeforeStart(makeHostContext());
}

await discoverServices();

function emitReady() {
  console.log(JSON.stringify({
    status: "ready",
    services: [...services.keys()],
    webComponents: [...bundledComponents],
    port: serverRef.port,
  }));
}

// Connect binary socket for stream/binary IPC (before ready signal so C# can send immediately)
if (binarySocketPath) {
  try {
    await binarySocket.connect(binarySocketPath);
    binarySocket.onEnvelope = (env: KeystoneEnvelope) => {
      switch (env.kind) {
        case "stream_open": streams.handleOpen(env); break;
        case "stream_chunk": streams.handleChunk(env); break;
        case "stream_close":
        case "cancel": streams.handleClose(env); break;
      }
    };
  } catch (e: any) {
    console.error(`[host] Binary socket connect failed: ${e.message}`);
  }
}

emitReady();

if (appHost?.onReady) {
  await appHost.onReady(makeHostContext());
}

// === Health monitor (conditional) ===

if (config.health.enabled) {
  setInterval(async () => {
    for (const [name, svc] of services) {
      try {
        const status = svc.health?.() ?? { ok: true };
        if (!status.ok) {
          console.error(`[host] service ${name} unhealthy, restarting`);
          try { svc.stop?.(); } catch {}
          invokeHandlers.removeByOwner(name);
          httpHandlers.removeByOwner(name);
          webMessageHandlers.removeByOwner(name);
          binaryHandlers.removeByOwner(name);
          currentServiceOwner = name;
          try { await svc.mod.start(ctx); } finally { currentServiceOwner = "__host__"; }
          services.set(name, { mod: svc.mod, query: svc.mod.query, stop: svc.mod.stop, health: svc.mod.health });
          console.error(`[host] service ${name} restarted`);
        }
      } catch (e: any) {
        console.error(`[host] service ${name} health check failed: ${e.message}`);
      }
    }
  }, config.health.intervalMs);
}

// === NDJSON request loop (C# → Bun) ===

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
        const req = JSON.parse(line);

        // Host query response — C# replying to a Bun→C# query_host request
        if (req.id && !req.type && hostQueryCallbacks.has(req.id)) {
          const cb = hostQueryCallbacks.get(req.id)!;
          hostQueryCallbacks.delete(req.id);
          if (req.error) cb.reject(new Error(typeof req.error === "string" ? req.error : req.error.message));
          else cb.resolve(req.result);
          continue;
        }

        if (req.type === "shutdown") {
          if (appHost?.onShutdown) await appHost.onShutdown(makeHostContext());
          for (const [, svc] of services) svc.stop?.();
          process.exit(0);
        }

        if (req.type === "worker_ports") {
          workerPorts = req.data;
          continue;
        }

        if (req.type === "relay_in") {
          serverRef.publish(req.channel, JSON.stringify({ type: req.channel, data: req.data }));
          const handler = webMessageHandlers.get(req.channel);
          if (handler) handler(req.data, null as any);
          continue;
        }

        if (req.type === "query") {
          const svc = services.get(req.service);
          if (!svc) {
            console.log(JSON.stringify({ id: req.id, error: { code: "service_not_found", message: `Unknown service: ${req.service}` } }));
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
          appHost?.onAction?.(req.action, makeHostContext());
          serverRef.publish("all", JSON.stringify({ type: "action", action: req.action }));
        }

        else if (req.type === "push") {
          serverRef.publish(req.channel, JSON.stringify({ type: req.channel, data: req.data }));
          const handlers = hostPushHandlers.get(req.channel);
          if (handlers) for (const fn of handlers) fn(req.data);
        }

        else if (req.type === "push_value") {
          const msg = JSON.stringify({ type: req.channel, data: req.data });
          valueCache.set(req.channel, msg);
          serverRef.publish(req.channel, msg);
        }

        else if (req.type === "eval") {
          if (!effectiveAllowEval) {
            console.log(JSON.stringify({ id: req.id, error: { code: "capability_denied", message: "eval disabled by security policy" } }));
          } else {
            const result = await eval(req.code);
            console.log(JSON.stringify({ id: req.id, result }));
          }
        }

      } catch (e: any) {
        console.log(JSON.stringify({ id: 0, error: { code: "handler_error", message: e.message } }));
      }
    }
  }
})();
