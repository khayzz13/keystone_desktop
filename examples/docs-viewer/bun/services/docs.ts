// docs service — reads markdown files from the docs directory and serves them to the web component

import { defineService } from "@keystone/sdk/service";
import { join, dirname, basename } from "path";
import { readdir, readFile } from "fs/promises";
import { fileURLToPath } from "url";

const __dir = dirname(fileURLToPath(import.meta.url));
// bun/services/ -> bun/ -> docs-viewer/ -> examples/ -> keystone/ -> docs/
const DOCS_DIR = join(__dir, "..", "..", "..", "..", "docs");

const ORDER = [
  "README.md",
  "getting-started.md",
  "process-model.md",
  "web-components.md",
  "bun-services.md",
  "csharp-app-layer.md",
  "native-api.md",
  "configuration.md",
];

type DocEntry = { slug: string; title: string };
type DocContent = { slug: string; title: string; content: string };

function slugify(filename: string): string {
  return filename.replace(/\.md$/, "");
}

function titleFromContent(content: string, filename: string): string {
  const match = content.match(/^#\s+(.+)$/m);
  if (match) return match[1].trim();
  return basename(filename, ".md")
    .replace(/-/g, " ")
    .replace(/\b\w/g, (c) => c.toUpperCase());
}

export default defineService("docs")
  .query(async (args: { slug?: string }) => {
    const files = await readdir(DOCS_DIR);
    const mdFiles = files.filter((f) => f.endsWith(".md") && !f.startsWith("."));

    // Sort by ORDER array, then alphabetically for anything not listed
    mdFiles.sort((a, b) => {
      const ai = ORDER.indexOf(a);
      const bi = ORDER.indexOf(b);
      if (ai !== -1 && bi !== -1) return ai - bi;
      if (ai !== -1) return -1;
      if (bi !== -1) return 1;
      return a.localeCompare(b);
    });

    if (!args?.slug) {
      // Return index
      const entries: DocEntry[] = await Promise.all(
        mdFiles.map(async (f) => {
          const content = await readFile(join(DOCS_DIR, f), "utf-8");
          return { slug: slugify(f), title: titleFromContent(content, f) };
        })
      );
      return { type: "index", entries };
    }

    // Return specific doc
    const filename = args.slug + ".md";
    const filepath = join(DOCS_DIR, filename);
    try {
      const content = await readFile(filepath, "utf-8");
      const title = titleFromContent(content, filename);
      return { type: "doc", slug: args.slug, title, content } satisfies DocContent & { type: string };
    } catch {
      return { type: "error", message: `Not found: ${args.slug}` };
    }
  })
  .build();
