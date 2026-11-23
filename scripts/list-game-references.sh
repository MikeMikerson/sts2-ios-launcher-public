#!/usr/bin/env bash
# Record game members referenced by the host and adapters so preparation can check compatibility.
#
#   scripts/list-game-references.sh          write the list (./setup.sh runs this)
#   scripts/list-game-references.sh --check  fail when the list is not the one the current sources make (scripts/test.sh runs this)
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
list=.tools/game-references.txt
log=.work/logs/game-references.log
mkdir -p .work/logs .tools
tool=tools/mono/MonoBuildTool/bin/Debug/net10.0/MonoBuildTool
dlls=(
    src/STS2Mobile.GameHost/.godot/mono/temp/bin/Debug/STS2Mobile.GameHost.dll
    src/STS2Mobile.GameAdapters/bin/Debug/net9.0/STS2Mobile.GameAdapters.dll
)
{
    dotnet build src/STS2Mobile.GameHost --nologo -v:q &&
    dotnet build src/STS2Mobile.GameAdapters -f net9.0 --nologo -v:q &&
    dotnet build tools/mono/MonoBuildTool --nologo -v:q
} > "$log" 2>&1 || { echo "Building the host for its game references failed. See $log" >&2; exit 1; }
fresh="$(mktemp "${TMPDIR:-/tmp}/game-references.XXXXXX")"
trap 'rm -f "$fresh"' EXIT
"$tool" list-game-references "${dlls[@]}" > "$fresh"
if [[ "${1:-}" == --check ]]; then
    if [[ ! -f "$list" ]]; then
        echo "$list is missing: run ./setup.sh --prepare-only." >&2
        exit 1
    fi
    if ! cmp -s "$fresh" "$list"; then
        echo "$list is stale: the host's game references changed. Run ./setup.sh --prepare-only. Differences:" >&2
        diff "$list" "$fresh" | head -20 >&2 || true
        exit 1
    fi
    echo "Game references listed: $(grep -cv '^#' "$list") (current)."
else
    mv "$fresh" "$list"
    trap - EXIT
fi
