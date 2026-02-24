import React, { useState, useEffect, useRef } from "react";
import { createRoot } from "react-dom/client";
import { keystone } from "@keystone/sdk/bridge";
import { formatDuration } from "./lib/utils";
import "./styles.css";

type PlaybackState = {
  currentTrack: { name: string; path: string; ext: string } | null;
  playing: boolean;
  currentTime: number;
  duration: number;
  volume: number;
};

// The pop-out player window. Minimal — just viewport + scrubber.
// Receives playback state from the main window via the "playback:state" channel
// and controls playback by publishing back to a shared channel.
function PlayerWindow() {
  const [state, setState] = useState<PlaybackState>({
    currentTrack: null,
    playing: false,
    currentTime: 0,
    duration: 0,
    volume: 1,
  });

  const videoRef = useRef<HTMLVideoElement | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);

  // Subscribe to playback state from the main window
  useEffect(() => {
    const ks = keystone();
    const windowId = ks.windowId;
    // Window-targeted channel so Bun can push to this window specifically
    return ks.subscribe(`window:${windowId}:playback:state`, (data: PlaybackState) => {
      setState(data);
    });
  }, []);

  // Apply state to media elements
  useEffect(() => {
    const isVid = state.currentTrack && ![".mp3", ".aac", ".m4a", ".wav", ".ogg", ".flac"].includes(state.currentTrack.ext);
    const el = isVid ? videoRef.current : audioRef.current;
    if (!el) return;

    const src = state.currentTrack
      ? `/api/file?path=${encodeURIComponent(state.currentTrack.path)}`
      : "";

    if (el.src !== src) el.src = src;

    if (state.playing && el.paused) el.play().catch(() => {});
    else if (!state.playing && !el.paused) el.pause();

    if (Math.abs(el.currentTime - state.currentTime) > 1.5) el.currentTime = state.currentTime;
    el.volume = state.volume;
  }, [state]);

  const seek = (time: number) => {
    keystone().publish("playback:seek", { time });
  };

  const toggle = () => {
    keystone().publish("playback:toggle-from-popout", {});
  };

  const isVideo = state.currentTrack && ![".mp3", ".aac", ".m4a", ".wav", ".ogg", ".flac"].includes(state.currentTrack.ext ?? "");

  return (
    <div className="player-window">
      <audio ref={audioRef} style={{ display: "none" }} />
      <video ref={videoRef} style={{ display: isVideo ? "block" : "none", flex: 1, width: "100%", objectFit: "contain" }} />

      {!isVideo && state.currentTrack && (
        <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", fontSize: 64, color: "var(--ks-text-muted)" }}>
          🎵
        </div>
      )}

      {!state.currentTrack && (
        <div style={{ flex: 1, display: "flex", alignItems: "center", justifyContent: "center", color: "var(--ks-text-muted)", fontSize: 14 }}>
          Nothing playing
        </div>
      )}

      <div className="player-window-bar">
        {state.currentTrack && (
          <div style={{ fontSize: 13, fontWeight: 500, color: "var(--ks-text-primary)", marginBottom: 4 }}>
            {state.currentTrack.name}
          </div>
        )}
        <div className="seek-row">
          <span className="seek-time">{formatDuration(state.currentTime)}</span>
          <input
            type="range"
            className="slider"
            min={0}
            max={state.duration || 1}
            step={0.5}
            value={state.currentTime}
            onChange={e => seek(Number(e.target.value))}
          />
          <span className="seek-time right">{formatDuration(state.duration)}</span>
          <button
            onClick={toggle}
            style={{ background: "none", border: "none", color: "var(--ks-text-primary)", fontSize: 18, cursor: "pointer", padding: "0 4px" }}
          >
            {state.playing ? "⏸" : "▶"}
          </button>
        </div>
      </div>
    </div>
  );
}

export function mount(root: HTMLElement) {
  const reactRoot = createRoot(root);
  reactRoot.render(<PlayerWindow />);
  return () => reactRoot.unmount();
}

export function unmount(root: HTMLElement) {
  root.innerHTML = "";
}
