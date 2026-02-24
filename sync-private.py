#!/usr/bin/env python3
"""
sync-private.py — Sync public repo → private repo.

Pulls changes from the public repo back into the private clone,
skipping files that are private-only (they won't exist in public).

Usage:
  python3 sync-private.py           # apply changes
  python3 sync-private.py --dry-run # preview only
"""

import os
import sys
import shutil
import filecmp
from pathlib import Path

PRIVATE = Path(__file__).parent
PUBLIC  = PRIVATE.parent.parent / "main public"

# Files/dirs that live only in private — never overwritten from public
PRIVATE_ONLY = {
    "CLAUDE.md",
    "OVERVIEW.md",
    "version.txt",
    ".claude",
    "docs/initial plan docs",
    "examples/my-app",
    "sync-public.py",
    "sync-private.py",
}

# Files that live only in public — never copied into private
PUBLIC_ONLY = {
    "CONTRIBUTING.md",
    "KNOWN_LIMITATIONS.md",
}

# Directory names to skip entirely when walking
SKIP_DIRS = {".git", "obj", "bin", "node_modules", ".claude"}

# Skip by suffix or exact filename
SKIP_SUFFIXES = {".user", ".dylib", ".so"}
SKIP_NAMES    = {"Keystone (Server)", ".DS_Store", ".gitignore"}


def is_private_only(rel: Path) -> bool:
    s = str(rel)
    for p in PRIVATE_ONLY:
        if s == p or s.startswith(p + "/") or s.startswith(p + os.sep):
            return True
    return False


def is_public_only(rel: Path) -> bool:
    return rel.name in PUBLIC_ONLY


def skip_entry(rel: Path) -> bool:
    for part in rel.parts:
        if part in SKIP_DIRS:
            return True
    if rel.suffix in SKIP_SUFFIXES or rel.name in SKIP_NAMES:
        return True
    return False


def sync(dry_run: bool):
    copied, new_in_public = [], []

    # ── public → private ──────────────────────────────────────────────────────
    for src in PUBLIC.rglob("*"):
        if src.is_dir():
            continue
        rel = src.relative_to(PUBLIC)
        if skip_entry(rel) or is_public_only(rel):
            continue

        dst = PRIVATE / rel
        if is_private_only(rel):
            continue

        if not dst.exists():
            new_in_public.append(str(rel))
            if not dry_run:
                dst.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(str(src), str(dst))
        elif not filecmp.cmp(str(src), str(dst), shallow=False):
            copied.append(str(rel))
            if not dry_run:
                shutil.copy2(str(src), str(dst))

    # ── report ────────────────────────────────────────────────────────────────
    prefix = "[dry run] " if dry_run else ""
    if copied:
        print(f"{prefix}Updated ({len(copied)}):")
        for f in sorted(copied):
            print(f"  ~ {f}")
    if new_in_public:
        print(f"{prefix}New from public ({len(new_in_public)}):")
        for f in sorted(new_in_public):
            print(f"  + {f}")
    if not copied and not new_in_public:
        print("Already in sync.")


if __name__ == "__main__":
    dry_run = "--dry-run" in sys.argv or "-n" in sys.argv
    if not PUBLIC.is_dir():
        print(f"Public repo not found at: {PUBLIC}", file=sys.stderr)
        sys.exit(1)
    if dry_run:
        print("[dry run — no changes written]\n")
    sync(dry_run)
