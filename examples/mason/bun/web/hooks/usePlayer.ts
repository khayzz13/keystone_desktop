import { useState, useRef, useCallback, useEffect } from "react";
import { keystone } from "@keystone/sdk/bridge";
import { useActions } from "../lib/bridge";
import { canPlayNatively, isVideo, extOf } from "../lib/formats";
import { useTranscode } from "./useTranscode";
import type { MediaFile } from "./useLibrary";

export type PlayerState = {
  currentTrack: MediaFile | null;
  queue: MediaFile[];
  playing: boolean;
  currentTime: number;
  duration: number;
  volume: number;
  isVideo: boolean;
  transcoding: boolean;
  transcodeProgress: number;
};

export type PlayerControls = {
  play: (file: MediaFile, queue?: MediaFile[]) => void;
  pause: () => void;
  resume: () => void;
  togglePlay: () => void;
  next: () => void;
  prev: () => void;
  seek: (time: number) => void;
  setVolume: (v: number) => void;
  enqueue: (file: MediaFile) => void;
  audioRef: React.RefObject<HTMLAudioElement | null>;
  videoRef: React.RefObject<HTMLVideoElement | null>;
};

export function usePlayer(): PlayerState & PlayerControls {
  const [currentTrack, setCurrentTrack] = useState<MediaFile | null>(null);
  const [queue, setQueue] = useState<MediaFile[]>([]);
  const [queueIndex, setQueueIndex] = useState(0);
  const [playing, setPlaying] = useState(false);
  const [currentTime, setCurrentTime] = useState(0);
  const [duration, setDuration] = useState(0);
  const [volume, setVolumeState] = useState(1);

  const audioRef = useRef<HTMLAudioElement | null>(null);
  const videoRef = useRef<HTMLVideoElement | null>(null);

  const ext = currentTrack ? extOf(currentTrack.path) : "";
  const mediaIsVideo = currentTrack ? isVideo(ext) : false;
  const needsTranscode = currentTrack ? !canPlayNatively(ext) : false;

  const { transcoding, transcodeProgress: transcodeProgressRaw, outputUrl, reset: resetTranscode } = useTranscode(
    needsTranscode ? currentTrack?.path ?? null : null
  );
  const transcodeProgress = transcoding ? transcodeProgressRaw : 0;

  // The src to feed to the media element
  const effectiveSrc = currentTrack
    ? needsTranscode
      ? outputUrl ?? null  // wait until transcode completes
      : `/api/file?path=${encodeURIComponent(currentTrack.path)}`
    : null;

  const activeEl = useCallback((): HTMLMediaElement | null => {
    return mediaIsVideo ? videoRef.current : audioRef.current;
  }, [mediaIsVideo]);

  // Apply src when it changes
  useEffect(() => {
    const el = activeEl();
    if (!el) return;
    if (effectiveSrc && el.src !== effectiveSrc) {
      el.src = effectiveSrc;
      if (playing) el.play().catch(() => {});
    }
  }, [effectiveSrc]);

  // Sync playback state to pop-out player window via publish
  useEffect(() => {
    keystone().publish("playback:state", {
      currentTrack,
      playing,
      currentTime,
      duration,
      volume,
    });
  }, [currentTrack, playing, currentTime, duration, volume]);

  const play = useCallback((file: MediaFile, newQueue?: MediaFile[]) => {
    resetTranscode();
    setCurrentTrack(file);
    if (newQueue) {
      setQueue(newQueue);
      setQueueIndex(newQueue.indexOf(file));
    }
    setPlaying(true);
  }, [resetTranscode]);

  const pause = useCallback(() => {
    activeEl()?.pause();
    setPlaying(false);
  }, [activeEl]);

  const resume = useCallback(() => {
    activeEl()?.play().catch(() => {});
    setPlaying(true);
  }, [activeEl]);

  const togglePlay = useCallback(() => {
    playing ? pause() : resume();
  }, [playing, pause, resume]);

  const next = useCallback(() => {
    const nextIdx = queueIndex + 1;
    if (nextIdx < queue.length) {
      setQueueIndex(nextIdx);
      play(queue[nextIdx]);
    }
  }, [queue, queueIndex, play]);

  const prev = useCallback(() => {
    const el = activeEl();
    if (el && el.currentTime > 3) {
      el.currentTime = 0;
      return;
    }
    const prevIdx = queueIndex - 1;
    if (prevIdx >= 0) {
      setQueueIndex(prevIdx);
      play(queue[prevIdx]);
    }
  }, [queue, queueIndex, play, activeEl]);

  const seek = useCallback((time: number) => {
    const el = activeEl();
    if (el) el.currentTime = time;
  }, [activeEl]);

  const setVolume = useCallback((v: number) => {
    setVolumeState(v);
    const el = activeEl();
    if (el) el.volume = v;
  }, [activeEl]);

  const enqueue = useCallback((file: MediaFile) => {
    setQueue(q => [...q, file]);
  }, []);

  // Media element event wiring
  useEffect(() => {
    const els = [audioRef.current, videoRef.current].filter(Boolean) as HTMLMediaElement[];
    const onTimeUpdate = (e: Event) => setCurrentTime((e.target as HTMLMediaElement).currentTime);
    const onDuration = (e: Event) => setDuration((e.target as HTMLMediaElement).duration);
    const onEnded = () => next();

    for (const el of els) {
      el.addEventListener("timeupdate", onTimeUpdate);
      el.addEventListener("durationchange", onDuration);
      el.addEventListener("ended", onEnded);
    }
    return () => {
      for (const el of els) {
        el.removeEventListener("timeupdate", onTimeUpdate);
        el.removeEventListener("durationchange", onDuration);
        el.removeEventListener("ended", onEnded);
      }
    };
  }, [next]);

  // Handle menu actions
  useActions((action) => {
    if (action === "playback:toggle") togglePlay();
    else if (action === "playback:next") next();
    else if (action === "playback:prev") prev();
  });

  return {
    currentTrack, queue, playing, currentTime, duration, volume,
    isVideo: mediaIsVideo, transcoding, transcodeProgress,
    play, pause, resume, togglePlay, next, prev, seek, setVolume, enqueue,
    audioRef, videoRef,
  };
}
