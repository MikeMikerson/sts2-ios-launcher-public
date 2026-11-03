#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
godot_editor="${STS2_GODOT_EDITOR:-$repo_root/.tools/godot/Godot_mono.app/Contents/MacOS/Godot}"
if [[ ! -x "$godot_editor" ]]; then
    echo "Godot 4.5.1 .NET is missing. Run ./setup.sh." >&2
    exit 1
fi
exec "$godot_editor" "$@"
