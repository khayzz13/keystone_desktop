// @keystone/config — App-side configuration for the Keystone Bun runtime
// Users create a keystone.config.ts in their bun root that exports a config object.
// The runtime loads this at startup to determine behavior.

export type KeystoneRuntimeConfig = {
  /** Service discovery and lifecycle */
  services?: {
    /** Directory containing service modules (relative to app root). Default: "services" */
    dir?: string;
    /** Enable hot-reload of services on file change. Default: true */
    hotReload?: boolean;
  };

  /** Web component bundling and serving */
  web?: {
    /** Directory containing web component source files. Default: "web" */
    dir?: string;
    /** Auto-bundle .ts/.tsx files on startup with Bun.build. Default: true */
    autoBundle?: boolean;
    /** Hot-reload web components on file change (triggers rebundle + HMR push). Default: true */
    hotReload?: boolean;
    /**
     * Explicit component entries — name → entry file path (relative to bun root).
     * When set, these are bundled in addition to any .ts/.tsx files discovered in web.dir.
     * The key is the component name used in keystone.json windows[].component.
     * The value can be any .ts/.tsx file anywhere in the bun directory.
     *
     * @example
     * components: {
     *   "dashboard": "src/dashboard.ts",
     *   "settings": "src/settings/index.ts",
     * }
     */
    components?: Record<string, string>;
    /**
     * Pre-built mode — set by the packager for distribution bundles.
     * When true, host.ts skips Bun.build() and serves existing .js/.css from web.dir.
     * Disables HMR and file watching for web components.
     */
    preBuilt?: boolean;
  };

  /** HTTP server that serves bundled web components and static assets */
  http?: {
    /** Enable the HTTP server. Default: true */
    enabled?: boolean;
    /** Hostname to bind. Default: "127.0.0.1" */
    hostname?: string;
  };

  /** File watcher settings */
  watch?: {
    /** File extensions to watch. Default: [".ts", ".tsx", ".js", ".jsx"] */
    extensions?: string[];
    /** Debounce interval in ms. Default: 150 */
    debounceMs?: number;
  };

  /** Service health monitoring */
  health?: {
    /** Enable periodic health checks. Default: true */
    enabled?: boolean;
    /** Health check interval in ms. Default: 30000 */
    intervalMs?: number;
  };

  /** Security */
  security?: {
    /**
     * Action security mode.
     * - "auto" (default): open in dev, allowlist in pre-built/package mode
     * - "open": allow any action string
     * - "allowlist": require action to match allowedActions (or framework defaults if empty)
     */
    mode?: "auto" | "open" | "allowlist";
    /**
     * Allowlist of action strings web components may dispatch via keystone().action().
     * Supports exact strings and prefix wildcards ending in "*" (e.g. "spawn:*").
     * In allowlist mode, empty uses framework defaults for built-in window/app actions.
     */
    allowedActions?: string[];
    /**
     * Controls NDJSON eval requests from the C# host.
     * - "auto" (default): enabled in dev, disabled in pre-built/package mode
     * - true: always enabled
     * - false: always disabled
     */
    allowEval?: "auto" | boolean;

    /**
     * Network endpoint allow-list mode. Inherited from C# config via KEYSTONE_NETWORK_MODE env var.
     * - "open" (default): no restrictions on outbound fetch
     * - "allowlist": only allowed hostnames/ports may be fetched
     * Normally set via keystone.config.json security.network.mode on the C# side.
     */
    networkMode?: "open" | "allowlist";

    /**
     * Allowed network endpoints (hostname or hostname:port).
     * Supports wildcard prefix: "*.example.com" matches any subdomain.
     * Inherited from C# config via KEYSTONE_NETWORK_ENDPOINTS env var.
     * Services can merge additional endpoints via defineService().network().
     */
    networkEndpoints?: string[];
  };
};

/** Resolved config with all defaults applied */
export type ResolvedConfig = Required<{
  [K in keyof KeystoneRuntimeConfig]: Required<NonNullable<KeystoneRuntimeConfig[K]>>
}>;

const defaults: ResolvedConfig = {
  services: { dir: "services", hotReload: true },
  web: { dir: "web", autoBundle: true, hotReload: true, components: {}, preBuilt: false },
  http: { enabled: true, hostname: "127.0.0.1" },
  watch: { extensions: [".ts", ".tsx", ".js", ".jsx"], debounceMs: 150 },
  health: { enabled: true, intervalMs: 30_000 },
  security: { mode: "auto", allowedActions: [], allowEval: "auto", networkMode: "open", networkEndpoints: [] },
};

/** Define your app's Keystone Desktop configuration. */
export function defineConfig(config: KeystoneRuntimeConfig): KeystoneRuntimeConfig {
  return config;
}

/** Merge user config with defaults. Used internally by host.ts. */
export function resolveConfig(user: KeystoneRuntimeConfig): ResolvedConfig {
  const resolved: ResolvedConfig = {
    services: { ...defaults.services, ...user.services },
    web: { ...defaults.web, ...user.web, components: { ...defaults.web.components, ...user.web?.components } },
    http: { ...defaults.http, ...user.http },
    watch: { ...defaults.watch, ...user.watch },
    health: { ...defaults.health, ...user.health },
    security: { ...defaults.security, ...user.security },
  };

  validateResolvedConfig(resolved);
  return resolved;
}

function validateResolvedConfig(config: ResolvedConfig): void {
  if (!config.services.dir.trim()) throw new Error("services.dir must be a non-empty string");
  if (!config.web.dir.trim()) throw new Error("web.dir must be a non-empty string");
  if (!config.http.hostname.trim()) throw new Error("http.hostname must be a non-empty string");
  if (config.watch.debounceMs < 0) throw new Error("watch.debounceMs must be >= 0");
  if (config.health.intervalMs <= 0) throw new Error("health.intervalMs must be > 0");

  if (!["auto", "open", "allowlist"].includes(config.security.mode))
    throw new Error("security.mode must be one of: auto, open, allowlist");
  if (!(config.security.allowEval === "auto" || typeof config.security.allowEval === "boolean"))
    throw new Error("security.allowEval must be 'auto' or boolean");

  if (!Array.isArray(config.security.allowedActions))
    throw new Error("security.allowedActions must be an array");

  for (const [idx, action] of config.security.allowedActions.entries()) {
    if (typeof action !== "string" || action.trim().length === 0)
      throw new Error(`security.allowedActions[${idx}] must be a non-empty string`);
  }
}
