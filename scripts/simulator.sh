#!/usr/bin/env bash
# Make the simulator app from the built device app and install it on the iPhone 18 Pro simulator.
# See tools/mono/simulator_app.py. Output: analysis/full-game-simulator/FullGame.app
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
if [[ ! -x tools/python/bin/python3 ]]; then
    echo 'Run ./setup.sh first.' >&2
    exit 1
fi
exec tools/python/bin/python3 tools/mono/simulator_app.py "$@"
