#!/usr/bin/env bash
# Launch the app on the iPhone 18 Pro simulator, wait for a line in its log, and leave it running. One command per state.
# It kills a run already there, so not while someone is testing on it. Install with scripts/simulator.sh first.
#
#   scripts/simulator-run.sh <name> <button labels to press, separated by |, or -> <regex to wait for> <timeout s> [ENV=VALUE ...]
#
# The labels go to the launcher as STS2_AUTO_TAP. The log is copied to .work/simlogs/<name>.log.
set -euo pipefail
if (( $# < 4 )); then
    echo 'Usage: scripts/simulator-run.sh <name> <labels|to|press or -> <regex> <timeout s> [ENV=VALUE ...]' >&2
    exit 2
fi
name=$1; taps=$2; pattern=$3; limit=$4; shift 4
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
mkdir -p .work/simlogs
# The simulator and the bundle id of the app scripts/simulator.sh installed there; no .local file is needed.
target="$(tools/python/bin/python3 tools/mono/simulator_app.py --print-target)"
read -r udid bundle <<< "$target"
if ! container="$(xcrun simctl get_app_container "$udid" "$bundle" data 2>/dev/null)"; then
    echo "The app ($bundle) is not installed on the simulator. Run scripts/simulator.sh first." >&2
    exit 1
fi
log="$container/Documents/full-game-startup.log"
xcrun simctl terminate "$udid" "$bundle" >/dev/null 2>&1 || true
sleep 1
rm -f "$log"
envs=()
if [[ $taps != - ]]; then
    envs+=("SIMCTL_CHILD_STS2_AUTO_TAP=$taps")
fi
for pair in "$@"; do envs+=("SIMCTL_CHILD_$pair"); done
env ${envs[@]+"${envs[@]}"} xcrun simctl launch "$udid" "$bundle" >/dev/null
start=$(date +%s); status=1
while (( $(date +%s) - start < limit )); do
    if [[ -f $log ]] && grep -qE "$pattern" "$log"; then
        echo "matched after $(( $(date +%s) - start )) s: $(grep -E "$pattern" "$log" | head -1 | cut -c1-240)"
        status=0; break
    fi
    sleep 1
done
sleep 2
cp "$log" ".work/simlogs/$name.log" 2>/dev/null || true
if (( status != 0 )); then
    echo "TIMEOUT after $limit s waiting for: $pattern"
    tail -5 "$log" 2>/dev/null | cut -c1-220 || true
fi
exit $status
