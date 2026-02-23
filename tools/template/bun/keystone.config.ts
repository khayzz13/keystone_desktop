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
