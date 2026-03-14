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
  },
});
