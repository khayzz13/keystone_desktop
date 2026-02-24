import React from "react";
import type { MediaFile } from "../hooks/useLibrary";
import type { PlayerState, PlayerControls } from "../hooks/usePlayer";
import { formatDuration } from "../lib/utils";

type Props = {
  queue: PlayerState["queue"];
  currentTrack: PlayerState["currentTrack"];
  onJump: (file: MediaFile) => void;
};

export function QueuePanel({ queue, currentTrack, onJump }: Props) {
  if (!queue.length) return null;

  return (
    <div className="queue-panel">
      <div className="queue-header">Up Next — {queue.length} tracks</div>
      <div className="queue-list">
        {queue.map((file, i) => (
          <div
            key={`${file.path}-${i}`}
            className={`queue-item ${file.path === currentTrack?.path ? "active" : ""}`}
            onClick={() => onJump(file)}
          >
            <span className="queue-item-idx">{i + 1}</span>
            <span className="queue-item-name">{file.name}</span>
          </div>
        ))}
      </div>
    </div>
  );
}
