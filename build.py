#!/usr/bin/env python3
"""
Keystone Desktop — Framework Build Script
Builds the engine from source: Rust native libs → C# core → dotnet publish.
App packaging is handled by tools/package.py.
"""

import subprocess
import os
import shutil
import argparse
from pathlib import Path

ROOT = Path(__file__).parent
RUST_FFI_DIR = ROOT / "rust_ffi"
APP_NAME = "Keystone"

DYLIB_DIR = ROOT / "dylib"
NATIVE_DIR = DYLIB_DIR / "native"

def app_out_path(debug=False):
    config = "Debug" if debug else "Release"
    return ROOT / "Keystone.App" / "bin" / config / "net10.0-macos" / "osx-arm64"

APP_OUT = app_out_path()
APP_BUNDLE = APP_OUT / f"{APP_NAME}.app"

def run(cmd, cwd=None, check=True):
    print(f"  $ {' '.join(cmd) if isinstance(cmd, list) else cmd}")
    return subprocess.run(cmd, cwd=cwd, check=check, shell=isinstance(cmd, str))


def clean():
    print("\n=== Cleaning ===")
    for csproj in ROOT.rglob("*.csproj"):
        proj_dir = csproj.parent
        for name in ["bin", "obj"]:
            d = proj_dir / name
            if d.exists() and d.is_dir():
                shutil.rmtree(d)
                print(f"  Removed {proj_dir.name}/{name}/")
    if DYLIB_DIR.exists():
        shutil.rmtree(DYLIB_DIR)
        print(f"  Removed {DYLIB_DIR.name}/")

def build_rust(debug=False):
    print("\n=== Building Rust Native Libraries ===")
    os.chdir(RUST_FFI_DIR)

    print("\nBuilding keystone-layout...")
    cargo_args = ["cargo", "build", "-p", "keystone-layout"]
    if not debug:
        cargo_args.append("--release")
    run(cargo_args)

    os.chdir(ROOT)

    NATIVE_DIR.mkdir(parents=True, exist_ok=True)
    rust_target = RUST_FFI_DIR / "target" / ("debug" if debug else "release")

    dylibs = [
        "libkeystone_layout.dylib",
    ]

    print("\nCopying native dylibs to dylib/native/...")
    for name in dylibs:
        src = rust_target / name
        dst = NATIVE_DIR / name
        if src.exists():
            shutil.copy2(src, dst)
            print(f"  {name}")
        else:
            print(f"  {name} (not found)")

def build_core(debug=False):
    config = "Debug" if debug else "Release"
    print(f"\n=== Building Keystone Desktop ({config}) ===")
    projects = [
        ("Keystone.Core", "Keystone.Core/Keystone.Core.csproj"),
        ("Keystone.Core.Platform", "Keystone.Core.Platform/Keystone.Core.Platform.csproj"),
        ("Keystone.Core.Graphics.Skia", "Keystone.Core.Graphics.Skia/Keystone.Core.Graphics.Skia.csproj"),
        ("Keystone.Core.Management", "Keystone.Core.Management/Keystone.Core.Management.csproj"),
        ("Keystone.Core.Runtime", "Keystone.Core.Runtime/Keystone.Core.Runtime.csproj"),
        ("Keystone.Toolkit", "Keystone.Toolkit/Keystone.Toolkit.csproj"),
    ]
    for name, proj in projects:
        print(f"\nBuilding {name}...")
        run(["dotnet", "build", str(ROOT / proj), "-c", config])

def build_app(debug=False):
    config = "Debug" if debug else "Release"
    print(f"\n=== Building Keystone.App ({config}) ===")
    app_proj = ROOT / "Keystone.App" / "Keystone.App.csproj"

    print("\nPublishing app...")
    run([
        "dotnet", "publish", str(app_proj),
        "-c", config,
        "-r", "osx-arm64",
        "--self-contained", "true"
    ])

    print("\nSetting up app bundle...")
    bundle_contents = APP_BUNDLE / "Contents"
    bundle_macos = bundle_contents / "MacOS"
    bundle_resources = bundle_contents / "Resources"

    bundle_macos.mkdir(parents=True, exist_ok=True)
    bundle_resources.mkdir(parents=True, exist_ok=True)

    info_plist = ROOT / "Keystone.App" / "Info.plist"
    if info_plist.exists():
        shutil.copy2(info_plist, bundle_contents / "Info.plist")

    icon = ROOT / "Keystone.App" / "Resources" / "AppIcon.icns"
    if icon.exists():
        shutil.copy2(icon, bundle_resources / "AppIcon.icns")

    print("\nCopying native dylibs to bundle...")
    if NATIVE_DIR.exists():
        for dylib in NATIVE_DIR.glob("*.dylib"):
            dst = bundle_macos / dylib.name
            shutil.copy2(dylib, dst)
            print(f"  {dylib.name}")

    print("\nCopying engine bun runtime to bundle...")
    engine_bun = ROOT / "bun"
    bundle_bun = bundle_resources / "bun"
    if engine_bun.exists():
        if bundle_bun.exists():
            shutil.rmtree(bundle_bun)
        shutil.copytree(engine_bun, bundle_bun,
                        ignore=shutil.ignore_patterns("node_modules", ".DS_Store"))
        print(f"  bun/ → Resources/bun/")

    print("\nSigning app bundle...")
    run(["codesign", "--force", "--deep", "--sign", "-", str(APP_BUNDLE)])
    run(["xattr", "-dr", "com.apple.quarantine", str(APP_BUNDLE)])
    print("  App signed with ad-hoc signature")

def print_summary():
    print("\n" + "=" * 50)
    print("BUILD COMPLETE")
    print("=" * 50)

    print("\nNative libraries (dylib/native/):")
    if NATIVE_DIR.exists():
        for f in sorted(NATIVE_DIR.glob("*.dylib")):
            print(f"  {f.name}")

    if APP_BUNDLE.exists():
        print(f"\nApp bundle: {APP_BUNDLE}")
        print("\nTo run:")
        print(f"  open '{APP_BUNDLE}'")

def main():
    parser = argparse.ArgumentParser(description="Build Keystone Desktop")
    parser.add_argument("--clean", action="store_true", help="Clean before build")
    parser.add_argument("--rust-only", action="store_true", help="Only build Rust")
    parser.add_argument("--core-only", action="store_true", help="Only build C# engine")
    parser.add_argument("--app-only", action="store_true", help="Only build app bundle")
    parser.add_argument("--no-rust", action="store_true", help="Skip Rust build")
    parser.add_argument("--debug", action="store_true", help="Build in Debug mode")
    args = parser.parse_args()

    os.chdir(ROOT)

    if args.clean:
        clean()

    global APP_OUT, APP_BUNDLE
    if args.debug:
        APP_OUT = app_out_path(debug=True)
        APP_BUNDLE = APP_OUT / f"{APP_NAME}.app"

    if args.rust_only:
        build_rust(debug=args.debug)
    elif args.core_only:
        build_core(debug=args.debug)
    elif args.app_only:
        build_app(debug=args.debug)
    else:
        if not args.no_rust:
            build_rust(debug=args.debug)
        build_core(debug=args.debug)
        build_app(debug=args.debug)

    print_summary()

if __name__ == "__main__":
    main()
