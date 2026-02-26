#!/usr/bin/env python3
"""
Keystone App Scaffolding
Creates a new Keystone application from template.

Web-only (default) — TypeScript + Bun, no C# required:
  python3 tools/create-app.py my-app
  python3 tools/create-app.py my-app --name "My App" --id "com.example.myapp"

With native C# assembly (for custom plugins, services, or logic):
  python3 tools/create-app.py my-app --native
"""

import argparse
import os
import re
import shutil
from pathlib import Path

SCRIPT_DIR = Path(__file__).parent
TEMPLATE_DIR = SCRIPT_DIR / "template"
ENGINE_ROOT = SCRIPT_DIR.parent

# Read version from engine root
_version_file = ENGINE_ROOT / "version.txt"
KEYSTONE_VERSION = _version_file.read_text().strip() if _version_file.exists() else "0.1.0"


def slug_to_name(slug: str) -> str:
    """my-app -> My App"""
    return " ".join(word.capitalize() for word in re.split(r"[-_]", slug))


def slug_to_namespace(slug: str) -> str:
    """my-app -> MyApp"""
    return "".join(word.capitalize() for word in re.split(r"[-_]", slug))


def slug_to_id(slug: str) -> str:
    """my-app -> com.keystone.myapp"""
    clean = re.sub(r"[-_]", "", slug.lower())
    return f"com.keystone.{clean}"


def scaffold(target_dir: Path, replacements: dict, native: bool):
    """Copy template/ to target_dir with placeholder substitution."""
    if target_dir.exists():
        print(f"ERROR: Directory already exists: {target_dir}")
        raise SystemExit(1)

    print(f"\nCreating {target_dir.name}...")

    # Copy entire template tree
    shutil.copytree(TEMPLATE_DIR, target_dir)

    # In web-only mode: remove the app/ C# directory
    if not native:
        app_dir = target_dir / "app"
        if app_dir.exists():
            shutil.rmtree(app_dir)

    # Walk all files and do replacements
    for filepath in sorted(target_dir.rglob("*")):
        if filepath.is_dir():
            continue

        try:
            content = filepath.read_text()
            original = content
            for placeholder, value in replacements.items():
                content = content.replace(placeholder, value)
            if content != original:
                filepath.write_text(content)
        except UnicodeDecodeError:
            pass  # binary file, skip

    # Native mode: rename App.Core.csproj to {Namespace}.Core.csproj
    if native:
        old_csproj = target_dir / "app" / "App.Core.csproj"
        new_csproj = target_dir / "app" / f"{replacements['{{APP_NAMESPACE}}']}.Core.csproj"
        if old_csproj.exists():
            old_csproj.rename(new_csproj)

    # Make build.py executable
    build_py = target_dir / "build.py"
    if build_py.exists():
        build_py.chmod(0o755)

    # Create empty placeholder directories
    for d in ["dylib", "icons", "scripts"]:
        (target_dir / d).mkdir(exist_ok=True)

    print(f"  Created {target_dir}")


def print_next_steps(target_dir: Path, name: str, native: bool):
    print(f"\n{'=' * 52}")
    print(f"  {name} created!")
    print(f"{'=' * 52}")
    if native:
        print(f"""
  Web + Native C# app. Build and run:
    cd {target_dir}
    python3 build.py --run

  Structure:
    keystone.config.json   runtime config (windows, menus, plugins)
    keystone.build.yaml    build config (engine version, packaging)
    build.py               build shim (calls engine tools)
    app/                   C# assembly (ICorePlugin + custom plugins)
    bun/
      host.ts              Bun lifecycle hooks (onReady, onShutdown, etc.)
      keystone.config.ts   bun runtime config
      package.json         app dependencies
      web/app.ts           main window component (edit this first)
      services/            Bun background services
    dylib/                 hot-reloadable plugin DLLs
    icons/                 SVG/ICNS assets
""")
    else:
        print(f"""
  Web-only app — no C# required. To run:
    cd {target_dir}
    python3 build.py --run

  Structure:
    keystone.config.json   runtime config (windows, menus, plugins)
    keystone.build.yaml    build config (engine version, packaging)
    build.py               build shim (calls engine tools)
    bun/
      host.ts              Bun lifecycle hooks (onReady, onShutdown, etc.)
      keystone.config.ts   bun runtime config
      package.json         app dependencies
      web/app.ts           main window  <- start here
      services/            Bun background services
    icons/                 SVG/ICNS assets

  To add native C# plugins later, re-scaffold with --native or
  manually add an app/ directory and set appAssembly in keystone.config.json.
""")


def main():
    parser = argparse.ArgumentParser(
        description="Create a new Keystone application",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__.strip(),
    )
    parser.add_argument("slug", help="Directory name (e.g. my-app)")
    parser.add_argument("--name", help="Display name (default: inferred from slug)")
    parser.add_argument("--id", help="Bundle ID (default: com.keystone.<slug>)")
    parser.add_argument("--dir", help="Parent directory (default: current directory)", default=".")
    parser.add_argument(
        "--native",
        action="store_true",
        help="Include C# assembly scaffold (app/ + csproj) for native plugins",
    )
    args = parser.parse_args()

    slug = args.slug
    name = args.name or slug_to_name(slug)
    namespace = slug_to_namespace(slug)
    app_id = args.id or slug_to_id(slug)
    target_dir = (Path(args.dir) / slug).resolve()

    # Compute relative path from the new app's app/ directory to the engine root.
    # Used for csproj ProjectReferences and tsconfig paths.
    app_csharp_dir = target_dir / "app"
    engine_rel = os.path.relpath(ENGINE_ROOT, app_csharp_dir).replace("\\", "/")
    bun_dir = target_dir / "bun"
    engine_rel_bun = os.path.relpath(ENGINE_ROOT, bun_dir).replace("\\", "/")

    replacements = {
        "{{APP_NAME}}": name,
        "{{APP_ID}}": app_id,
        "{{APP_NAMESPACE}}": namespace,
        "{{APP_SLUG}}": slug,
        "{{KEYSTONE_VERSION}}": KEYSTONE_VERSION,
        "{{ENGINE_REL}}": engine_rel,
        "{{ENGINE_REL_BUN}}": engine_rel_bun,
    }

    mode = "web + native C#" if args.native else "web-only"
    print(f"  Name:      {name}")
    print(f"  ID:        {app_id}")
    print(f"  Mode:      {mode}")
    print(f"  Directory: {target_dir}")
    print(f"  Keystone:  {KEYSTONE_VERSION}")

    scaffold(target_dir, replacements, native=args.native)
    print_next_steps(target_dir, name, native=args.native)


if __name__ == "__main__":
    main()
