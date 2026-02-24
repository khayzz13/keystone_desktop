// formats.ts — Browser-native format detection.
// Native formats play directly via <audio>/<video> with no transcoding needed.
// Everything else goes through the hw-transcode (AVFoundation) or ffmpeg worker path.

export const NATIVE_AUDIO_EXTS = new Set([".mp3", ".aac", ".m4a", ".wav", ".ogg", ".opus", ".flac"]);
export const NATIVE_VIDEO_EXTS = new Set([".mp4", ".webm", ".mov"]);

// All formats Mason can play (natively or via transcode)
export const SUPPORTED_EXTS = new Set([
  // Native audio
  ".mp3", ".aac", ".m4a", ".wav", ".ogg", ".opus", ".flac",
  // Native video
  ".mp4", ".webm", ".mov",
  // Transcode-only audio
  ".wma", ".ape", ".mka", ".dsf",
  // Transcode-only video
  ".mkv", ".avi", ".wmv", ".flv", ".ts", ".m2ts", ".mpeg", ".mpg",
  ".3gp", ".rmvb", ".divx",
]);

export function canPlayNatively(ext: string): boolean {
  const e = ext.toLowerCase();
  return NATIVE_AUDIO_EXTS.has(e) || NATIVE_VIDEO_EXTS.has(e);
}

export function isAudio(ext: string): boolean {
  const e = ext.toLowerCase();
  return NATIVE_AUDIO_EXTS.has(e) || [".wma", ".ape", ".mka", ".dsf"].includes(e);
}

export function isVideo(ext: string): boolean {
  return !isAudio(ext) && SUPPORTED_EXTS.has(ext.toLowerCase());
}

export function extOf(path: string): string {
  const i = path.lastIndexOf(".");
  return i >= 0 ? path.slice(i).toLowerCase() : "";
}
