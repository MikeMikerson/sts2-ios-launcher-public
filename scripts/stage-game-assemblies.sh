#!/usr/bin/env bash
# Make the assembly the phone would, on the Mac, for checking the game stub against (tools/prepare_game_files.py). The app does
# this itself on the phone (src/STS2Mobile.GamePatcher). Nothing under the installed game is written.
#
#   STS2_GAME_DLL=<installed sts2.dll> scripts/stage-game-assemblies.sh <new folder>
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
out="${1:?Give a new output folder.}"
game_dll="${STS2_GAME_DLL:?Name the installed sts2.dll in STS2_GAME_DLL.}"
mkdir -p "$out"
dotnet run --project tools/FullGamePatcher -- stage "$game_dll" "$out/game" mods/builtin.json > "$out/patch.log" 2>&1
# The stub check reads the adapters' own build.
dotnet build src/STS2Mobile.GameAdapters --nologo -v:q > "$out/adapters-build.log" 2>&1
