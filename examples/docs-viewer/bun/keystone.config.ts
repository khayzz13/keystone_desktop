import { defineConfig } from "@keystone/sdk/config";

export default defineConfig({
  web: {
    dir: "web",
    autoBundle: true,
    hotReload: true,
  },
});
