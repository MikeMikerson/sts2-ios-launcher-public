#!/usr/bin/env bash
# Build the Mono app from a tree where ./setup.sh has run, and check its output.
# See tools/mono/build_mono.py. Logs: .work/logs/mono-*.log. Output: analysis/full-game-mono/FullGame.app
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
if [[ ! -x tools/python/bin/python3 ]]; then
    echo 'Run ./setup.sh first.' >&2
    exit 1
fi
exec tools/python/bin/python3 tools/mono/build_mono.py "$@"
