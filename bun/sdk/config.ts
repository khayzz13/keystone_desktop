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
     * Allowlist of action strings web components may dispatch via keystone().action().
     * If empty/undefined, all actions are permitted (open model).
     * Supports exact strings and prefix wildcards ending in "*" (e.g. "spawn:*").
     */
    allowedActions?: string[];
  };
};

/** Resolved config with all defaults applied */
export type ResolvedConfig = Required<{
  [K in keyof KeystoneRuntimeConfig]: Required<NonNullable<KeystoneRuntimeConfig[K]>>
}>;

const defaults: ResolvedConfig = {
  services: { dir: "services", hotReload: true },
  web: { dir: "web", autoBundle: true, hotReload: true, components: {} },
  http: { enabled: true, hostname: "127.0.0.1" },
  watch: { extensions: [".ts", ".tsx", ".js", ".jsx"], debounceMs: 150 },
  health: { enabled: true, intervalMs: 30_000 },
  security: { allowedActions: [] },
};

/** Define your app's Keystone Desktop configuration. */
export function defineConfig(config: KeystoneRuntimeConfig): KeystoneRuntimeConfig {
  return config;
}

/** Merge user config with defaults. Used internally by host.ts. */
export function resolveConfig(user: KeystoneRuntimeConfig): ResolvedConfig {
  return {
    services: { ...defaults.services, ...user.services },
    web: { ...defaults.web, ...user.web, components: { ...defaults.web.components, ...user.web?.components } },
    http: { ...defaults.http, ...user.http },
    watch: { ...defaults.watch, ...user.watch },
    health: { ...defaults.health, ...user.health },
    security: { ...defaults.security, ...user.security },
  };
}
