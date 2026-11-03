#!/usr/bin/env bash
# Prepare the game files for the app's data folder from this Mac's Steam install. See tools/prepare_game_files.py.
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
if [[ ! -x tools/python/bin/python3 ]]; then
    echo 'Run ./setup.sh first.' >&2
    exit 1
fi
exec tools/python/bin/python3 tools/prepare_game_files.py "$@"
