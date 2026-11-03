#!/usr/bin/env bash
# Bootstrap a private Python environment, then prepare the iOS project.
set -euo pipefail
repo_root="$(cd "$(dirname "$0")" && pwd)"
cd "$repo_root"
case "${1:-}" in
  --help|-h|--check) exec /usr/bin/python3 tools/setup_ios.py "$@" ;;
esac
if [[ "$(uname -s)" != Darwin || "$(uname -m)" != arm64 ]]; then
    echo 'Setup requires an Apple silicon Mac.' >&2
    exit 1
fi
mkdir -p .tools/cache .tools/uv .work/logs
uv_archive=.tools/cache/uv-0.9.7-aarch64-apple-darwin.tar.gz
uv_hash=35572b9619fc14d67fc1cd72582c3cfc5c9c66d97f310192e04f26fb3fe96005
if [[ ! -f "$uv_archive" ]]; then
    curl --fail --location --retry 3 --proto '=https' --tlsv1.2 \
        https://github.com/astral-sh/uv/releases/download/0.9.7/uv-aarch64-apple-darwin.tar.gz \
        --output "$uv_archive.partial"
    mv "$uv_archive.partial" "$uv_archive"
fi
actual_hash="$(shasum -a 256 "$uv_archive" | cut -d ' ' -f 1)"
if [[ "$actual_hash" != "$uv_hash" ]]; then
    echo 'Python bootstrap archive checksum mismatch. Remove .tools/cache/uv-0.9.7-aarch64-apple-darwin.tar.gz and retry.' >&2
    exit 1
fi
# Re-extract the verified executable, rather than trusting an existing executable.
tar -xzf "$uv_archive" -C .tools/uv --strip-components=1
export UV_PYTHON_INSTALL_DIR="$repo_root/.tools/python" UV_CACHE_DIR="$repo_root/.tools/uv-cache"
export UV_PYTHON_PREFERENCE=only-managed UV_NO_MODIFY_PATH=1
.tools/uv/uv python install 3.13.9 > .work/logs/python-setup.log 2>&1
if [[ ! -x tools/python/bin/python3 ]]; then
    .tools/uv/uv venv --python 3.13.9 tools/python >> .work/logs/python-setup.log 2>&1
fi
.tools/uv/uv pip sync --require-hashes --only-binary :all: --python tools/python/bin/python3 tools/requirements.txt >> .work/logs/python-setup.log 2>&1
exec tools/python/bin/python3 tools/setup_ios.py "$@"
