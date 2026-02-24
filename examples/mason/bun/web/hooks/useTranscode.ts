import { useState, useEffect, useRef, useCallback } from "react";
import { keystone } from "@keystone/sdk/bridge";
import { canPlayNatively, extOf } from "../lib/formats";

export type TranscodeState = {
  transcoding: boolean;
  progress: number; // 0–1
  outputUrl: string | null;
  error: string | null;
};

export function useTranscode(filePath: string | null): TranscodeState & { reset: () => void } {
  const [state, setState] = useState<TranscodeState>({
    transcoding: false,
    progress: 0,
    outputUrl: null,
    error: null,
  });
  const cancelRef = useRef<(() => void) | null>(null);

  const reset = useCallback(() => {
    cancelRef.current?.();
    cancelRef.current = null;
    setState({ transcoding: false, progress: 0, outputUrl: null, error: null });
  }, []);

  useEffect(() => {
    if (!filePath) { reset(); return; }

    const ext = extOf(filePath);
    if (canPlayNatively(ext)) {
      // No transcoding needed — serve file directly via Bun HTTP
      setState({ transcoding: false, progress: 0, outputUrl: null, error: null });
      return;
    }

    let cancelled = false;
    setState({ transcoding: true, progress: 0, outputUrl: null, error: null });

    const ks = keystone();
    const sessionId = `${Date.now()}-${Math.random().toString(36).slice(2)}`;

    // Subscribe to progress updates for this session
    const unsubProgress = ks.subscribe(`transcode:progress:${sessionId}`, (data: { progress: number }) => {
      if (!cancelled) setState(s => ({ ...s, progress: data.progress }));
    });

    const unsubReady = ks.subscribe(`transcode:ready:${sessionId}`, (data: { url: string }) => {
      if (!cancelled) setState({ transcoding: false, progress: 1, outputUrl: data.url, error: null });
    });

    cancelRef.current = () => {
      cancelled = true;
      unsubProgress();
      unsubReady();
    };

    // Try AVFoundation (C# hardware path) first, fall back to ffmpeg worker
    (async () => {
      try {
        const res = await fetch("/api/hw-transcode/convert", {
          method: "POST",
          body: JSON.stringify({ input: filePath, sessionId }),
        });
        if (!res.ok) throw new Error(await res.text());
      } catch {
        // Hardware path failed — fall back to ffmpeg worker
        if (cancelled) return;
        try {
          await ks.invoke("transcoder:convert", { input: filePath, sessionId });
        } catch (e: any) {
          if (!cancelled) setState({ transcoding: false, progress: 0, outputUrl: null, error: e.message });
        }
      }
    })();

    return () => {
      cancelRef.current?.();
      cancelRef.current = null;
    };
  }, [filePath]);

  return { ...state, reset };
}
