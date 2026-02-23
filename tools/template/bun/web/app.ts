// app.ts — Main window component for {{APP_NAME}}
// This file is auto-bundled and served by the Keystone runtime.
// It runs inside a WKWebView slot managed by the native window.
//
// Electron equivalent:
//   renderer process / index.html + renderer.js
//
// Import the bridge SDK to communicate with the native host:
//   action()   — fire-and-forget to C# (minimize, spawn windows, custom actions)
//   invoke()   — request/reply to C# native handlers (file dialogs, app info, etc.)
//   subscribe()— subscribe to named data channels pushed from Bun services
//   query()    — query a Bun service directly

import { keystone, app, nativeWindow, dialog } from "@keystone/sdk/bridge";

const ks = keystone();

export function mount(root: HTMLElement) {
  // Apply theme CSS variables to this component's root
  root.style.cssText = `
    display: flex;
    flex-direction: column;
    height: 100%;
    background: var(--ks-bg-surface);
    color: var(--ks-text-primary);
    font-family: var(--ks-font);
    box-sizing: border-box;
    padding: 32px;
    gap: 16px;
  `;

  const title = document.createElement("h1");
  title.textContent = "{{APP_NAME}}";
  title.style.cssText = `
    margin: 0;
    font-size: 24px;
    font-weight: 600;
    color: var(--ks-text-primary);
  `;

  const subtitle = document.createElement("p");
  subtitle.textContent = "Edit bun/web/app.ts to get started.";
  subtitle.style.cssText = `
    margin: 0;
    font-size: 14px;
    color: var(--ks-text-secondary);
  `;

  const btnRow = document.createElement("div");
  btnRow.style.cssText = "display: flex; gap: 8px; margin-top: 8px;";

  const openBtn = makeButton("Open File", "var(--ks-accent)");
  openBtn.onclick = async () => {
    const paths = await dialog.openFile({ title: "Open a file", multiple: false });
    if (paths) result.textContent = `Selected: ${paths[0]}`;
  };

  const infoBtn = makeButton("App Info", "var(--ks-bg-button)");
  infoBtn.onclick = async () => {
    const [name, version] = await Promise.all([app.getName(), app.getVersion()]);
    result.textContent = `${name} v${version}`;
  };

  const result = document.createElement("p");
  result.style.cssText = `
    margin: 0;
    font-size: 13px;
    color: var(--ks-text-muted);
    font-family: monospace;
  `;

  btnRow.appendChild(openBtn);
  btnRow.appendChild(infoBtn);
  root.appendChild(title);
  root.appendChild(subtitle);
  root.appendChild(btnRow);
  root.appendChild(result);

  // Subscribe to updates from Bun services (if any)
  // const unsub = ks.subscribe("my-channel", (data) => { ... });
  // return () => unsub();
}

export function unmount(root: HTMLElement) {
  root.innerHTML = "";
}

function makeButton(label: string, bg: string): HTMLButtonElement {
  const btn = document.createElement("button");
  btn.textContent = label;
  btn.style.cssText = `
    padding: 8px 16px;
    background: ${bg};
    color: var(--ks-text-primary);
    border: none;
    border-radius: 6px;
    font-size: 13px;
    cursor: pointer;
    font-family: var(--ks-font);
  `;
  btn.onmouseenter = () => (btn.style.filter = "brightness(1.15)");
  btn.onmouseleave = () => (btn.style.filter = "");
  return btn;
}
