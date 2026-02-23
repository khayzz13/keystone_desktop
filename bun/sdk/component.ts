// @keystone/sdk/component — Optional cleanup-wiring helper
//
// The slot host only requires two named exports from your entry file:
//
//   export function mount(root: HTMLElement, ctx: SlotContext) { ... }
//   export function unmount(root: HTMLElement) { ... }          // optional
//
// That's it. The entry file can import from any number of modules, use any
// framework, and have any internal structure — Keystone doesn't care.
//
// defineComponent() is a convenience that auto-wires a cleanup return value
// from your mount callback to the unmount export. Use it if you want. Skip it
// if you don't.

import type { SlotContext } from "../types";

export type { SlotContext } from "../types";

export type MountFn = (root: HTMLElement, ctx: SlotContext) => (() => void) | void;

/**
 * Optional helper that wires a mount callback's cleanup return to `unmount`.
 *
 * @example
 * export const { mount, unmount } = defineComponent((root, ctx) => {
 *   const unsub = subscribe(`window:${ctx.windowId}:data`, update);
 *   return () => unsub();  // called automatically on unmount
 * });
 */
export function defineComponent(mount: MountFn): {
    mount: (root: HTMLElement, ctx: SlotContext) => void;
    unmount: (root: HTMLElement) => void;
} {
    let _cleanup: (() => void) | null = null;

    return {
        mount(root, ctx) {
            const result = mount(root, ctx);
            _cleanup = typeof result === "function" ? result : null;
        },

        unmount(root) {
            try { _cleanup?.(); } catch {}
            _cleanup = null;
            root.innerHTML = "";
        },
    };
}
