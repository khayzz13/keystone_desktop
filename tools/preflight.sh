#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "==> Python syntax checks"
python3 -m py_compile tools/package.py tools/cli.py

if command -v bun >/dev/null 2>&1; then
  echo "==> Bun typecheck"
  TMPDIR="${TMPDIR:-/tmp}" bun x tsc --noEmit -p bun/tsconfig.json
else
  echo "==> bun not found, skipping Bun typecheck"
fi

echo "==> Preflight complete"
