"""Stage the pinned full asset pack separately, verify all entries, and audit GDScript identifiers."""

import argparse
import hashlib
import json
import shutil
from pathlib import Path
from typing import BinaryIO

from godot_pack import PackEntry, gdscript_identifiers, read_directory, read_entry
from hashing import sha256_file
from profiles import ASSET_PROFILE

DEPENDENCIES = Path(__file__).resolve().parent / "dependencies.json"
# The pinned pack's entry count; with the hash pinned, any other count means the directory was misread.
GAME_PACK_ENTRIES = 13576
BLOCKED = {
    "HTTPRequest",
    "HTTPClient",
    "WebSocketPeer",
    "TCPServer",
    "StreamPeerTCP",
    "UDPServer",
    "PacketPeerUDP",
    "shell_open",
    "load_resource_pack",
}


def supported_pack_sha256() -> str:
    return json.loads(DEPENDENCIES.read_text())["game"]["pack_sha256"]


def audit_script(name: str, data: bytes) -> dict:
    identifiers = gdscript_identifiers(data)
    violations = sorted(set(identifiers) & BLOCKED)
    if violations:
        raise ValueError(f"Unexpected GDScript network/pack API: {name}: {', '.join(violations)}")
    return {"path": name, "sha256": hashlib.sha256(data).hexdigest(), "identifiers": identifiers}


def write_entries(stream: BinaryIO, entries: list[PackEntry], output: Path) -> tuple[list, list, list]:
    """Write every entry except the reporting SDK into output. Returns the staged, removed and script lists."""
    staged, removed, scripts = [], [], []
    for entry in entries:
        data = read_entry(stream, entry)
        if entry.name.startswith("addons/sentry/"):
            removed.append(entry.name)
            continue
        if entry.name.endswith(".gdc"):
            scripts.append(audit_script(entry.name, data))
        target = output / entry.name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(data)
        staged.append({"path": entry.name, "sha256": hashlib.sha256(data).hexdigest()})
    return staged, removed, scripts


def stage(pack: Path, output: Path) -> dict:
    pack, output = pack.resolve(), output.resolve()
    if output.exists() or output.is_relative_to(pack.parent):
        raise ValueError("Use a new directory outside the installed game")
    digest = sha256_file(pack)
    if digest != supported_pack_sha256():
        raise ValueError(f"Unsupported full game pack SHA-256: {digest}")
    with pack.open("rb") as stream:
        entries = read_directory(stream)
        if len(entries) != GAME_PACK_ENTRIES:
            raise ValueError("Unsupported pack entry count")
        output.mkdir(parents=True)
        try:
            staged, removed, scripts = write_entries(stream, entries, output)
        except Exception:
            shutil.rmtree(output)
            raise
    report = {
        "profile": ASSET_PROFILE,
        "packSha256": digest,
        "staged": staged,
        "removed": removed,
        "scripts": scripts,
        "executionApproved": False,
        "limitations": (
            "The identifier check covers the known direct GDScript networking APIs only. "
            "Native and indirect calls are not checked."
        ),
    }
    (output / "asset-report.json").write_text(json.dumps(report, indent=2) + "\n")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--pack",
        type=Path,
        default=Path.home()
        / "Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
        / "SlayTheSpire2.app/Contents/Resources/Slay the Spire 2.pck",
    )
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    try:
        report = stage(options.pack, options.output)
    except ValueError as error:
        raise SystemExit(str(error)) from None
    print(
        f"Verified {len(report['staged'])} staged entries and {len(report['scripts'])} GDScripts; "
        f"removed {len(report['removed'])} Sentry entries."
    )


if __name__ == "__main__":
    main()
