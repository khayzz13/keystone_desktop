import React from "react";
import type { LibraryState } from "../hooks/useLibrary";

type Filter = "all" | "audio" | "video";

type Props = {
  library: LibraryState;
  filter: Filter;
  onFilter: (f: Filter) => void;
};

const NAV_ITEMS: { label: string; icon: string; value: Filter }[] = [
  { label: "All Files", icon: "♬", value: "all" },
  { label: "Music",     icon: "🎵", value: "audio" },
  { label: "Videos",    icon: "🎬", value: "video" },
];

export function Sidebar({ library, filter, onFilter }: Props) {
  return (
    <aside className="sidebar app-sidebar">
      <div className="sidebar-section">
        <div className="sidebar-label">Library</div>
        {NAV_ITEMS.map(item => (
          <div
            key={item.value}
            className={`sidebar-item ${filter === item.value ? "active" : ""}`}
            onClick={() => onFilter(item.value)}
          >
            <span>{item.icon}</span>
            <span>{item.label}</span>
          </div>
        ))}
      </div>

      <div className="sidebar-divider" />

      <div className="sidebar-section" style={{ flex: 1, overflow: "hidden", display: "flex", flexDirection: "column" }}>
        <div className="sidebar-label">Folders</div>
        <div style={{ flex: 1, overflowY: "auto" }}>
          {library.folders.map(folder => (
            <div key={folder} className="sidebar-folder">
              <span style={{ fontSize: 11, flexShrink: 0 }}>📁</span>
              <span style={{ flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap", fontSize: 12 }}>
                {folder.split(/[\\/]/).pop()}
              </span>
              <button className="remove-btn" onClick={() => library.removeFolder(folder)} title="Remove folder">×</button>
            </div>
          ))}
        </div>
        <button className="sidebar-add-btn" onClick={library.addFolder}>
          <span>+</span>
          <span>Add Folder</span>
        </button>
      </div>
    </aside>
  );
}
