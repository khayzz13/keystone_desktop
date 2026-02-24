import React from "react";

export type ViewMode = "grid" | "list";
export type SortKey = "name" | "size" | "ext";

type Props = {
  search: string;
  onSearch: (q: string) => void;
  viewMode: ViewMode;
  onViewMode: (m: ViewMode) => void;
  sortKey: SortKey;
  onSortKey: (k: SortKey) => void;
};

export function Toolbar({ search, onSearch, viewMode, onViewMode, sortKey, onSortKey }: Props) {
  return (
    <div className="toolbar">
      <input
        className="toolbar-search"
        type="search"
        placeholder="Search library…"
        value={search}
        onChange={e => onSearch(e.target.value)}
      />

      <div className="toolbar-group">
        <button
          className={`icon-btn ${viewMode === "grid" ? "active" : ""}`}
          title="Grid view"
          onClick={() => onViewMode("grid")}
        >⊞</button>
        <button
          className={`icon-btn ${viewMode === "list" ? "active" : ""}`}
          title="List view"
          onClick={() => onViewMode("list")}
        >≡</button>
      </div>

      <select
        className="sort-select"
        value={sortKey}
        onChange={e => onSortKey(e.target.value as SortKey)}
      >
        <option value="name">Sort: Name</option>
        <option value="size">Sort: Size</option>
        <option value="ext">Sort: Type</option>
      </select>
    </div>
  );
}
