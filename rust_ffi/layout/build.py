#!/usr/bin/env python3
import subprocess
import sys
import shutil
from pathlib import Path

def build():
    print("Building libkeystone_layout.dylib...")

    result = subprocess.run(
        ["cargo", "build", "--release"],
        cwd=Path(__file__).parent,
    )

    if result.returncode != 0:
        print("Build failed!")
        sys.exit(1)

    # Copy to convenient location
    src = Path(__file__).parent / "../target/release/libkeystone_layout.dylib"
    dst = Path(__file__).parent / "../../dylib/native/libkeystone_layout.dylib"
    dst.parent.mkdir(exist_ok=True)
    shutil.copy2(src, dst)

    print(f"✓ Built: {dst}")
    print(f"✓ Size: {dst.stat().st_size / 1024:.1f} KB")

if __name__ == "__main__":
    build()
