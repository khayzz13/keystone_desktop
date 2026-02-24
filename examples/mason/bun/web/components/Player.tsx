import React from "react";
import type { PlayerControls } from "../hooks/usePlayer";

type Props = Pick<PlayerControls, "audioRef" | "videoRef"> & {
  showVideo: boolean;
};

// Hidden media elements — controlled entirely via refs from usePlayer.
// Rendered once at the app root so they persist across view changes.
export function Player({ audioRef, videoRef, showVideo }: Props) {
  return (
    <>
      <audio ref={audioRef} style={{ display: "none" }} preload="auto" />
      <video
        ref={videoRef}
        style={{ display: showVideo ? "block" : "none", width: "100%", height: "100%", objectFit: "contain", background: "#000" }}
        preload="auto"
      />
    </>
  );
}
