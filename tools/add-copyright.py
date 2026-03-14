#!/usr/bin/env python3
"""Add copyright header to all .cs and .ts/.tsx files in the Keystone framework."""

import os
import sys

FRAMEWORK_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

HEADER = """/*---------------------------------------------------------------------------------------------
 *  Copyright (c) 2026 Kaedyn Limon. All rights reserved.
 *  Licensed under the MIT License. See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/"""

SKIP_DIRS = {
    "bin", "obj", "node_modules", ".git", "dist", "build",
    "packages", ".vs", ".idea", "TestResults",
}

SKIP_FILES = {
    "GlobalUsings.cs",
    "AssemblyInfo.cs",
}


def should_skip(path: str) -> bool:
    parts = path.split(os.sep)
    return any(p in SKIP_DIRS for p in parts)


def needs_header(content: str) -> bool:
    if not content.strip():
        return False
    if content.startswith("/*---"):
        first_block = content[:content.find("*/") + 2] if "*/" in content else ""
        if "Copyright" in first_block and "Kaedyn Limon" in first_block:
            return False
    return True


def add_header(filepath: str, dry_run: bool) -> bool:
    with open(filepath, "r", encoding="utf-8", errors="replace") as f:
        content = f.read()

    if not needs_header(content):
        return False

    if dry_run:
        return True

    new_content = HEADER + "\n\n" + content
    with open(filepath, "w", encoding="utf-8") as f:
        f.write(new_content)
    return True


def main():
    dry_run = "--dry-run" in sys.argv
    verbose = "--verbose" in sys.argv or "-v" in sys.argv

    if dry_run:
        print("DRY RUN — no files will be modified\n")

    count = 0
    skipped = 0

    for root, dirs, files in os.walk(FRAMEWORK_ROOT):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]

        for name in sorted(files):
            if name in SKIP_FILES:
                continue
            if not (name.endswith(".cs") or name.endswith(".ts") or name.endswith(".tsx")):
                continue

            filepath = os.path.join(root, name)
            rel = os.path.relpath(filepath, FRAMEWORK_ROOT)

            if should_skip(rel):
                continue

            if add_header(filepath, dry_run):
                count += 1
                if verbose:
                    print(f"  + {rel}")
            else:
                skipped += 1

    action = "Would add" if dry_run else "Added"
    print(f"\n{action} header to {count} files")
    print(f"Skipped {skipped} (already had header or empty)")


if __name__ == "__main__":
    main()
