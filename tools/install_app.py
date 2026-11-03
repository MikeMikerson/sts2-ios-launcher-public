"""Sign the app for your own iPhone and install it there.

`./setup.sh --install` runs this around the build: `configure` before it, so a missing Apple ID or iPhone stops
setup in seconds, and `install` after it. Alone, `tools/python/bin/python3 tools/install_app.py` installs the app
that `scripts/build-mono.sh` last built.

The signing values live in `.local/device.json` (gitignored):

- `team`: the development team, one of the teams signed in to Xcode (Xcode > Settings > Apple Accounts).
- `dev_bundle_id`: the app's bundle identifier. Keep it: a new one installs a second app with no game or saves.
- `device_udid`: the iPhone that the build registers with the team and installs on.
"""

from __future__ import annotations

import argparse
import json
import plistlib
import re
import subprocess
import sys
import tempfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DEVICE_FILE = ROOT / ".local/device.json"
APP = ROOT / "analysis/full-game-mono/FullGame.app"

TEAM_PATTERN = re.compile(r"[A-Z0-9]{10}")


def xcode_teams() -> list[dict]:
    """The teams of the Apple accounts signed in to Xcode, as Xcode last saw them."""
    result = subprocess.run(["defaults", "export", "com.apple.dt.Xcode", "-"], capture_output=True)
    if result.returncode:
        return []
    accounts = plistlib.loads(result.stdout).get("IDEProvisioningTeamByIdentifier", {})
    teams = {}
    for account_teams in accounts.values():
        for team in account_teams:
            if TEAM_PATTERN.fullmatch(str(team.get("teamID", ""))):
                teams[team["teamID"]] = {
                    "id": team["teamID"],
                    "name": str(team.get("teamName", "")),
                    "free": bool(team.get("isFreeProvisioningTeam")),
                }
    return sorted(teams.values(), key=lambda team: (team["free"], team["name"]))


def describe(team: dict) -> str:
    kind = "free Apple ID, 7-day signature" if team["free"] else "paid Apple Developer Program"
    return f"{team['id']}  {team['name']} ({kind})"


def iphones() -> list[dict]:
    """Physical iPhones this Mac can see, with what an install needs from each."""
    with tempfile.TemporaryDirectory() as folder:
        output = Path(folder) / "devices.json"
        result = subprocess.run(
            ["xcrun", "devicectl", "list", "devices", "--json-output", str(output)], capture_output=True, text=True
        )
        if result.returncode or not output.is_file():
            raise ValueError(f"devicectl could not list devices: {result.stderr.strip() or result.stdout.strip()}")
        devices = json.loads(output.read_text())["result"]["devices"]
    phones = []
    for device in devices:
        hardware = device.get("hardwareProperties", {})
        if hardware.get("reality") != "physical" or hardware.get("platform") != "iOS":
            continue
        properties = device.get("deviceProperties", {})
        connection = device.get("connectionProperties", {})
        phones.append(
            {
                "udid": hardware.get("udid", ""),
                "name": properties.get("name", ""),
                "model": hardware.get("marketingName", ""),
                "paired": connection.get("pairingState") == "paired",
                "connected": connection.get("tunnelState") == "connected",
                "developer_mode": properties.get("developerModeStatus"),
            }
        )
    return phones


def choose_team(requested: str | None, configured: str | None) -> str:
    teams = xcode_teams()
    listing = "\n".join(f"  {describe(team)}" for team in teams)
    wanted = requested or configured
    if wanted:
        if teams and wanted not in {team["id"] for team in teams}:
            raise ValueError(f"Team {wanted} is not signed in to Xcode. Teams signed in:\n{listing}")
        return wanted
    if not teams:
        raise ValueError(
            "No Apple ID is signed in to Xcode. Open Xcode > Settings > Apple Accounts, add your Apple ID, "
            "then run this again."
        )
    if len(teams) > 1:
        raise ValueError(f"Xcode has more than one team. Pick one with --team:\n{listing}")
    return teams[0]["id"]


def check_phone(phone: dict) -> None:
    if not phone["paired"]:
        raise ValueError(f"{phone['name']} is not paired. Unlock it, tap Trust on it, and run this again.")
    if phone["developer_mode"] != "enabled":
        raise ValueError(
            f"Developer Mode is off on {phone['name']}. On the iPhone open Settings > Privacy & Security > "
            "Developer Mode, turn it on and restart the iPhone, then run this again."
        )
    if not phone["connected"] and not open_tunnel(phone["udid"]):
        raise ValueError(f"{phone['name']} is paired but not reachable. Unlock it and keep it connected by cable.")


def open_tunnel(udid: str) -> bool:
    """devicectl lists a paired iPhone's tunnel as disconnected until something asks for it.

    Asking for its details opens it, as an install would."""
    try:
        result = subprocess.run(
            ["xcrun", "devicectl", "device", "info", "details", "--device", udid, "--timeout", "60"],
            capture_output=True,
            text=True,
            timeout=90,
        )
    except subprocess.TimeoutExpired:
        return False
    return result.returncode == 0


def choose_phone(requested: str | None, configured: str | None) -> dict:
    phones = iphones()
    wanted = requested or configured
    if wanted:
        for phone in phones:
            if phone["udid"] == wanted:
                return phone
        listing = "\n".join(f"  {phone['udid']}  {phone['name']} ({phone['model']})" for phone in phones) or "  none"
        raise ValueError(f"The iPhone {wanted} is not connected. iPhones this Mac sees:\n{listing}")
    if not phones:
        raise ValueError("No iPhone is connected. Connect it by cable, unlock it, tap Trust, and run this again.")
    if len(phones) > 1:
        listing = "\n".join(f"  {phone['udid']}  {phone['name']} ({phone['model']})" for phone in phones)
        raise ValueError(f"More than one iPhone is connected. Pick one with --device:\n{listing}")
    return phones[0]


def read_values(path: Path = DEVICE_FILE) -> dict:
    return json.loads(path.read_text()) if path.is_file() else {}


def configure(team: str | None = None, device: str | None = None, path: Path = DEVICE_FILE) -> dict:
    """Fill in and save the signing values, and check the iPhone is ready. Values already saved are kept."""
    import xcode_local  # here, not at the top: setup.sh --check imports this file with macOS's older Python

    values = read_values(path)
    values["team"] = choose_team(team, values.get("team"))
    phone = choose_phone(device, values.get("device_udid"))
    check_phone(phone)
    values["device_udid"] = phone["udid"]
    if not values.get("dev_bundle_id"):
        # The same identifier as the generated Xcode project. It is random, so it is free on any team.
        values["dev_bundle_id"] = xcode_local.local_settings()["bundle"]
    xcode_local.validate({"bundle": values["dev_bundle_id"], "team": values["team"]})
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(values, indent=2) + "\n")
    free = next((t["free"] for t in xcode_teams() if t["id"] == values["team"]), False)
    print(
        f"Signing as team {values['team']}{' (free Apple ID)' if free else ''}, bundle id {values['dev_bundle_id']}, "
        f"for {phone['name']} ({phone['model']}). Saved in {path.relative_to(ROOT) if path.is_relative_to(ROOT) else path}.",
        flush=True,
    )
    return values


def install(values: dict | None = None, app: Path = APP) -> None:
    values = values or read_values()
    if not values.get("device_udid"):
        raise ValueError("No iPhone in .local/device.json. Run ./setup.sh --install.")
    if not (app / "Info.plist").is_file():
        raise ValueError(f"No built app at {app.relative_to(ROOT)}. Run ./setup.sh --install.")
    bundle = plistlib.loads((app / "Info.plist").read_bytes()).get("CFBundleIdentifier")
    if bundle != values.get("dev_bundle_id"):
        raise ValueError(
            f"The built app's bundle id is {bundle}, not {values.get('dev_bundle_id')} from .local/device.json. "
            "Rebuild with scripts/build-mono.sh, or run ./setup.sh --install."
        )
    phone = choose_phone(values["device_udid"], None)
    check_phone(phone)
    print(f"Installing on {phone['name']}...", flush=True)
    result = subprocess.run(
        ["xcrun", "devicectl", "device", "install", "app", "--device", phone["udid"], str(app)],
        capture_output=True,
        text=True,
    )
    if result.returncode:
        message = (result.stderr.strip() or result.stdout.strip()).splitlines()
        hint = ""
        text = " ".join(message)
        if "maximum number of installed apps" in text or "0xe8008021" in text.lower():
            hint = (
                "\nA free Apple ID can have 3 of its own apps on the iPhone at once. "
                "Delete one of them from the iPhone and run this again."
            )
        raise ValueError("The install failed:\n  " + "\n  ".join(message[-6:]) + hint)
    print(
        f"Installed {bundle} on {phone['name']}. Your game and saves on the iPhone are kept across reinstalls.",
        flush=True,
    )


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--configure-only", action="store_true", help="save the signing values and stop")
    parser.add_argument("--team", help="the development team to sign with, when Xcode has more than one")
    parser.add_argument("--device", help="the iPhone's UDID, when more than one is connected")
    options = parser.parse_args()
    if options.configure_only or options.team or options.device or not DEVICE_FILE.is_file():
        values = configure(options.team, options.device)
        if options.configure_only:
            return
    else:
        values = read_values()
    install(values)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Install stopped: {error}", file=sys.stderr)
        sys.exit(1)
