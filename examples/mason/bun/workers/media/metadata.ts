import { defineService } from "@keystone/sdk/service";

type TrackMetadata = {
  duration: number;
  codec: string;
  bitrate: number;
  sampleRate?: number;
  title?: string;
  artist?: string;
  album?: string;
  artworkDataUrl?: string;
};

async function probe(path: string): Promise<TrackMetadata> {
  const proc = Bun.spawn(
    ["ffprobe", "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path],
    { stdout: "pipe", stderr: "pipe" }
  );
  const raw = await new Response(proc.stdout).text();
  await proc.exited;

  const json = JSON.parse(raw);
  const fmt = json.format ?? {};
  const streams: any[] = json.streams ?? [];
  const audio = streams.find(s => s.codec_type === "audio") ?? {};

  const tags: Record<string, string> = {
    ...(fmt.tags ?? {}),
    ...(audio.tags ?? {}),
  };
  const normalizedTags: Record<string, string> = {};
  for (const [k, v] of Object.entries(tags)) normalizedTags[k.toLowerCase()] = v;

  return {
    duration: parseFloat(fmt.duration ?? "0"),
    codec: audio.codec_name ?? "",
    bitrate: parseInt(fmt.bit_rate ?? "0"),
    sampleRate: audio.sample_rate ? parseInt(audio.sample_rate) : undefined,
    title: normalizedTags.title,
    artist: normalizedTags.artist ?? normalizedTags.album_artist,
    album: normalizedTags.album,
  };
}

export default defineService("metadata")
  .handle("metadata:get", async (args: { path: string }, svc) => {
    const cacheKey = `meta:${args.path}`;
    const cached = svc.store.get(cacheKey);
    if (cached) return JSON.parse(cached as string);

    const result = await probe(args.path);
    svc.store.set(cacheKey, JSON.stringify(result));
    return result;
  })

  .handle("metadata:batch", async (args: { paths: string[] }, svc) => {
    const results: Record<string, TrackMetadata> = {};
    await Promise.all(args.paths.map(async path => {
      try {
        const cacheKey = `meta:${path}`;
        const cached = svc.store.get(cacheKey);
        const meta = cached ? JSON.parse(cached as string) : await probe(path);
        if (!cached) svc.store.set(cacheKey, JSON.stringify(meta));
        results[path] = meta;
        svc.push("metadata:result", { path, meta });
      } catch { /* skip */ }
    }));
    return results;
  })

  .health(svc => {
    // Count cached entries by iterating the store keys
    return { ok: true };
  })

  .build(async () => {
    console.log("[metadata] ready");
  });
