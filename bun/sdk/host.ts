// @keystone/sdk/host — App host module authoring SDK for Keystone Desktop.
// Export a defineHost() result as the default export from your app's bun/host.ts
// to hook into the framework's Bun subprocess lifecycle.
//
// All hooks are optional — only export what you need.
//
// Electron equivalent:
//   onBeforeStart  → register ipcMain handlers before createWindow()
//   onReady        → app.whenReady().then(createWindow)
//   onShutdown     → app.on("before-quit", ...)
//   onAction       → ipcMain.on("action", ...)

import type { AppHostModule, HostContext, HostHook } from "../types";
export type { AppHostModule, HostContext, HostHook };

/** Define your app's host module. All hooks are optional. Returns the module unchanged (type helper). */
export function defineHost(module: AppHostModule): AppHostModule {
  return module;
}
