import { defineService } from "@keystone/sdk/service";
import { join } from "node:path";
import { tmpdir } from "node:os";
import { mkdir, rm } from "node:fs/promises";
import { existsSync } from "node:fs";

const TEMP_DIR = join(tmpdir(), "mason-transcode");
const activeJobs = new Map<string, ReturnType<typeof Bun.spawn>>();

async function ensureTempDir() {
  await mkdir(TEMP_DIR, { recursive: true });
}

function parseProgress(line: string): number | null {
  // ffmpeg progress lines: "out_time_ms=1234567"
  const match = line.match(/out_time_ms=(\d+)/);
  if (match) return parseInt(match[1]);
  return null;
}

export default defineService("transcoder")
  .handle("transcoder:convert", async (args: { input: string; sessionId: string }, svc) => {
    await ensureTempDir();
    const outPath = join(TEMP_DIR, `${args.sessionId}.mp4`);

    if (existsSync(outPath)) {
      // Already transcoded — serve from cache
      svc.push(`transcode:ready:${args.sessionId}`, {
        url: `/api/transcode-file?path=${encodeURIComponent(outPath)}`,
      });
      return { ok: true, cached: true };
    }

    // Get duration first for progress calculation
    const probeProc = Bun.spawn(
      ["ffprobe", "-v", "quiet", "-show_entries", "format=duration", "-of", "csv=p=0", args.input],
      { stdout: "pipe", stderr: "pipe" }
    );
    const durationStr = (await new Response(probeProc.stdout).text()).trim();
    const totalDuration = parseFloat(durationStr) || 1;
    await probeProc.exited;

    const proc = Bun.spawn([
      "ffmpeg", "-i", args.input,
      "-c:v", "libx264", "-preset", "fast", "-crf", "23",
      "-c:a", "aac", "-b:a", "192k",
      "-movflags", "+faststart",
      "-progress", "pipe:1",
      "-nostats",
      "-y", outPath,
    ], { stdout: "pipe", stderr: "pipe" });

    activeJobs.set(args.sessionId, proc);

    // Stream progress
    const reader = proc.stdout.getReader();
    const decoder = new TextDecoder();
    let buf = "";

    (async () => {
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;
        buf += decoder.decode(value, { stream: true });
        const lines = buf.split("\n");
        buf = lines.pop() ?? "";
        for (const line of lines) {
          const outTimeMs = parseProgress(line);
          if (outTimeMs !== null) {
            const progress = Math.min(outTimeMs / 1_000_000 / totalDuration, 0.99);
            svc.push(`transcode:progress:${args.sessionId}`, { progress });
          }
        }
      }
    })();

    const exitCode = await proc.exited;
    activeJobs.delete(args.sessionId);

    if (exitCode !== 0) {
      throw new Error("FFmpeg transcoding failed");
    }

    svc.push(`transcode:ready:${args.sessionId}`, {
      url: `/api/transcode-file?path=${encodeURIComponent(outPath)}`,
    });

    return { ok: true };
  })

  .handle("transcoder:cancel", (args: { sessionId: string }) => {
    const proc = activeJobs.get(args.sessionId);
    if (proc) { proc.kill(); activeJobs.delete(args.sessionId); }
    return { ok: true };
  })

  .handle("transcoder:canPlay", (args: { ext: string }) => {
    const nativeExts = new Set([".mp3", ".aac", ".m4a", ".wav", ".ogg", ".opus", ".mp4", ".webm", ".mov"]);
    return { native: nativeExts.has(args.ext.toLowerCase()) };
  })

  .health(() => {
    return { ok: true, activeJobs: activeJobs.size };
  })

  .onStop(async () => {
    for (const proc of activeJobs.values()) proc.kill();
    activeJobs.clear();
    await rm(TEMP_DIR, { recursive: true, force: true });
  })

  .build(async () => {
    await ensureTempDir();
    console.log("[transcoder] ready, temp dir:", TEMP_DIR);
  });
