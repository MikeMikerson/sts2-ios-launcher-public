#!/usr/bin/env bash
# Stage SteamKit2 with the static serializers the app ships: inventory the pinned contracts, prove the committed model
# matches, and stage both target framework variants. No game file takes part.
#
#   scripts/stage-steam.sh <new folder>
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
out="${1:?Give a new output folder.}"
# The repository's own package folder, as setup and the tests use, never the user's ~/.nuget.
export NUGET_PACKAGES="${NUGET_PACKAGES:-$repo_root/.tools/nuget/packages}"
steamkit="$NUGET_PACKAGES/steamkit2/3.4.0/lib"
mkdir -p "$out"
dotnet restore src/STS2Mobile.Steam/STS2Mobile.Steam.csproj --nologo > "$out/steam-restore.log" 2>&1
dotnet run --project tools/SteamAotPatcher -- model-roots "$steamkit/net10.0/SteamKit2.dll" "$out/steam-aot-roots.json" > "$out/steam-roots.log" 2>&1
tools/python/bin/python3 tools/generate_steam_wire_model.py "$out/steam-aot-roots.json" --check
dotnet build src/STS2Mobile.SteamWire --nologo -v:q > "$out/steam-wire-build.log" 2>&1
# The host targets net9.0 on desktop and net10.0 on iOS; SteamKit2 3.4.0 ships net8.0 and net10.0.
for pair in net9.0:net8.0 net10.0:net10.0; do
    target="${pair%%:*}"
    package="${pair##*:}"
    dotnet run --project tools/SteamAotPatcher -- stage "$steamkit/$package/SteamKit2.dll" \
        "$out/steam/$target/SteamKit2.dll" "src/STS2Mobile.SteamWire/bin/Debug/$target/STS2Mobile.SteamWire.dll" \
        > "$out/steam-stage-$target.log" 2>&1
done
