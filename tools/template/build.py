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
import tarfile
import urllib.request
from pathlib import Path

APP_ROOT = Path(__file__).parent
KEYSTONE_VERSION = "{{KEYSTONE_VERSION}}"
ENGINE_CACHE = Path.home() / ".keystone" / "engines" / KEYSTONE_VERSION


def find_engine() -> Path:
    """Locate the Keystone Desktop engine."""
    # 1. Adjacent source checkout (development)
    local = APP_ROOT.parent / "keystone_desktop"
    if (local / "Keystone.App").exists() or (local / "Keystone.Core").exists():
        return local
    # 2. Vendored in project
    vendored = APP_ROOT / "keystone-desktop"
    if (vendored / "version.txt").exists():
        return vendored
    # 3. Global cache
    if (ENGINE_CACHE / "version.txt").exists():
        return ENGINE_CACHE
    # 4. Auto-download
    print(f"\nKeystone Desktop {KEYSTONE_VERSION} not found — downloading...")
    tarball_name = f"Keystone-{KEYSTONE_VERSION}-arm64.tar.gz"
    url = f"https://github.com/khayzz13/keystone_desktop/releases/download/{KEYSTONE_VERSION}/{tarball_name}"
    ENGINE_CACHE.mkdir(parents=True, exist_ok=True)
    tmp = ENGINE_CACHE.parent / tarball_name
    print(f"  Downloading {url}")
    try:
        urllib.request.urlretrieve(url, tmp)
    except Exception as e:
        print(f"  ERROR: Download failed: {e}")
        print(f"  Download manually and extract to {ENGINE_CACHE}")
        sys.exit(1)
    print(f"  Extracting...")
    with tarfile.open(tmp) as t:
        t.extractall(ENGINE_CACHE.parent)
    tmp.unlink(missing_ok=True)
    extracted = ENGINE_CACHE.parent / "keystone-desktop"
    if extracted.exists() and extracted != ENGINE_CACHE:
        extracted.rename(ENGINE_CACHE)
    print(f"  Engine ready at {ENGINE_CACHE}")
    return ENGINE_CACHE


def main():
    engine = find_engine()
    cli = engine / "tools" / "cli.py"
    if not cli.exists():
        print(f"ERROR: Engine CLI not found at {cli}")
        print(f"Make sure the engine is built or downloaded correctly.")
        sys.exit(1)

    # Map local flags to cli.py command + flags
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

    cmd = [sys.executable, str(cli), str(APP_ROOT), command] + flags
    sys.exit(subprocess.run(cmd).returncode)


if __name__ == "__main__":
    main()
