// docs — Keystone documentation viewer

import { keystone, query } from "@keystone/sdk/bridge";

type DocEntry = { slug: string; title: string };

// ─── Markdown → HTML ────────────────────────────────────────────────────────

function renderMarkdown(md: string): string {
  const codeBlocks: string[] = [];

  md = md.replace(/```(\w*)\n([\s\S]*?)```/g, (_, lang, code) => {
    const idx = codeBlocks.length;
    const escaped = code.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;");
    codeBlocks.push(`<pre class="code-block"><code class="lang-${lang || "text"}">${escaped.trimEnd()}</code></pre>`);
    return `\x00CODE${idx}\x00`;
  });

  md = md.replace(/`([^`]+)`/g, (_, c) =>
    `<code class="inline-code">${c.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</code>`
  );

  const lines = md.split("\n");
  const out: string[] = [];
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
      const tableLines: string[] = [];
      while (i < lines.length && lines[i].includes("|")) tableLines.push(lines[i++]);
      out.push(renderTable(tableLines));
      continue;
    }

    if (/^[\-\*]\s/.test(line)) {
      const items: string[] = [];
      while (i < lines.length && /^[\-\*]\s/.test(lines[i]))
        items.push(`<li>${inlineFormat(lines[i++].replace(/^[\-\*]\s/, ""))}</li>`);
      out.push(`<ul class="md-ul">${items.join("")}</ul>`);
      continue;
    }

    if (/^\d+\.\s/.test(line)) {
      const items: string[] = [];
      while (i < lines.length && /^\d+\.\s/.test(lines[i]))
        items.push(`<li>${inlineFormat(lines[i++].replace(/^\d+\.\s/, ""))}</li>`);
      out.push(`<ol class="md-ol">${items.join("")}</ol>`);
      continue;
    }

    if (line.startsWith(">")) {
      const items: string[] = [];
      while (i < lines.length && lines[i].startsWith(">"))
        items.push(inlineFormat(lines[i++].slice(1).trim()));
      out.push(`<blockquote class="md-blockquote">${items.join("<br>")}</blockquote>`);
      continue;
    }

    if (line.trim() === "") { i++; continue; }

    const para: string[] = [];
    while (
      i < lines.length &&
      lines[i].trim() !== "" &&
      !/^#{1,6}\s/.test(lines[i]) &&
      !/^[\-\*\d]/.test(lines[i]) &&
      !/^---+$/.test(lines[i].trim()) &&
      !lines[i].includes("|") &&
      !/^\x00CODE\d+\x00$/.test(lines[i].trim())
    ) para.push(inlineFormat(lines[i++]));

    if (para.length) out.push(`<p class="md-p">${para.join(" ")}</p>`);
  }

  return out.join("\n");
}

function inlineFormat(text: string): string {
  return text
    .replace(/\*\*(.+?)\*\*/g, "<strong>$1</strong>")
    .replace(/\*(.+?)\*/g, "<em>$1</em>")
    .replace(/`([^`]+)`/g, (_, c) =>
      `<code class="inline-code">${c.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")}</code>`
    )
    .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a class="md-link" href="$2">$1</a>');
}

function renderTable(lines: string[]): string {
  const rows = lines.map((l) =>
    l.split("|").map((c) => c.trim()).filter((c, idx, arr) =>
      !(idx === 0 && c === "") && !(idx === arr.length - 1 && c === "")
    )
  );
  const header = rows[0];
  const body = rows.slice(2);
  const th = header.map((c) => `<th class="md-th">${inlineFormat(c)}</th>`).join("");
  const trs = body.map((row) =>
    `<tr>${row.map((c) => `<td class="md-td">${inlineFormat(c)}</td>`).join("")}</tr>`
  ).join("");
  return `<div class="table-wrap"><table class="md-table"><thead><tr>${th}</tr></thead><tbody>${trs}</tbody></table></div>`;
}

// ─── Component ───────────────────────────────────────────────────────────────

export function mount(root: HTMLElement) {
  const ks = keystone();

  // External stylesheet — served as a static asset from bun/web/docs.css
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

  const sidebar = root.querySelector<HTMLElement>("#sidebar")!;
  const content = root.querySelector<HTMLElement>("#content")!;
  const searchInput = root.querySelector<HTMLInputElement>("#search")!;
  const searchResults = root.querySelector<HTMLElement>("#search-results")!;

  let entries: DocEntry[] = [];
  let docCache = new Map<string, { title: string; content: string }>();
  let currentSlug = "";

  // ── Nav ──────────────────────────────────────────────────────────────────

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

  // ── Doc rendering ────────────────────────────────────────────────────────

  async function navigateTo(slug: string) {
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

    // Wire internal .md links to in-app navigation
    inner.querySelectorAll<HTMLAnchorElement>("a.md-link").forEach((a) => {
      const href = a.getAttribute("href") ?? "";
      if (href.endsWith(".md") && !href.startsWith("http")) {
        const targetSlug = href.replace(/\.md$/, "").replace(/^\.\//, "");
        if (entries.some((e) => e.slug === targetSlug)) {
          a.addEventListener("click", (ev) => { ev.preventDefault(); navigateTo(targetSlug); });
        }
      }
    });

    content.innerHTML = "";
    content.appendChild(inner);
    document.title = `${cached.title} — Keystone Docs`;
  }

  // ── Search ───────────────────────────────────────────────────────────────

  function showSearchResults(term: string) {
    if (!term.trim()) { searchResults.classList.remove("visible"); return; }

    const re = new RegExp(term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi");
    const results: Array<{ slug: string; title: string; match: string }> = [];

    for (const entry of entries) {
      if (results.length >= 8) break;
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

    searchResults.innerHTML = results.length === 0
      ? `<div class="search-no-results">No results for "${term}"</div>`
      : results.map((r) => `
          <div class="search-result" data-slug="${r.slug}">
            <div class="search-result-title">${r.title}</div>
            ${r.match ? `<div class="search-result-match">${r.match}</div>` : ""}
          </div>`).join("");

    searchResults.querySelectorAll<HTMLElement>(".search-result").forEach((el) => {
      el.addEventListener("click", () => {
        searchResults.classList.remove("visible");
        searchInput.value = "";
        navigateTo(el.dataset.slug!);
      });
    });

    searchResults.classList.add("visible");
  }

  searchInput.addEventListener("input", () => showSearchResults(searchInput.value));
  searchInput.addEventListener("keydown", (e) => {
    if (e.key === "Escape") { searchResults.classList.remove("visible"); searchInput.value = ""; }
  });
  document.addEventListener("click", (e) => {
    if (!searchResults.contains(e.target as Node) && e.target !== searchInput)
      searchResults.classList.remove("visible");
  });

  // ── Bootstrap ────────────────────────────────────────────────────────────

  ks.onConnect(async () => {
    const res = await query("docs");
    if (!res?.entries?.length) {
      content.innerHTML = `<div id="loading"><span>Could not load docs.</span></div>`;
      return;
    }
    entries = res.entries;
    renderSidebar();

    // Pre-cache all docs in the background so search works across the full corpus
    (async () => {
      for (const entry of entries) {
        if (docCache.has(entry.slug)) continue;
        const doc = await query("docs", { slug: entry.slug });
        if (doc.type === "doc") docCache.set(entry.slug, { title: doc.title, content: doc.content });
      }
    })();

    const first = entries.find((e) => e.slug === "README") ?? entries[0];
    if (first) navigateTo(first.slug);
  });
}

export function unmount(root: HTMLElement) {
  document.head.querySelector('link[href="/web/docs.css"]')?.remove();
  root.innerHTML = "";
}
