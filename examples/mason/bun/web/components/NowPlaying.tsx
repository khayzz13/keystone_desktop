import React from "react";
import type { PlayerState, PlayerControls } from "../hooks/usePlayer";
import { keystone } from "@keystone/sdk/bridge";
import { Player } from "./Player";

type Props = PlayerState & PlayerControls & {
  onBack: () => void;
};

export function NowPlaying(props: Props) {
  const { currentTrack, isVideo, audioRef, videoRef, onBack } = props;

  const handlePopOut = async () => {
    await keystone().invoke("window:open", { type: "player" });
  };

  return (
    <div className="now-playing">
      <button className="now-playing-back" onClick={onBack}>← Library</button>

      {isVideo ? (
        <Player audioRef={audioRef} videoRef={videoRef} showVideo={true} />
      ) : (
        <>
          <div className="now-playing-art">
            <span>🎵</span>
          </div>
          <div>
            <div className="now-playing-title">{currentTrack?.name ?? "—"}</div>
            <div className="now-playing-artist" style={{ marginTop: 4 }}>
              {currentTrack?.ext.replace(".", "").toUpperCase() ?? ""}
            </div>
          </div>
        </>
      )}

      <button
        onClick={handlePopOut}
        style={{
          position: "absolute", top: 12, right: 12,
          background: "none", border: "none",
          color: "var(--ks-text-muted)", fontSize: 14,
          cursor: "pointer", padding: "4px 8px",
          borderRadius: 6,
        }}
        title="Pop out player"
      >
        ⧉ Pop out
      </button>
    </div>
  );
}
