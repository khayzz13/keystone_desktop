// host.ts — Keystone Bun Runtime
// Framework subprocess: services (backend logic), web components (frontend), actions, hot-reload.
// Users build their UI entirely in web/ (any framework — vanilla, React, Vue, Svelte, etc.)
// and their backend logic in services/ (data fetching, APIs, persistent state, background work).
// C# handles native window chrome, GPU rendering, and system integration.
// Communication: NDJSON over stdin/stdout + WebSocket bridge for web clients.
//
// All behavior is driven by the app's keystone.config.ts (or defaults if absent).

process.title = process.env.KEYSTONE_APP_NAME ?? process.env.KEYSTONE_APP_ID ?? "keystone";

import { readdirSync, existsSync, statSync, watch, realpathSync } from "fs";
import { join } from "path";
import type { ServerWebSocket } from "bun";
import type { Request, ServiceContext, WorkerConnection } from "./types";
import { resolveConfig, type ResolvedConfig } from "./sdk/config";

// Engine runtime root (host.ts, types.ts, lib/)
const ENGINE_ROOT = import.meta.dir;
// App root — where the app's web/, services/ live.
// Passed as first CLI arg by ApplicationRuntime, defaults to ENGINE_ROOT for dev.
const APP_ROOT = process.argv[2] || ENGINE_ROOT;
process.env.KEYSTONE_APP_ROOT = APP_ROOT;

// === Load app config ===

async function loadAppConfig(): Promise<ResolvedConfig> {
  const configPath = join(APP_ROOT, "keystone.config.ts");
  if (existsSync(configPath)) {
    try {
      const mod = require(configPath);
      const userConfig = mod.default ?? mod;
      console.error(`[host] loaded config from ${configPath}`);
      return resolveConfig(userConfig);
    } catch (e: any) {
      console.error(`[host] failed to load config: ${e.message}, using defaults`);
    }
  }
  return resolveConfig({});
}

const config = await loadAppConfig();

// === Registries ===

const actionHandlers = new Map<string, (action: string) => void>();
// Custom web message handlers — services register these via ctx.onWebMessage(type, fn)
const webMessageHandlers = new Map<string, (data: any, ws: ServerWebSocket) => void | Promise<void>>();
// Named invoke handlers — services register these via ctx.registerInvokeHandler(channel, fn)
// Browser calls via invoke("channel", args) and gets a promise-based reply. Electron ipcMain.handle parity.
const invokeHandlers = new Map<string, (args: any) => any | Promise<any>>();

type ServiceEntry = {
  mod: any;
  query: (args?: any) => any;
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

// Cache the last __theme__ push so new WS clients get it immediately on connect
let lastTheme: string | null = null;

// === Worker direct connections ===

let workerPorts: Record<string, number> = {};

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

const ctx: ServiceContext = {
  call: async (service, args) => {
    const svc = services.get(service);
    if (!svc) throw new Error(`Unknown service: ${service}`);
    return await svc.query?.(args);
  },
  push: (channel, data) => {
    const msg = JSON.stringify({ type: channel, data });
    if (channel === "__theme__") lastTheme = msg;
    console.log(JSON.stringify({ type: "service_push", channel, data }));
    serverRef.publish(channel, msg);
  },
  onWebMessage: (type, handler) => {
    webMessageHandlers.set(type, handler);
  },
  registerInvokeHandler: (channel, handler) => {
    invokeHandlers.set(channel, handler);
  },
  relay: (target, channel, data) => {
    console.log(JSON.stringify({ type: "relay", target, channel, data }));
  },
  workers: {
    connect: connectToWorker,
    ports: () => workerPorts,
  },
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
}

// === Discovery ===

async function discoverServices() {
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
    if (mod.start) {
      await mod.start(ctx);
      services.set(name, { mod, query: mod.query, stop: mod.stop, health: mod.health });
    }
    if (mod.onAction) actionHandlers.set(name, mod.onAction);
  }
}

// === Require cache helpers ===

function clearCacheForDir(dir: string) {
  for (const key of Object.keys(require.cache)) {
    if (key.startsWith(dir)) delete require.cache[key];
  }
}

function clearCacheForFile(file: string) {
  delete require.cache[file];
}

// === Reload ===

async function reloadService(name: string) {
  const existing = services.get(name);
  if (existing) {
    try { existing.stop?.(); } catch (e: any) {
      console.error(`[host] ${name} stop failed: ${e.message}`);
    }
  }

  const dirPath = join(serviceDir, name);
  const filePath = join(serviceDir, name + ".ts");

  let entryPath: string;
  if (existsSync(dirPath) && statSync(dirPath).isDirectory()) {
    clearCacheForDir(dirPath);
    entryPath = join(dirPath, "index.ts");
  } else if (existsSync(filePath)) {
    clearCacheForFile(filePath);
    entryPath = filePath;
  } else {
    console.error(`[host] service ${name} not found on disk, removing`);
    services.delete(name);
    actionHandlers.delete(name);
    return;
  }

  try {
    const mod = require(entryPath);
    if (mod.start) {
      await mod.start(ctx);
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
  services.clear();

  for (const key of Object.keys(require.cache)) {
    if (key.startsWith(APP_ROOT) && !key.includes("node_modules")) {
      delete require.cache[key];
    }
  }

  await discoverServices();
  if (config.web.autoBundle) await bundleWebComponents();

  emitReady();
}

// === File watcher — hot-reload (conditional) ===

const watchEnabled = config.services.hotReload || config.web.hotReload;

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
  const server = Bun.serve({
    hostname: config.http.hostname,
    port: 0,
    async fetch(req, server) {
      const url = new URL(req.url);

      if (url.pathname === "/ws") {
        return server.upgrade(req) ? undefined : new Response("", { status: 400 });
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

      return new Response("Not found", { status: 404 });
    },

    websocket: {
      open(ws: ServerWebSocket) {
        ws.subscribe("all");
        if (lastTheme) ws.send(lastTheme);
      },
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
  const allowed = config.security.allowedActions;
  if (!allowed || allowed.length === 0) return true; // open model
  return allowed.some(rule =>
    rule.endsWith("*") ? action.startsWith(rule.slice(0, -1)) : action === rule
  );
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
      console.error(`[host] blocked web action not in allowedActions: ${data.action}`);
      return;
    }
    for (const [, handler] of actionHandlers) handler(data.action);
    console.log(JSON.stringify({ type: "action_from_web", action: data.action }));
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
  // Named invoke handler — registered by services via ctx.registerInvokeHandler(channel, fn)
  // Browser: invoke("channel", args) → promise reply. Electron ipcMain.handle() parity.
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

// === Boot ===

registerBuiltins();
await discoverServices();

function emitReady() {
  console.log(JSON.stringify({
    status: "ready",
    services: [...services.keys()],
    webComponents: [...bundledComponents],
    port: serverRef.port,
  }));
}

emitReady();

// === Health monitor (conditional) ===

if (config.health.enabled) {
  setInterval(async () => {
    for (const [name, svc] of services) {
      try {
        const status = svc.health?.() ?? { ok: true };
        if (!status.ok) {
          console.error(`[host] service ${name} unhealthy, restarting`);
          try { svc.stop?.(); } catch {}
          await svc.mod.start(ctx);
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
        const req = JSON.parse(line) as Request;

        if (req.type === "shutdown") {
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
          serverRef.publish("all", JSON.stringify({ type: "action", action: req.action }));
        }

        else if (req.type === "push") {
          serverRef.publish(req.channel, JSON.stringify({ type: req.channel, data: req.data }));
        }

        else if (req.type === "eval") {
          const result = await eval(req.code);
          console.log(JSON.stringify({ id: req.id, result }));
        }

      } catch (e: any) {
        console.log(JSON.stringify({ id: 0, error: e.message }));
      }
    }
  }
})();
