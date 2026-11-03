#!/bin/sh
set -eu
repo_root="$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)"
cd "$repo_root"
if ! tools/python/bin/python3 tools/full_game_build_inputs.py verify analysis/full-game-ios/build-inputs.json; then
    echo 'error: Prepared game inputs are stale or missing. Run ./setup.sh from the repository, then build again.' >&2
    exit 1
fi
