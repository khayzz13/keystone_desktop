import { defineService } from "@keystone/sdk/service";
import { stat } from "node:fs/promises";

async function extractFrame(path: string): Promise<string> {
  const proc = Bun.spawn([
    "ffmpeg", "-ss", "2", "-i", path,
    "-frames:v", "1",
    "-f", "image2pipe", "-vcodec", "mjpeg",
    "-vf", "scale=300:-1",
    "pipe:1",
  ], { stdout: "pipe", stderr: "pipe" });

  const buf = await new Response(proc.stdout).arrayBuffer();
  await proc.exited;

  if (!buf.byteLength) throw new Error("No frame extracted");
  const b64 = Buffer.from(buf).toString("base64");
  return `data:image/jpeg;base64,${b64}`;
}

export default defineService("thumbnails")
  .handle("thumbnails:get", async (args: { path: string }, svc) => {
    // Cache key includes mtime so stale thumbs are invalidated
    const info = await stat(args.path).catch(() => null);
    const mtime = info?.mtimeMs ?? 0;
    const cacheKey = `thumb:${args.path}:${mtime}`;

    const cached = svc.store.get(cacheKey);
    if (cached) return { dataUrl: cached as string };

    const dataUrl = await extractFrame(args.path);
    svc.store.set(cacheKey, dataUrl);
    return { dataUrl };
  })

  .health(() => ({ ok: true }))

  .build(async () => {
    console.log("[thumbnails] ready");
  });
