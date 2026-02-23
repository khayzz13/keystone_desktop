// store — Namespaced key-value store backed by Bun's native SQLite
// Survives hot-reloads. Each service gets its own namespace.
// DB path is resolved relative to the app root (passed via APP_ROOT env or fallback).

import { Database } from "bun:sqlite";
import { join } from "path";
import { mkdirSync, existsSync } from "fs";

const dataDir = join(process.env.KEYSTONE_APP_ROOT || import.meta.dir, "..", "data");
if (!existsSync(dataDir)) mkdirSync(dataDir, { recursive: true });

const db = new Database(join(dataDir, "services.db"));

db.run(`CREATE TABLE IF NOT EXISTS kv (
  ns TEXT NOT NULL,
  key TEXT NOT NULL,
  val TEXT NOT NULL,
  ts INTEGER NOT NULL DEFAULT (unixepoch()),
  PRIMARY KEY (ns, key)
)`);

const _get = db.prepare("SELECT val FROM kv WHERE ns = ? AND key = ?");
const _set = db.prepare("INSERT OR REPLACE INTO kv (ns, key, val, ts) VALUES (?, ?, ?, unixepoch())");
const _del = db.prepare("DELETE FROM kv WHERE ns = ? AND key = ?");
const _clear = db.prepare("DELETE FROM kv WHERE ns = ?");
const _keys = db.prepare("SELECT key FROM kv WHERE ns = ?");

export function store(namespace: string) {
  return {
    get<T = any>(key: string): T | null {
      const row = _get.get(namespace, key) as { val: string } | null;
      if (!row) return null;
      try { return JSON.parse(row.val); }
      catch { return null; }
    },

    set(key: string, val: any): void {
      _set.run(namespace, key, JSON.stringify(val));
    },

    del(key: string): void {
      _del.run(namespace, key);
    },

    clear(): void {
      _clear.run(namespace);
    },

    keys(): string[] {
      return (_keys.all(namespace) as { key: string }[]).map(r => r.key);
    },
  };
}
