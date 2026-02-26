// host.ts — {{APP_NAME}} Bun host
// Lifecycle extension point for the Keystone Bun runtime.
// All hooks are optional — delete what you don't need.
//
// Electron equivalent:
//   onBeforeStart → register ipcMain handlers before createWindow()
//   onReady       → app.whenReady()
//   onShutdown    → app.on("before-quit", ...)
//   onAction      → ipcMain.on("action", ...)

import { defineHost } from "@keystone/sdk/host";

export default defineHost({
  // Fires before service discovery.
  // Good for: registering services other services will call(), global invoke handlers.
  //
  // async onBeforeStart(ctx) {
  //   await ctx.registerService("db", myDbModule);
  //   ctx.registerInvokeHandler("app:getConfig", async () => loadConfig());
  // },

  // Fires after all services are started and the HTTP server is live.
  // Windows are opening on the C# side at this point.
  //
  // async onReady(ctx) {
  //   ctx.push("app:status", { ready: true });
  // },

  // Fires before services are stopped on shutdown.
  //
  // async onShutdown(ctx) {
  //   await flushPendingWrites();
  // },

  // Global action handler — receives every action from C# or web.
  // Runs alongside per-service onAction handlers.
  //
  // onAction(action, ctx) {
  //   if (action === "app:refresh") ctx.push("app:refresh", {});
  // },
});
