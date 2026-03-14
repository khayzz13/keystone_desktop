/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

import { defineConfig } from "@keystone/sdk/config";

export default defineConfig({
  services: {
    dir: "services",
    hotReload: true,
  },
  web: {
    dir: "web",
    autoBundle: true,
    hotReload: true,
    components: {
      app: "./web/app.tsx",
      player: "./web/player.tsx",
    },
  },
  health: {
    intervalMs: 10_000,
  },
  security: {
    allowedActions: [
      "library:addFolder",
      "playback:toggle",
      "playback:next",
      "playback:prev",
      "player:popout",
      "window:close",
    ],
  },
});
