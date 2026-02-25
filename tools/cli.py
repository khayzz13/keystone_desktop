#!/usr/bin/env python3
"""
Keystone Desktop — Build CLI

Engine-side build tooling. Called by the thin build.py shim in each app directory.
Reads keystone.build.yaml for build-time config, keystone.config.json for runtime config.

Usage (called by app's build.py, not directly):
  python3 <engine>/tools/cli.py <app_root> build
  python3 <engine>/tools/cli.py <app_root> run [--debug]
  python3 <engine>/tools/cli.py <app_root> package [--debug] [--dmg] [--mode bundled|side-by-side]
  python3 <engine>/tools/cli.py <app_root> clean
"""

import subprocess
import os
import sys
import json
import re
import shutil
import argparse
import tarfile
import urllib.request
from pathlib import Path

SCRIPT_DIR = Path(__file__).parent
ENGINE_ROOT = SCRIPT_DIR.parent

_version_file = ENGINE_ROOT / "version.txt"
ENGINE_VERSION = _version_file.read_text().strip() if _version_file.exists() else "0.1.0"


def run(cmd, cwd=None, check=True):
    print(f"  $ {' '.join(str(c) for c in cmd) if isinstance(cmd, list) else cmd}")
    return subprocess.run(cmd, cwd=cwd, check=check, shell=isinstance(cmd, str))


# ─── YAML loading (minimal, no external deps) ────────────────────────────────

def load_build_yaml(app_root: Path) -> dict:
    """Load keystone.build.yaml. Returns empty dict if absent."""
    path = app_root / "keystone.build.yaml"
    if not path.exists():
        return {}

    try:
        import yaml
        return yaml.safe_load(path.read_text()) or {}
    except ImportError:
        pass

    # Fallback: minimal YAML parser for flat/simple configs
    return _parse_simple_yaml(path.read_text())


def _parse_simple_yaml(text: str) -> dict:
    """Parse simple YAML (flat keys, no nesting beyond one level). Good enough for build config."""
    result = {}
    current_section = None
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        # Top-level key with value
        if not line[0].isspace() and ":" in stripped:
            key, _, val = stripped.partition(":")
            key = key.strip()
            val = val.strip()
            if val:
                result[key] = _yaml_value(val)
            else:
                result[key] = {}
                current_section = key
        elif current_section and line[0].isspace() and ":" in stripped:
            key, _, val = stripped.partition(":")
            result[current_section][key.strip()] = _yaml_value(val.strip())
    return result


def _yaml_value(s: str):
    """Convert a YAML scalar string to Python type."""
    if not s or s == '""' or s == "''":
        return ""
    # Strip quotes
    if (s.startswith('"') and s.endswith('"')) or (s.startswith("'") and s.endswith("'")):
        return s[1:-1]
    if s.lower() == "true":
        return True
    if s.lower() == "false":
        return False
    if s.lower() in ("null", "~"):
        return None
    try:
        return int(s)
    except ValueError:
        pass
    try:
        return float(s)
    except ValueError:
        pass
    # Strip inline comments
    if " #" in s:
        s = s[:s.index(" #")].strip()
    return s


def load_runtime_config(app_root: Path) -> dict:
    """Load keystone.config.json (JSONC supported)."""
    for name in ["keystone.config.json", "keystone.json"]:
        path = app_root / name
        if path.exists():
            text = path.read_text()
            text = re.sub(r'^\s*//.*$', '', text, flags=re.MULTILINE)
            return json.loads(text)
    return {}


# ─── Engine discovery ─────────────────────────────────────────────────────────

def find_engine(build_cfg: dict) -> Path:
    """Locate the Keystone Desktop engine. Currently: vendored adjacent to app (source checkout)."""
    # 1. Source checkout — this script is inside the engine
    if (ENGINE_ROOT / "Keystone.App").exists() or (ENGINE_ROOT / "Keystone.Core").exists():
        return ENGINE_ROOT

    # 2. Global cache
    version = build_cfg.get("engine", ENGINE_VERSION)
    cache = Path.home() / ".keystone" / "engines" / version
    if (cache / "version.txt").exists():
        return cache

    # 3. Auto-download
    print(f"\nKeystone Desktop {version} not found — downloading...")
    _download_engine(version, cache)
    return cache


def _download_engine(version: str, dest: Path):
    tarball_name = f"Keystone-{version}-arm64.tar.gz"
    url = f"https://github.com/khayzz13/keystone_desktop/releases/download/{version}/{tarball_name}"
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


# ─── Build steps ──────────────────────────────────────────────────────────────

def vendor_engine_bun(app_root: Path, engine: Path, bun_root: str = "bun"):
    """Copy engine bun runtime + SDK into app's node_modules as real files."""
    engine_bun = engine / "bun"
    bun_dir = app_root / bun_root
    if not engine_bun.exists() or not bun_dir.exists():
        return

    nm = bun_dir / "node_modules"
    nm.mkdir(parents=True, exist_ok=True)

    # keystone-desktop -> engine/bun/ (host.ts, types.ts, lib/, sdk/, etc.)
    dst_kd = nm / "keystone-desktop"
    if dst_kd.exists():
        shutil.rmtree(dst_kd)
    shutil.copytree(engine_bun, dst_kd, ignore=shutil.ignore_patterns(
        "node_modules", ".bun", "bun.lock", "tsconfig.json"))
    print(f"  Vendored keystone-desktop -> {dst_kd.relative_to(app_root)}")

    # @keystone/sdk -> engine/bun/sdk/
    dst_sdk = nm / "@keystone" / "sdk"
    if dst_sdk.exists():
        shutil.rmtree(dst_sdk)
    dst_sdk.parent.mkdir(parents=True, exist_ok=True)
    shutil.copytree(engine_bun / "sdk", dst_sdk)
    print(f"  Vendored @keystone/sdk -> {dst_sdk.relative_to(app_root)}")

    # @keystone/lib -> engine/bun/lib/ (SDK imports ../lib/store relative to sdk/)
    engine_lib = engine_bun / "lib"
    if engine_lib.exists():
        dst_lib = nm / "@keystone" / "lib"
        if dst_lib.exists():
            shutil.rmtree(dst_lib)
        shutil.copytree(engine_lib, dst_lib)


def build_app_assembly(app_root: Path, engine: Path):
    """Build the optional C# app assembly if app/ contains a .csproj."""
    app_dir = app_root / "app"
    if not app_dir.exists() or not any(app_dir.glob("*.csproj")):
        return

    print(f"\n=== Building App Assembly ===")
    csproj = next(app_dir.glob("*.csproj"))

    # Resolve {{ENGINE_REL}} placeholder in csproj if still present.
    # Computes the relative path from the csproj directory to the engine root.
    content = csproj.read_text()
    if "{{ENGINE_REL}}" in content:
        rel = os.path.relpath(engine.resolve(), csproj.parent.resolve())
        # Normalize to forward slashes for MSBuild compatibility
        rel = rel.replace("\\", "/")
        content = content.replace("{{ENGINE_REL}}", rel)
        csproj.write_text(content)
        print(f"  Resolved {{{{ENGINE_REL}}}} → {rel}")

    run(["dotnet", "build", str(csproj), "-c", "Release"])


def setup_bun(app_root: Path, engine: Path, bun_root: str = "bun"):
    """Install bun dependencies and vendor engine runtime."""
    bun_dir = app_root / bun_root
    if not (bun_dir / "package.json").exists():
        return

    print(f"\n=== Installing Bun Dependencies ===")
    run(["bun", "install"], cwd=bun_dir)
    vendor_engine_bun(app_root, engine, bun_root)


def find_engine_binary(engine: Path) -> Path:
    """Find the Keystone.App binary from the engine."""
    # Distributed layout
    simple = engine / "bin" / "Keystone.App"
    if simple.exists():
        return simple
    # Source checkout
    for mode in ["Release", "Debug"]:
        src = (engine / "Keystone.App" / "bin" / mode / "net10.0-macos" / "osx-arm64"
               / "Keystone.app" / "Contents" / "MacOS" / "Keystone.App")
        if src.exists():
            return src
    return None


def clean(app_root: Path, build_cfg: dict = {}):
    print("\n=== Cleaning ===")
    for csproj in app_root.rglob("*.csproj"):
        proj_dir = csproj.parent
        for name in ["bin", "obj"]:
            d = proj_dir / name
            if d.exists():
                shutil.rmtree(d)
                print(f"  Removed {proj_dir.name}/{name}/")
    dylib_dir = app_root / build_cfg.get("dylib_directory", "dylib")
    if dylib_dir.exists():
        for f in dylib_dir.glob("*.dll"):
            f.unlink()
            print(f"  Removed {f.name}")
    pkg = build_cfg.get("package", {}) if isinstance(build_cfg.get("package"), dict) else {}
    out_dir_name = pkg.get("out_directory", build_cfg.get("outDir", "dist"))
    dist_dir = app_root / out_dir_name
    if dist_dir.exists():
        shutil.rmtree(dist_dir)
        print(f"  Removed {out_dir_name}/")


# ─── Commands ─────────────────────────────────────────────────────────────────

def resolve_bun_root(build_cfg: dict, runtime_cfg: dict) -> str:
    """bun_directory in build yaml > bun.root in runtime config > 'bun'."""
    if build_cfg.get("bun_directory"):
        return build_cfg["bun_directory"]
    bun = runtime_cfg.get("bun", {})
    return bun.get("root", "bun") if isinstance(bun, dict) else "bun"


def resolve_dylib_dir(build_cfg: dict, runtime_cfg: dict) -> str:
    """dylib_directory in build yaml > plugins.dir in runtime config > 'dylib'."""
    if build_cfg.get("dylib_directory"):
        return build_cfg["dylib_directory"]
    plugins = runtime_cfg.get("plugins", {})
    return plugins.get("dir", "dylib") if isinstance(plugins, dict) else "dylib"


def cmd_build(app_root: Path, build_cfg: dict, runtime_cfg: dict):
    """Build step: compile C# assembly + install bun deps + vendor engine."""
    engine = find_engine(build_cfg)
    bun_root = resolve_bun_root(build_cfg, runtime_cfg)

    build_app_assembly(app_root, engine)
    setup_bun(app_root, engine, bun_root)

    return engine


def cmd_run(app_root: Path, build_cfg: dict, runtime_cfg: dict):
    """Build and run in dev mode."""
    engine = cmd_build(app_root, build_cfg, runtime_cfg)
    binary = find_engine_binary(engine)
    if binary is None:
        print(f"  ERROR: Engine binary not found. Build the engine first.")
        sys.exit(1)

    name = runtime_cfg.get("name", app_root.name)
    print(f"\n=== Running {name} ===")
    env = os.environ.copy()
    env["KEYSTONE_ROOT"] = str(app_root)

    # Surface dev_extensions_directory to the runtime so it can load 3rd-party plugin dev builds
    plugins_build = build_cfg.get("plugins", {})
    if isinstance(plugins_build, dict) and plugins_build.get("dev_extensions_directory"):
        ext_dir = Path(plugins_build["dev_extensions_directory"]).expanduser().resolve()
        env["KEYSTONE_DEV_EXTENSIONS"] = str(ext_dir)

    subprocess.run([str(binary)], cwd=app_root, env=env)


def cmd_package(app_root: Path, build_cfg: dict, runtime_cfg: dict,
                mode=None, dmg=False, allow_external=False):
    """Build and package into distributable .app bundle."""
    engine = cmd_build(app_root, build_cfg, runtime_cfg)
    packager = engine / "tools" / "package.py"
    if not packager.exists():
        print(f"  ERROR: Packager not found at {packager}")
        sys.exit(1)

    # CLI flags override; build yaml is read directly by package.py
    cmd = [sys.executable, str(packager), str(app_root), "--engine", str(engine)]
    if mode:
        cmd += ["--mode", mode]
    if allow_external:
        cmd.append("--allow-external")
    if dmg:
        cmd.append("--dmg")
    run(cmd)


def cmd_clean(app_root: Path, build_cfg: dict = {}):
    clean(app_root, build_cfg)


# ─── CLI ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Keystone Desktop Build CLI")
    parser.add_argument("app_root", type=str, help="Path to the app directory")
    parser.add_argument("command", choices=["build", "run", "package", "clean"],
                        help="Command to run")
    parser.add_argument("--mode", choices=["side-by-side", "bundled"],
                        help="Override plugins.mode for packaging")
    parser.add_argument("--dmg", action="store_true", help="Create DMG")
    parser.add_argument("--allow-external", action="store_true",
                        help="Override plugins.allow_external_signatures")

    args = parser.parse_args()
    app_root = Path(args.app_root).resolve()

    if not app_root.exists():
        print(f"ERROR: App directory not found: {app_root}")
        sys.exit(1)

    build_cfg = load_build_yaml(app_root)
    runtime_cfg = load_runtime_config(app_root)

    if args.command == "clean":
        cmd_clean(app_root, build_cfg)
    elif args.command == "build":
        cmd_build(app_root, build_cfg, runtime_cfg)
    elif args.command == "run":
        cmd_run(app_root, build_cfg, runtime_cfg)
    elif args.command == "package":
        cmd_package(app_root, build_cfg, runtime_cfg,
                    mode=args.mode, dmg=args.dmg,
                    allow_external=args.allow_external)


if __name__ == "__main__":
    main()
