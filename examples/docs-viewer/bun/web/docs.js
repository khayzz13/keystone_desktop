// sdk/bridge.ts
var ws = null;
var _connected = false;
var _port = 0;
var _queryId = 0;
var channelHandlers = new Map;
var channelCache = new Map;
var pendingSubs = new Set;
var queryCallbacks = new Map;
var pendingQueries = [];
var connectCallbacks = new Set;
var actionCallbacks = new Set;
var themeCallbacks = new Set;
var _invokeId = 0;
var _windowId = window.__KEYSTONE_SLOT_CTX__?.windowId ?? "";
var theme = {
  bgBase: "#1a1a22",
  bgSurface: "#1e1e23",
  bgElevated: "#24242e",
  bgChrome: "#22222c",
  bgStrip: "#252530",
  bgHover: "#3a3a48",
  bgPressed: "#32323e",
  bgButton: "#2a2a36",
  bgButtonHover: "#3a3a48",
  bgButtonDark: "#32323e",
  bgMedium: "#2a2a32",
  bgLight: "#3a3a44",
  stroke: "#33333e",
  divider: "#2e2e3a",
  accent: "#4a6fa5",
  accentBright: "#4a9eff",
  accentHeader: "#6a8abc",
  success: "#26a69a",
  warning: "#ffca28",
  danger: "#ef5350",
  textPrimary: "#cccccc",
  textSecondary: "#888888",
  textMuted: "#667788",
  textSubtle: "rgba(255,255,255,0.67)",
  font: '-apple-system, BlinkMacSystemFont, "SF Pro Text", system-ui, sans-serif'
};
function connect(port) {
  _port = port;
  ws = new WebSocket(`ws://127.0.0.1:${port}/ws`);
  ws.onopen = () => {
    _connected = true;
    for (const channel of pendingSubs) {
      ws.send(JSON.stringify({ type: "subscribe", data: { channel } }));
    }
    pendingSubs.clear();
    for (const q of pendingQueries) {
      ws.send(JSON.stringify({ type: "query", data: { id: q.id, service: q.service, args: q.args } }));
    }
    pendingQueries.length = 0;
    connectCallbacks.forEach((cb) => {
      try {
        cb();
      } catch {}
    });
  };
  ws.onmessage = (e) => {
    try {
      const msg = JSON.parse(e.data);
      if (msg.type === "action" && msg.action) {
        actionCallbacks.forEach((cb) => {
          try {
            cb(msg.action);
          } catch {}
        });
        return;
      }
      if (msg.type === "__theme__") {
        Object.assign(theme, msg.data);
        applyThemeCSSVars();
        themeCallbacks.forEach((cb) => {
          try {
            cb(theme);
          } catch {}
        });
        return;
      }
      if (msg.type === "__query_result__" && typeof msg.id === "number") {
        const cb = queryCallbacks.get(msg.id);
        if (cb) {
          queryCallbacks.delete(msg.id);
          if (msg.error)
            cb.reject(new Error(msg.error));
          else
            cb.resolve(msg.result);
        }
        return;
      }
      if (msg.type && msg.data !== undefined) {
        channelCache.set(msg.type, msg.data);
        channelHandlers.get(msg.type)?.forEach((cb) => {
          try {
            cb(msg.data);
          } catch {}
        });
      }
    } catch {}
  };
  ws.onclose = () => {
    _connected = false;
    setTimeout(() => connect(port), 1500);
  };
}
function applyThemeCSSVars() {
  const root = document.documentElement;
  for (const [key, value] of Object.entries(theme)) {
    root.style.setProperty(`--ks-${camelToKebab(key)}`, value);
  }
}
function camelToKebab(s) {
  return s.replace(/[A-Z]/g, (m) => "-" + m.toLowerCase());
}
var _instance = null;
function keystone() {
  if (_instance)
    return _instance;
  const port = window.__KEYSTONE_PORT__;
  if (port) {
    connect(port);
    applyThemeCSSVars();
  }
  _instance = {
    action(action) {
      if (_connected)
        ws?.send(JSON.stringify({ type: "action", data: { action } }));
    },
    subscribe(channel, callback) {
      const set = channelHandlers.get(channel) ?? new Set;
      set.add(callback);
      channelHandlers.set(channel, set);
      if (_connected) {
        ws?.send(JSON.stringify({ type: "subscribe", data: { channel } }));
      } else {
        pendingSubs.add(channel);
      }
      const cached = channelCache.get(channel);
      if (cached !== undefined)
        callback(cached);
      return () => {
        set.delete(callback);
      };
    },
    async query(service, args) {
      return new Promise((resolve, reject) => {
        const id = ++_queryId;
        queryCallbacks.set(id, { resolve, reject });
        if (_connected) {
          ws?.send(JSON.stringify({ type: "query", data: { id, service, args } }));
        } else {
          pendingQueries.push({ id, service, args });
        }
        setTimeout(() => {
          if (queryCallbacks.delete(id))
            reject(new Error(`Query timeout: ${service}`));
        }, 1e4);
      });
    },
    invoke(channel, args) {
      return new Promise((resolve, reject) => {
        const id = ++_invokeId;
        const replyChannel = `window:${_windowId}:__reply__:${id}`;
        const unsub = _instance.subscribe(replyChannel, (data) => {
          unsub();
          if (data?.error)
            reject(new Error(data.error));
          else
            resolve(data?.result);
        });
        const wk = window.webkit?.messageHandlers?.keystone;
        if (wk) {
          wk.postMessage(JSON.stringify({ ks_invoke: true, id, channel, args, windowId: _windowId }));
        } else {
          unsub();
          reject(new Error("invoke() requires WKWebView (webkit.messageHandlers not available)"));
          return;
        }
        setTimeout(() => {
          unsub();
          reject(new Error(`invoke timeout: ${channel}`));
        }, 15000);
      });
    },
    send(type, data) {
      if (_connected)
        ws?.send(JSON.stringify({ type, data }));
    },
    get theme() {
      return theme;
    },
    onThemeChange(callback) {
      themeCallbacks.add(callback);
      return () => {
        themeCallbacks.delete(callback);
      };
    },
    onConnect(callback) {
      connectCallbacks.add(callback);
      if (_connected)
        callback();
      return () => {
        connectCallbacks.delete(callback);
      };
    },
    onAction(callback) {
      actionCallbacks.add(callback);
      return () => {
        actionCallbacks.delete(callback);
      };
    },
    get connected() {
      return _connected;
    },
    get port() {
      return _port;
    },
    get windowId() {
      return _windowId;
    }
  };
  return _instance;
}
var query = (svc, args) => keystone().query(svc, args);

// ../../../../../../../../../examples/docs-viewer/bun/web/docs.ts
function renderMarkdown(md) {
  const codeBlocks = [];
  md = md.replace(/```(\w*)\n([\s\S]*?)```/g, (_, lang, code) => {
    const idx = codeBlocks.length;
    const escaped = code.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    codeBlocks.push(`<pre class="code-block"><code class="lang-${lang || "text"}">${escaped.trimEnd()}</code></pre>`);
    return `\x00CODE${idx}\x00`;
  });
  md = md.replace(/`([^`]+)`/g, (_, c) => `<code class="inline-code">${c.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</code>`);
  const lines = md.split(`
`);
  const out = [];
  let i = 0;
  while (i < lines.length) {
    const line = lines[i];
    if (/^\x00CODE\d+\x00$/.test(line.trim())) {
      out.push(codeBlocks[parseInt(line.trim().slice(5, -1))]);
      i++;
      continue;
    }
    const h = line.match(/^(#{1,6})\s+(.+)$/);
    if (h) {
      const level = h[1].length;
      const id = h[2].toLowerCase().replace(/[^\w\s-]/g, "").replace(/\s+/g, "-");
      out.push(`<h${level} id="${id}" class="md-h${level}">${inlineFormat(h[2])}</h${level}>`);
      i++;
      continue;
    }
    if (/^---+$/.test(line.trim())) {
      out.push(`<hr class="md-hr">`);
      i++;
      continue;
    }
    if (line.includes("|") && i + 1 < lines.length && /^\|?[\s\-|:]+\|?$/.test(lines[i + 1])) {
      const tableLines = [];
      while (i < lines.length && lines[i].includes("|"))
        tableLines.push(lines[i++]);
      out.push(renderTable(tableLines));
      continue;
    }
    if (/^[\-\*]\s/.test(line)) {
      const items = [];
      while (i < lines.length && /^[\-\*]\s/.test(lines[i]))
        items.push(`<li>${inlineFormat(lines[i++].replace(/^[\-\*]\s/, ""))}</li>`);
      out.push(`<ul class="md-ul">${items.join("")}</ul>`);
      continue;
    }
    if (/^\d+\.\s/.test(line)) {
      const items = [];
      while (i < lines.length && /^\d+\.\s/.test(lines[i]))
        items.push(`<li>${inlineFormat(lines[i++].replace(/^\d+\.\s/, ""))}</li>`);
      out.push(`<ol class="md-ol">${items.join("")}</ol>`);
      continue;
    }
    if (line.startsWith(">")) {
      const items = [];
      while (i < lines.length && lines[i].startsWith(">"))
        items.push(inlineFormat(lines[i++].slice(1).trim()));
      out.push(`<blockquote class="md-blockquote">${items.join("<br>")}</blockquote>`);
      continue;
    }
    if (line.trim() === "") {
      i++;
      continue;
    }
    const para = [];
    while (i < lines.length && lines[i].trim() !== "" && !/^#{1,6}\s/.test(lines[i]) && !/^[\-\*\d]/.test(lines[i]) && !/^---+$/.test(lines[i].trim()) && !lines[i].includes("|") && !/^\x00CODE\d+\x00$/.test(lines[i].trim()))
      para.push(inlineFormat(lines[i++]));
    if (para.length)
      out.push(`<p class="md-p">${para.join(" ")}</p>`);
  }
  return out.join(`
`);
}
function inlineFormat(text) {
  return text.replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>").replace(/\*(.+?)\*/g, "<em>$1</em>").replace(/`([^`]+)`/g, (_, c) => `<code class="inline-code">${c.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</code>`).replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a class="md-link" href="$2">$1</a>');
}
function renderTable(lines) {
  const rows = lines.map((l) => l.split("|").map((c) => c.trim()).filter((c, idx, arr) => !(idx === 0 && c === "") && !(idx === arr.length - 1 && c === "")));
  const header = rows[0];
  const body = rows.slice(2);
  const th = header.map((c) => `<th class="md-th">${inlineFormat(c)}</th>`).join("");
  const trs = body.map((row) => `<tr>${row.map((c) => `<td class="md-td">${inlineFormat(c)}</td>`).join("")}</tr>`).join("");
  return `<div class="table-wrap"><table class="md-table"><thead><tr>${th}</tr></thead><tbody>${trs}</tbody></table></div>`;
}
function mount(root) {
  const ks = keystone();
  const link = document.createElement("link");
  link.rel = "stylesheet";
  link.href = "/web/docs.css";
  document.head.appendChild(link);
  root.innerHTML = `
    <div id="app">
      <header id="header">
        <div id="header-logo">K</div>
        <span id="header-title">Keystone Docs</span>
        <div id="header-spacer"></div>
        <div id="search-wrap">
          <span id="search-icon">⌕</span>
          <input id="search" type="text" placeholder="Search docs…" autocomplete="off" spellcheck="false">
        </div>
      </header>
      <div id="body">
        <nav id="sidebar"></nav>
        <main id="content">
          <div id="loading"><div class="spinner"></div><span>Loading…</span></div>
        </main>
      </div>
      <div id="search-results"></div>
    </div>
  `;
  const sidebar = root.querySelector("#sidebar");
  const content = root.querySelector("#content");
  const searchInput = root.querySelector("#search");
  const searchResults = root.querySelector("#search-results");
  let entries = [];
  let docCache = new Map;
  let currentSlug = "";
  function renderSidebar() {
    sidebar.innerHTML = "";
    for (const entry of entries) {
      const item = document.createElement("div");
      item.className = "nav-item" + (entry.slug === currentSlug ? " active" : "");
      item.innerHTML = `<span class="nav-dot"></span>${entry.title}`;
      item.addEventListener("click", () => navigateTo(entry.slug));
      sidebar.appendChild(item);
    }
  }
  async function navigateTo(slug) {
    currentSlug = slug;
    renderSidebar();
    content.innerHTML = `<div id="loading"><div class="spinner"></div><span>Loading…</span></div>`;
    content.scrollTop = 0;
    let cached = docCache.get(slug);
    if (!cached) {
      const res = await query("docs", { slug });
      if (res.type !== "doc") {
        content.innerHTML = `<div id="loading"><span>Not found.</span></div>`;
        return;
      }
      cached = { title: res.title, content: res.content };
      docCache.set(slug, cached);
    }
    const inner = document.createElement("div");
    inner.id = "content-inner";
    inner.innerHTML = renderMarkdown(cached.content);
    inner.querySelectorAll("a.md-link").forEach((a) => {
      const href = a.getAttribute("href") ?? "";
      if (href.endsWith(".md") && !href.startsWith("http")) {
        const targetSlug = href.replace(/\.md$/, "").replace(/^\.\//, "");
        if (entries.some((e) => e.slug === targetSlug)) {
          a.addEventListener("click", (ev) => {
            ev.preventDefault();
            navigateTo(targetSlug);
          });
        }
      }
    });
    content.innerHTML = "";
    content.appendChild(inner);
    document.title = `${cached.title} — Keystone Docs`;
  }
  function showSearchResults(term) {
    if (!term.trim()) {
      searchResults.classList.remove("visible");
      return;
    }
    const re = new RegExp(term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi");
    const results = [];
    for (const entry of entries) {
      if (results.length >= 8)
        break;
      const cached = docCache.get(entry.slug);
      if (entry.title.match(re)) {
        results.push({ slug: entry.slug, title: entry.title, match: "" });
      } else if (cached) {
        const idx = cached.content.search(re);
        if (idx !== -1) {
          const snippet = cached.content.slice(Math.max(0, idx - 30), idx + 80).replace(/\n/g, " ").trim();
          results.push({ slug: entry.slug, title: entry.title, match: snippet.replace(re, (m) => `<mark>${m}</mark>`) });
        }
      }
    }
    searchResults.innerHTML = results.length === 0 ? `<div class="search-no-results">No results for "${term}"</div>` : results.map((r) => `
          <div class="search-result" data-slug="${r.slug}">
            <div class="search-result-title">${r.title}</div>
            ${r.match ? `<div class="search-result-match">${r.match}</div>` : ""}
          </div>`).join("");
    searchResults.querySelectorAll(".search-result").forEach((el) => {
      el.addEventListener("click", () => {
        searchResults.classList.remove("visible");
        searchInput.value = "";
        navigateTo(el.dataset.slug);
      });
    });
    searchResults.classList.add("visible");
  }
  searchInput.addEventListener("input", () => showSearchResults(searchInput.value));
  searchInput.addEventListener("keydown", (e) => {
    if (e.key === "Escape") {
      searchResults.classList.remove("visible");
      searchInput.value = "";
    }
  });
  document.addEventListener("click", (e) => {
    if (!searchResults.contains(e.target) && e.target !== searchInput)
      searchResults.classList.remove("visible");
  });
  ks.onConnect(async () => {
    const res = await query("docs");
    if (!res?.entries?.length) {
      content.innerHTML = `<div id="loading"><span>Could not load docs.</span></div>`;
      return;
    }
    entries = res.entries;
    renderSidebar();
    (async () => {
      for (const entry of entries) {
        if (docCache.has(entry.slug))
          continue;
        const doc = await query("docs", { slug: entry.slug });
        if (doc.type === "doc")
          docCache.set(entry.slug, { title: doc.title, content: doc.content });
      }
    })();
    const first = entries.find((e) => e.slug === "README") ?? entries[0];
    if (first)
      navigateTo(first.slug);
  });
}
function unmount(root) {
  document.head.querySelector('link[href="/web/docs.css"]')?.remove();
  root.innerHTML = "";
}
export {
  unmount,
  mount
};
