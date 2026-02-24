import React from "react";
import type { PlayerState, PlayerControls } from "../hooks/usePlayer";
import { formatDuration } from "../lib/utils";
import { TranscodeIndicator } from "./TranscodeIndicator";
import { keystone } from "@keystone/sdk/bridge";

type Props = PlayerState & PlayerControls & {
  onExpand: () => void;
};

export function PlayerBar(props: Props) {
  const {
    currentTrack, playing, currentTime, duration, volume,
    transcoding, transcodeProgress,
    togglePlay, next, prev, seek, setVolume,
    onExpand,
  } = props;

  const handlePopOut = async () => {
    await keystone().invoke("window:open", { type: "player" });
  };

  return (
    <div className="player-bar app-playerbar">
      {/* Track info */}
      <div className="player-bar-track" style={{ cursor: "pointer" }} onClick={onExpand}>
        <div className="player-bar-art">
          {currentTrack ? "🎵" : "♬"}
        </div>
        <div>
          <div className="player-bar-name">
            {currentTrack ? currentTrack.name : "Nothing playing"}
          </div>
          {transcoding && (
            <div style={{ marginTop: 2 }}>
              <TranscodeIndicator transcoding={transcoding} progress={transcodeProgress} />
            </div>
          )}
        </div>
      </div>

      {/* Controls + seek */}
      <div className="player-controls">
        <div className="player-controls-btns">
          <button className="icon-btn" onClick={prev} title="Previous" disabled={!currentTrack}>⏮</button>
          <button className="play-btn" onClick={togglePlay} title={playing ? "Pause" : "Play"}>
            {playing ? "⏸" : "▶"}
          </button>
          <button className="icon-btn" onClick={next} title="Next" disabled={!currentTrack}>⏭</button>
        </div>

        <div className="seek-row">
          <span className="seek-time">{formatDuration(currentTime)}</span>
          <input
            type="range"
            className="slider"
            min={0}
            max={duration || 1}
            step={0.5}
            value={currentTime}
            onChange={e => seek(Number(e.target.value))}
            disabled={!currentTrack}
          />
          <span className="seek-time right">{formatDuration(duration)}</span>
        </div>
      </div>

      {/* Volume + pop-out */}
      <div className="volume-group">
        <span style={{ fontSize: 13, color: "var(--ks-text-muted)" }}>🔈</span>
        <input
          type="range"
          className="slider"
          min={0}
          max={1}
          step={0.02}
          value={volume}
          onChange={e => setVolume(Number(e.target.value))}
        />
        <button className="popout-btn" onClick={handlePopOut} title="Pop out player">⧉</button>
      </div>
    </div>
  );
}
