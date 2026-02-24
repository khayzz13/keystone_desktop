// utils.ts — Display formatting helpers.

export function formatDuration(seconds: number): string {
  if (!isFinite(seconds) || seconds < 0) return "--:--";
  const h = Math.floor(seconds / 3600);
  const m = Math.floor((seconds % 3600) / 60);
  const s = Math.floor(seconds % 60);
  if (h > 0) return `${h}:${String(m).padStart(2, "0")}:${String(s).padStart(2, "0")}`;
  return `${m}:${String(s).padStart(2, "0")}`;
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
  return `${(bytes / (1024 * 1024 * 1024)).toFixed(2)} GB`;
}

export function codecDisplayName(codec: string): string {
  const map: Record<string, string> = {
    h264: "H.264", avc1: "H.264",
    h265: "H.265", hevc: "H.265",
    vp9: "VP9", vp8: "VP8",
    av1: "AV1",
    mp3: "MP3", mp2a: "MP3",
    aac: "AAC",
    flac: "FLAC",
    opus: "Opus",
    vorbis: "Vorbis",
    pcm_s16le: "PCM", pcm_s24le: "PCM",
    alac: "ALAC",
    wmv3: "WMV", mpeg4: "MPEG-4",
    mpeg2video: "MPEG-2",
    theora: "Theora",
  };
  return map[codec?.toLowerCase()] ?? codec?.toUpperCase() ?? "—";
}

export function basename(path: string): string {
  return path.split(/[\\/]/).pop() ?? path;
}

export function dirname(path: string): string {
  const parts = path.split(/[\\/]/);
  parts.pop();
  return parts.join("/") || "/";
}
