#!/usr/bin/env python3
"""
{{APP_NAME}} — Build Script

Usage:
  python3 build.py          # Build (install deps, vendor engine, compile C# if present)
  python3 build.py --run    # Build and run in dev mode
  python3 build.py --package  # Package into distributable .app
  python3 build.py --clean  # Clean build artifacts
  python3 build.py --debug  # Debug mode (combine with --run or --package)
"""

import subprocess
import sys
from pathlib import Path

APP_ROOT = Path(__file__).parent


def load_build_yaml() -> dict:
    path = APP_ROOT / "keystone.build.yaml"
    if not path.exists():
        return {}
    try:
        import yaml
        return yaml.safe_load(path.read_text()) or {}
    except ImportError:
        pass
    # Minimal key: value parser (no nesting needed here)
    result = {}
    for line in path.read_text().splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        if ":" in stripped and not line[0].isspace():
            key, _, val = stripped.partition(":")
            val = val.strip().strip('"').strip("'")
            if val:
                result[key.strip()] = val
    return result


def find_engine(cfg: dict) -> Path:
    fw = cfg.get("framework_directory")
    if fw:
        p = (APP_ROOT / fw).resolve()
        if p.exists():
            return p
        print(f"ERROR: framework_directory '{fw}' not found (resolved: {p})")
        sys.exit(1)

    print("ERROR: framework_directory not set in keystone.build.yaml")
    sys.exit(1)


def find_app_root(cfg: dict) -> Path:
    app = cfg.get("app_directory", ".")
    return (APP_ROOT / app).resolve()


def main():
    cfg = load_build_yaml()
    engine = find_engine(cfg)
    app_root = find_app_root(cfg)

    cli = engine / "tools" / "cli.py"
    if not cli.exists():
        print(f"ERROR: Engine CLI not found at {cli}")
        sys.exit(1)

    args = sys.argv[1:]
    if "--clean" in args:
        command = "clean"
        flags = [a for a in args if a != "--clean"]
    elif "--package" in args:
        command = "package"
        flags = [a for a in args if a != "--package"]
    elif "--run" in args:
        command = "run"
        flags = [a for a in args if a != "--run"]
    else:
        command = "build"
        flags = args

    cmd = [sys.executable, str(cli), str(app_root), command] + flags
    sys.exit(subprocess.run(cmd).returncode)


if __name__ == "__main__":
    main()
