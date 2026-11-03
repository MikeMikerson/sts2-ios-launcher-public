#!/usr/bin/env bash
set -euo pipefail
repo_root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$repo_root"
python=tools/python/bin/python3
export PATH="$repo_root/tools/python/bin:$repo_root/tools/ios-dotnet:$PATH"
# The same settings as tools/dotnet_environment.py: no telemetry, no first-run text.
export DOTNET_ROOT="$repo_root/tools/ios-dotnet" DOTNET_CLI_UI_LANGUAGE=en
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 DOTNET_GENERATE_ASPNET_CERTIFICATE=false
export NUGET_PACKAGES="$repo_root/.tools/nuget/packages" DOTNET_CLI_HOME="$repo_root/.tools/dotnet-home"
export PYTHONPATH="$repo_root/tools"
mkdir -p .work/logs
if ! "$python" -m unittest discover -s tests -p 'test_*.py' > .work/logs/python-tests.log 2>&1; then
    echo 'Python tests failed. See .work/logs/python-tests.log' >&2
    exit 1
fi
for project in STS2Mobile.Steam.Tests STS2Mobile.Platform.Tests ModWeaver.Tests GamePatcher.Tests GamePack.Tests; do
    log=".work/logs/$project.log"
    if ! dotnet test "tests/$project" > "$log" 2>&1; then
        echo "$project failed. See $log" >&2
        exit 1
    fi
    grep -E '^(Passed|Failed)!' "$log"
done
# A console app, so it can also run under NativeAOT (see its project file).
log=.work/logs/SteamWire.Tests.log
if ! dotnet run --project tests/SteamWire.Tests > "$log" 2>&1; then
    echo "SteamWire.Tests failed. See $log" >&2
    exit 1
fi
tail -1 "$log"
echo 'Tests passed. Logs: .work/logs'
