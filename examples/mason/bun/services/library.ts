import { defineService } from "@keystone/sdk/service";
import { readdir, stat } from "node:fs/promises";
import { join, extname, basename } from "node:path";
import { keystone } from "@keystone/sdk/bridge";

const MEDIA_EXTS = new Set([
  ".mp3", ".m4a", ".flac", ".wav", ".aac", ".ogg", ".opus", ".wma", ".ape", ".mka",
  ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".flv", ".ts", ".m2ts", ".webm", ".3gp",
]);

type MediaFile = {
  path: string;
  name: string;
  ext: string;
  size: number;
};

async function scanDir(dir: string, results: MediaFile[] = []): Promise<MediaFile[]> {
  try {
    const entries = await readdir(dir, { withFileTypes: true });
    await Promise.all(entries.map(async entry => {
      const full = join(dir, entry.name);
      if (entry.isDirectory()) {
        await scanDir(full, results);
      } else if (entry.isFile()) {
        const ext = extname(entry.name).toLowerCase();
        if (MEDIA_EXTS.has(ext)) {
          const info = await stat(full).catch(() => null);
          results.push({ path: full, name: basename(full), ext, size: info?.size ?? 0 });
        }
      }
    }));
  } catch { /* unreadable dir */ }
  return results;
}

export default defineService("library")
  .handle("library:getAll", async (_args, svc) => {
    const raw = svc.store.get("files") as string | undefined;
    return raw ? (JSON.parse(raw) as MediaFile[]) : [];
  })

  .handle("library:getFolders", (_args, svc) => {
    const raw = svc.store.get("folders") as string | undefined;
    return raw ? (JSON.parse(raw) as string[]) : [];
  })

  .handle("library:addFolder", async (args: { path: string }, svc) => {
    const folders: string[] = JSON.parse(svc.store.get("folders") as string ?? "[]");
    if (!folders.includes(args.path)) {
      folders.push(args.path);
      svc.store.set("folders", JSON.stringify(folders));
    }
    // Scan and merge
    const newFiles = await scanDir(args.path);
    const existing: MediaFile[] = JSON.parse(svc.store.get("files") as string ?? "[]");
    const merged = [...existing.filter(f => !f.path.startsWith(args.path)), ...newFiles];
    svc.store.set("files", JSON.stringify(merged));
    svc.push("library:updated", { count: merged.length });
    return { ok: true, added: newFiles.length };
  })

  .handle("library:removeFolder", (args: { path: string }, svc) => {
    const folders: string[] = JSON.parse(svc.store.get("folders") as string ?? "[]");
    const next = folders.filter(f => f !== args.path);
    svc.store.set("folders", JSON.stringify(next));
    const files: MediaFile[] = JSON.parse(svc.store.get("files") as string ?? "[]");
    const pruned = files.filter(f => !f.path.startsWith(args.path));
    svc.store.set("files", JSON.stringify(pruned));
    svc.push("library:updated", { count: pruned.length });
    return { ok: true };
  })

  .handle("library:search", (args: { query: string }, svc) => {
    const files: MediaFile[] = JSON.parse(svc.store.get("files") as string ?? "[]");
    const q = (args.query ?? "").toLowerCase();
    return q ? files.filter(f => f.name.toLowerCase().includes(q)) : files;
  })

  .handle("library:scan", async (_args, svc) => {
    const folders: string[] = JSON.parse(svc.store.get("folders") as string ?? "[]");
    const all: MediaFile[] = [];
    for (const dir of folders) await scanDir(dir, all);
    svc.store.set("files", JSON.stringify(all));
    svc.push("library:updated", { count: all.length });
    return { ok: true, total: all.length };
  })

  .onAction((action, svc) => {
    // "library:addFolder" from menu bar — trigger native dialog from Bun side
    // (The dialog invoke goes browser→C#, so menu actions that need a dialog
    //  should be handled in the web layer via useAction("library:addFolder").
    //  Nothing needed here for now.)
  })

  .health(svc => {
    const files: MediaFile[] = JSON.parse(svc.store.get("files") as string ?? "[]");
    const folders: string[] = JSON.parse(svc.store.get("folders") as string ?? "[]");
    return { ok: true, fileCount: files.length, folderCount: folders.length };
  })

  .build(async svc => {
    // Provide /api/file route — serves a local file by path for the media player
    // (This is registered as a Bun HTTP route, not C# HttpRouter)
    // Note: actual file serving is done via Bun's static server in host.ts for
    // files outside the bun/ dir. We expose metadata here.
    console.log("[library] ready —", JSON.parse(svc.store.get("files") as string ?? "[]").length, "files indexed");
  });
