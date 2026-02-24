import React, { useState, useMemo } from "react";
import { createRoot } from "react-dom/client";
import { Sidebar } from "./components/Sidebar";
import { Toolbar, type ViewMode, type SortKey } from "./components/Toolbar";
import { MediaGrid } from "./components/MediaGrid";
import { MediaList } from "./components/MediaList";
import { NowPlaying } from "./components/NowPlaying";
import { PlayerBar } from "./components/PlayerBar";
import { Player } from "./components/Player";
import { useLibrary } from "./hooks/useLibrary";
import { usePlayer } from "./hooks/usePlayer";
import { useAction } from "./lib/bridge";
import { isAudio, isVideo } from "./lib/formats";
import type { MediaFile } from "./hooks/useLibrary";
import "./styles.css";

type Filter = "all" | "audio" | "video";

function App() {
  const library = useLibrary();
  const player = usePlayer();

  const [filter, setFilter] = useState<Filter>("all");
  const [viewMode, setViewMode] = useState<ViewMode>("grid");
  const [sortKey, setSortKey] = useState<SortKey>("name");
  const [showNowPlaying, setShowNowPlaying] = useState(false);

  // Handle "Add Folder" menu action
  useAction("library:addFolder", library.addFolder);
  useAction("player:popout", () => {
    import("@keystone/sdk/bridge").then(({ keystone }) => keystone().invoke("window:open", { type: "player" }));
  });

  const filteredFiles = useMemo(() => {
    let files = library.files;
    if (filter === "audio") files = files.filter(f => isAudio(f.ext));
    if (filter === "video") files = files.filter(f => isVideo(f.ext));

    return [...files].sort((a, b) => {
      if (sortKey === "name") return a.name.localeCompare(b.name);
      if (sortKey === "size") return b.size - a.size;
      if (sortKey === "ext")  return a.ext.localeCompare(b.ext);
      return 0;
    });
  }, [library.files, filter, sortKey]);

  const handlePlay = (file: MediaFile) => {
    player.play(file, filteredFiles);
    setShowNowPlaying(true);
  };

  const mainContent = showNowPlaying && player.currentTrack
    ? <NowPlaying {...player} onBack={() => setShowNowPlaying(false)} />
    : (
      <>
        <Toolbar
          search={library.search}
          onSearch={library.setSearch}
          viewMode={viewMode}
          onViewMode={setViewMode}
          sortKey={sortKey}
          onSortKey={setSortKey}
        />
        {viewMode === "grid"
          ? <MediaGrid files={filteredFiles} currentPath={player.currentTrack?.path ?? null} onPlay={handlePlay} />
          : <MediaList files={filteredFiles} currentPath={player.currentTrack?.path ?? null} sortKey={sortKey} onSortKey={setSortKey} onPlay={handlePlay} />
        }
      </>
    );

  return (
    <div className="app-layout">
      <Sidebar library={library} filter={filter} onFilter={setFilter} />

      <main className="app-main">
        {mainContent}
      </main>

      <PlayerBar
        {...player}
        onExpand={() => player.currentTrack && setShowNowPlaying(true)}
      />

      {/* Hidden audio element always mounted */}
      {!showNowPlaying && <Player audioRef={player.audioRef} videoRef={player.videoRef} showVideo={false} />}
    </div>
  );
}

export function mount(root: HTMLElement) {
  const reactRoot = createRoot(root);
  reactRoot.render(<App />);
  return () => reactRoot.unmount();
}

export function unmount(root: HTMLElement) {
  root.innerHTML = "";
}
