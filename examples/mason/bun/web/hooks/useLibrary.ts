import { useState, useEffect, useCallback } from "react";
import { keystone } from "@keystone/sdk/bridge";
import { useSubscribe } from "../lib/bridge";

export type MediaFile = {
  path: string;
  name: string;
  ext: string;
  size: number;
};

export type LibraryState = {
  files: MediaFile[];
  folders: string[];
  loading: boolean;
  search: string;
  setSearch: (q: string) => void;
  addFolder: () => Promise<void>;
  removeFolder: (path: string) => Promise<void>;
  refresh: () => Promise<void>;
};

export function useLibrary(): LibraryState {
  const [files, setFiles] = useState<MediaFile[]>([]);
  const [folders, setFolders] = useState<string[]>([]);
  const [loading, setLoading] = useState(false);
  const [search, setSearch] = useState("");

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const ks = keystone();
      const [allFiles, allFolders] = await Promise.all([
        ks.invoke<MediaFile[]>("library:getAll"),
        ks.invoke<string[]>("library:getFolders"),
      ]);
      setFiles(allFiles ?? []);
      setFolders(allFolders ?? []);
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { load(); }, [load]);

  // Re-load when library scan completes
  useSubscribe("library:updated", load);

  const addFolder = useCallback(async () => {
    const ks = keystone();
    const paths = await ks.invoke<string[] | null>("dialog:openFile", {
      title: "Add Folder to Library",
      directory: true,
      multiple: false,
    });
    if (!paths?.length) return;
    await ks.invoke("library:addFolder", { path: paths[0] });
  }, []);

  const removeFolder = useCallback(async (path: string) => {
    await keystone().invoke("library:removeFolder", { path });
  }, []);

  const filtered = search
    ? files.filter(f => f.name.toLowerCase().includes(search.toLowerCase()))
    : files;

  return { files: filtered, folders, loading, search, setSearch, addFolder, removeFolder, refresh: load };
}
