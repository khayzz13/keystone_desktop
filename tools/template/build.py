#!/usr/bin/env python3
"""
{{APP_NAME}} Build Script

Usage:
  python3 build.py          # Build app assembly + bun install
  python3 build.py --run    # Build and run
  python3 build.py --package# Package into distributable .app bundle
  python3 build.py --plugins# Rebuild plugin DLLs only
  python3 build.py --clean  # Clean all build artifacts
  python3 build.py --debug  # Debug mode
"""

import subprocess
import os
import sys
import shutil
import argparse
import tarfile
import urllib.request
from pathlib import Path

APP_ROOT = Path(__file__).parent
DYLIB_DIR = APP_ROOT / "dylib"
APP_DIR = APP_ROOT / "app"
BUN_DIR = APP_ROOT / "bun"

KEYSTONE_VERSION = "{{KEYSTONE_VERSION}}"
ENGINE_CACHE = Path.home() / ".keystone" / "engines" / KEYSTONE_VERSION


def run(cmd, cwd=None, check=True):
    print(f"  $ {' '.join(str(c) for c in cmd) if isinstance(cmd, list) else cmd}")
    return subprocess.run(cmd, cwd=cwd, check=check, shell=isinstance(cmd, str))


# ─── Engine location ─────────────────────────────────────────────────────────

def find_engine() -> Path:
    """Locate the Keystone Desktop binary. Downloads if not present."""
    # 1. Vendored in project
    local = APP_ROOT / "keystone-desktop"
    if (local / "version.txt").exists():
        return local
    # 2. Global cache
    if (ENGINE_CACHE / "version.txt").exists():
        return ENGINE_CACHE
    # 3. Auto-download
    print(f"\nKeystone Desktop {KEYSTONE_VERSION} not found — downloading...")
    _download_engine(KEYSTONE_VERSION, ENGINE_CACHE)
    return ENGINE_CACHE


def _download_engine(version: str, dest: Path):
    tarball_name = f"Keystone-{version}-arm64.tar.gz"
    url = f"https://github.com/khayzz13/keystone-desktop/releases/download/{version}/{tarball_name}"
    dest.mkdir(parents=True, exist_ok=True)
    tmp = dest.parent / tarball_name
    print(f"  Downloading {url}")
    try:
        urllib.request.urlretrieve(url, tmp)
    except Exception as e:
        print(f"  ERROR: Download failed: {e}")
        print(f"  Download manually and extract to {dest}")
        sys.exit(1)
    print(f"  Extracting...")
    with tarfile.open(tmp) as t:
        t.extractall(dest.parent)
    tmp.unlink(missing_ok=True)
    extracted = dest.parent / "keystone-desktop"
    if extracted.exists() and extracted != dest:
        extracted.rename(dest)
    print(f"  Engine ready at {dest}")


# ─── App build steps ─────────────────────────────────────────────────────────

def _has_app_assembly() -> bool:
    return APP_DIR.exists() and any(APP_DIR.glob("*.csproj"))


def build_app_assembly(debug=False, engine: Path = None):
    if not _has_app_assembly():
        return
    config = "Debug" if debug else "Release"
    print(f"\n=== Building App Assembly ({config}) ===")
    csproj = next(APP_DIR.glob("*.csproj"))
    if engine:
        nuget_dir = engine / "nuget"
        if nuget_dir.exists():
            _write_nuget_config(nuget_dir)
    run(["dotnet", "build", str(csproj), "-c", config])


def _write_nuget_config(nuget_dir: Path):
    (APP_DIR / "nuget.config").write_text(f"""<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="keystone-desktop" value="{nuget_dir}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
""")


def build_plugins(debug=False):
    plugins_dir = APP_ROOT / "plugins"
    if not plugins_dir.exists() or not any(plugins_dir.glob("*.csproj")):
        return
    config = "Debug" if debug else "Release"
    print(f"\n=== Building Plugins ({config}) ===")
    DYLIB_DIR.mkdir(parents=True, exist_ok=True)
    for csproj in sorted(plugins_dir.glob("*.csproj")):
        print(f"\n  Building {csproj.stem}...")
        run(["dotnet", "build", str(csproj), "-c", config])


def setup_bun():
    if (BUN_DIR / "package.json").exists():
        print("\n=== Installing Bun Dependencies ===")
        run(["bun", "install"], cwd=BUN_DIR)


def find_engine_binary(engine: Path, debug=False) -> Path:
    """Find the Keystone.App binary — handles source checkouts and distributed engines."""
    # Distributed engine layout: bin/Keystone.App
    simple = engine / "bin" / "Keystone.App"
    if simple.exists():
        return simple
    # Source checkout: dotnet publish output
    config_mode = "Debug" if debug else "Release"
    for mode in [config_mode, "Release", "Debug"]:
        src = (engine / "Keystone.App" / "bin" / mode / "net10.0-macos" / "osx-arm64"
               / "Keystone.app" / "Contents" / "MacOS" / "Keystone.App")
        if src.exists():
            return src
    return None


def run_app(engine: Path, debug=False):
    binary = find_engine_binary(engine, debug)
    if binary is None:
        print(f"  ERROR: Engine binary not found. Build the engine first.")
        sys.exit(1)
    print(f"\n=== Running {{APP_NAME}} ===")
    env = os.environ.copy()
    env["KEYSTONE_ROOT"] = str(APP_ROOT)
    subprocess.run([str(binary)], cwd=APP_ROOT, env=env)


def package_app(engine: Path, debug=False, mode=None, dmg=False,
                allow_external=False):
    """Package the app using the framework's standalone packager."""
    packager = engine / "tools" / "package.py"
    if not packager.exists():
        print(f"  ERROR: Packager not found at {packager}")
        sys.exit(1)
    cmd = [sys.executable, str(packager), str(APP_ROOT), "--engine", str(engine)]
    if debug:
        cmd.append("--debug")
    if mode:
        cmd += ["--mode", mode]
    if allow_external:
        cmd.append("--allow-external")
    if dmg:
        cmd.append("--dmg")
    run(cmd)


def clean():
    print("\n=== Cleaning ===")
    for csproj in APP_ROOT.rglob("*.csproj"):
        proj_dir = csproj.parent
        for name in ["bin", "obj"]:
            d = proj_dir / name
            if d.exists():
                shutil.rmtree(d)
                print(f"  Removed {proj_dir.name}/{name}/")
    if DYLIB_DIR.exists():
        for f in DYLIB_DIR.glob("*.dll"):
            f.unlink()
            print(f"  Removed {f.name}")


def main():
    parser = argparse.ArgumentParser(description="Build {{APP_NAME}}")
    parser.add_argument("--run", action="store_true", help="Build and run")
    parser.add_argument("--package", action="store_true", help="Package into distributable .app")
    parser.add_argument("--mode", choices=["side-by-side", "bundled"],
                        help="Override build.pluginMode from config")
    parser.add_argument("--allow-external", action="store_true",
                        help="Allow externally-signed plugins")
    parser.add_argument("--dmg", action="store_true", help="Create DMG after packaging")
    parser.add_argument("--plugins", action="store_true", help="Build plugin DLLs only")
    parser.add_argument("--clean", action="store_true", help="Clean build artifacts")
    parser.add_argument("--debug", action="store_true", help="Debug mode")
    args = parser.parse_args()

    os.chdir(APP_ROOT)

    if args.clean:
        clean()
        return

    engine = find_engine()

    if args.plugins:
        build_plugins(debug=args.debug)
        return

    build_app_assembly(debug=args.debug, engine=engine)
    build_plugins(debug=args.debug)
    setup_bun()

    if args.package:
        package_app(engine, debug=args.debug, mode=args.mode, dmg=args.dmg,
                    allow_external=args.allow_external)
        return

    if args.run:
        run_app(engine, debug=args.debug)


if __name__ == "__main__":
    main()
