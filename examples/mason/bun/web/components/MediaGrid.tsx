import React, { useEffect, useState } from "react";
import { keystone } from "@keystone/sdk/bridge";
import type { MediaFile } from "../hooks/useLibrary";
import { isVideo, canPlayNatively } from "../lib/formats";
import { codecDisplayName } from "../lib/utils";

type Props = {
  files: MediaFile[];
  currentPath: string | null;
  onPlay: (file: MediaFile) => void;
};

function Thumbnail({ file }: { file: MediaFile }) {
  const [src, setSrc] = useState<string | null>(null);

  useEffect(() => {
    if (!isVideo(file.ext)) return;
    keystone().invoke<{ dataUrl: string }>("thumbnails:get", { path: file.path })
      .then(r => { if (r?.dataUrl) setSrc(r.dataUrl); })
      .catch(() => {});
  }, [file.path]);

  if (src) return <img src={src} alt={file.name} />;
  return <span>{isVideo(file.ext) ? "🎬" : "🎵"}</span>;
}

export function MediaGrid({ files, currentPath, onPlay }: Props) {
  if (!files.length) {
    return (
      <div className="empty-state">
        <div className="empty-state-icon">♬</div>
        <div className="empty-state-title">No media found</div>
        <div className="empty-state-sub">Add a folder from the sidebar to get started.</div>
      </div>
    );
  }

  return (
    <div className="media-scroll">
      <div className="media-grid">
        {files.map(file => (
          <div
            key={file.path}
            className={`media-card ${file.path === currentPath ? "active" : ""}`}
            onClick={() => onPlay(file)}
            title={file.path}
          >
            <div className="media-card-thumb">
              <Thumbnail file={file} />
            </div>
            <div className="media-card-info">
              <div className="media-card-name">{file.name}</div>
              <div className="media-card-meta">
                <span className={`codec-badge ${canPlayNatively(file.ext) ? "native" : ""}`}>
                  {codecDisplayName(file.ext.replace(".", ""))}
                </span>
              </div>
            </div>
          </div>
        ))}
      </div>
    </div>
  );
}
