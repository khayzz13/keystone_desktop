// @keystone SDK entry point
// Browser: import { keystone } from "@keystone/sdk/bridge"
// Service: import { defineService } from "@keystone/sdk/service"
//
// This file re-exports both for convenience, but tree-shaking means
// only the side you actually use gets bundled.

export { keystone, action, subscribe, query, invoke, app, nativeWindow, dialog, shell } from "./bridge";
export type { KeystoneClient, Theme } from "./bridge";

export { defineService } from "./service";
export type { ServiceHandle, ServiceBuilder, ServiceContext } from "./service";

export { defineComponent } from "./component";
export type { ComponentDefinition, MountFn } from "./component";

export { defineConfig } from "./config";
export type { KeystoneRuntimeConfig } from "./config";

export { defineHost } from "./host";
export type { AppHostModule, HostContext, HostHook } from "./host";
