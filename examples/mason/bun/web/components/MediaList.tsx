import React, { useState } from "react";
import type { MediaFile } from "../hooks/useLibrary";
import { canPlayNatively } from "../lib/formats";
import { formatSize, codecDisplayName } from "../lib/utils";
import type { SortKey } from "./Toolbar";

type Props = {
  files: MediaFile[];
  currentPath: string | null;
  sortKey: SortKey;
  onSortKey: (k: SortKey) => void;
  onPlay: (file: MediaFile) => void;
};

const COLS: { label: string; key: SortKey | null; className: string }[] = [
  { label: "Name",  key: "name", className: "col-name" },
  { label: "Type",  key: "ext",  className: "col-codec" },
  { label: "Size",  key: "size", className: "col-size" },
];

export function MediaList({ files, currentPath, sortKey, onSortKey, onPlay }: Props) {
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
      <table className="media-list">
        <thead>
          <tr>
            {COLS.map(col => (
              <th
                key={col.label}
                className={col.className}
                onClick={() => col.key && onSortKey(col.key)}
              >
                {col.label}{sortKey === col.key ? " ↓" : ""}
              </th>
            ))}
          </tr>
        </thead>
        <tbody>
          {files.map(file => (
            <tr
              key={file.path}
              className={file.path === currentPath ? "active" : ""}
              onClick={() => onPlay(file)}
              title={file.path}
            >
              <td className="col-name">{file.name}</td>
              <td className="col-codec">
                <span className={`codec-badge ${canPlayNatively(file.ext) ? "native" : ""}`}>
                  {codecDisplayName(file.ext.replace(".", ""))}
                </span>
              </td>
              <td className="col-size">{formatSize(file.size)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
