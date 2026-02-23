#!/usr/bin/env python3
"""
Keystone Docs Viewer — Build Script

Usage:
  python3 build.py          # Copy native libs, install Bun deps, run
  python3 build.py --run    # Same as above
  python3 build.py --bun    # Install Bun deps only
  python3 build.py --package  # Package into a distributable .app
  python3 build.py --dmg    # Package and create a DMG
"""

import subprocess
import os
import shutil
import argparse
from pathlib import Path

APP_ROOT = Path(__file__).parent
ENGINE_ROOT = (APP_ROOT / ".." / "..").resolve()
DYLIB_DIR = APP_ROOT / "dylib"

def run(cmd, cwd=None, check=True):
    print(f"  $ {' '.join(cmd) if isinstance(cmd, list) else cmd}")
    return subprocess.run(cmd, cwd=cwd, check=check, shell=isinstance(cmd, str))


def copy_native_libs():
    native_src = ENGINE_ROOT / "dylib" / "native"
    native_dst = DYLIB_DIR / "native"

    if not native_src.exists():
        print("\n  NOTE: No native libs in engine — run engine build first")
        print(f"    python3 {ENGINE_ROOT}/build.py")
        return

    native_dst.mkdir(parents=True, exist_ok=True)
    copied = 0
    for dylib in native_src.glob("*.dylib"):
        shutil.copy2(dylib, native_dst / dylib.name)
        print(f"  Copied {dylib.name} → dylib/native/")
        copied += 1

    if copied == 0:
        print("  NOTE: No .dylib files found in engine dylib/native/ — run engine build first")


def setup_bun():
    bun_dir = APP_ROOT / "bun"
    if (bun_dir / "package.json").exists() and not (bun_dir / "node_modules").exists():
        print("\n=== Installing Bun Dependencies ===")
        run(["bun", "install"], cwd=bun_dir)
    else:
        print("\n=== Bun dependencies up to date ===")


def run_app(debug=False):
    config = "Debug" if debug else "Release"
    bundle = ENGINE_ROOT / "Keystone.App" / "bin" / config / "net10.0-macos" / "osx-arm64" / "Keystone.app"

    if not bundle.exists():
        print(f"\n  ERROR: Engine bundle not found at {bundle}")
        print(f"  Build the engine first: python3 {ENGINE_ROOT}/build.py")
        return

    exe = bundle / "Contents" / "MacOS" / "Keystone.App"
    print(f"\n=== Running Keystone Docs ===")
    env = os.environ.copy()
    env["KEYSTONE_ROOT"] = str(APP_ROOT.resolve())
    subprocess.run([str(exe)], env=env, check=False)


def main():
    parser = argparse.ArgumentParser(description="Build and run the Keystone Docs Viewer")
    parser.add_argument("--run", action="store_true", help="Copy native libs, install deps, and run (default)")
    parser.add_argument("--bun", action="store_true", help="Install Bun deps only")
    parser.add_argument("--debug", action="store_true", help="Run in Debug mode")
    parser.add_argument("--package", action="store_true", help="Package into a distributable .app")
    parser.add_argument("--dmg", action="store_true", help="Also create a DMG (use with --package)")
    args = parser.parse_args()

    os.chdir(APP_ROOT)

    if args.package:
        engine_build = ENGINE_ROOT / "build.py"
        run(["python3", str(engine_build), "--package", str(APP_ROOT)] +
            (["--dmg"] if args.dmg else []) +
            (["--debug"] if args.debug else []))
        return

    if not args.bun:
        print("\n=== Copying Native Libraries ===")
        copy_native_libs()

    setup_bun()

    if args.bun:
        return

    run_app(debug=args.debug)


if __name__ == "__main__":
    main()
