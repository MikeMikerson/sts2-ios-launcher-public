"""Write the AltStore and SideStore source for a released IPA.

The release workflow (.github/workflows/build-ipa.yml) runs this on the unsigned IPA it built. Everything that describes the
app (bundle id, version, minimum iOS, the privacy texts AltStore checks against the app) comes from the IPA's Info.plist,
so the source cannot drift from the build. The download and icon URLs are the release's assets; the icon is the one
setup generates (src/STS2Mobile.FullGame/icon.png), since the IPA's own icons are in Apple's altered PNG form.
"""

from __future__ import annotations

import argparse
import datetime
import json
import plistlib
import re
import zipfile
from pathlib import Path

APP_NAME = "STS2 Mobile"
DESCRIPTION = (
    "An unofficial iPhone launcher for Slay the Spire 2. It holds none of the game: sign in to Steam with an account "
    "that owns the game, and the app downloads it from Steam and prepares it on the phone. Unofficial, not made or "
    "endorsed by Mega Crit."
)


def app_folder(names: list[str]) -> str:
    folders = {match.group(1) for name in names if (match := re.match(r"(Payload/[^/]+\.app)/", name))}
    if len(folders) != 1:
        raise ValueError("The IPA must hold exactly one app under Payload/.")
    return folders.pop()


def read_info(ipa: Path) -> dict:
    with zipfile.ZipFile(ipa) as archive:
        return plistlib.loads(archive.read(f"{app_folder(archive.namelist())}/Info.plist"))


def make_source(info: dict, *, size: int, download_url: str, icon_url: str, website: str, date: str, notes: str) -> dict:
    bundle = info["CFBundleIdentifier"]
    if "$(" in bundle or "$(" in info.get("CFBundleShortVersionString", ""):
        raise ValueError("The IPA's Info.plist still holds unexpanded build settings.")
    privacy = {key: value for key, value in info.items() if key.startswith("NS") and key.endswith("UsageDescription")}
    version = {
        "version": info["CFBundleShortVersionString"],
        "buildVersion": info["CFBundleVersion"],
        "date": date,
        "localizedDescription": notes,
        "downloadURL": download_url,
        "size": size,
        "minOSVersion": info.get("MinimumOSVersion", "15.0"),
    }
    return {
        "name": APP_NAME,
        "identifier": f"{bundle}.source",
        "subtitle": "Unofficial launcher for Slay the Spire 2",
        "website": website,
        "iconURL": icon_url,
        "apps": [
            {
                "name": APP_NAME,
                "bundleIdentifier": bundle,
                "developerName": "STS2 Mobile contributors",
                "subtitle": "Play your Steam copy of Slay the Spire 2 on iPhone",
                "localizedDescription": DESCRIPTION,
                "iconURL": icon_url,
                "category": "games",
                "versions": [version],
                # AltStore refuses an install when these differ from the app's own.
                "appPermissions": {"entitlements": [], "privacy": privacy},
            }
        ],
        "news": [],
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ipa", type=Path)
    parser.add_argument("--download-url", required=True)
    parser.add_argument("--icon-url", required=True)
    parser.add_argument("--website", required=True)
    parser.add_argument("--notes", default="")
    parser.add_argument("--output", type=Path, required=True, help="the source JSON to write")
    arguments = parser.parse_args()
    info = read_info(arguments.ipa)
    source = make_source(
        info,
        size=arguments.ipa.stat().st_size,
        download_url=arguments.download_url,
        icon_url=arguments.icon_url,
        website=arguments.website,
        date=datetime.date.today().isoformat(),
        notes=arguments.notes,
    )
    arguments.output.write_text(json.dumps(source, indent=2) + "\n")
    print(f"{arguments.output}: {source['apps'][0]['bundleIdentifier']} {source['apps'][0]['versions'][0]['version']}")


if __name__ == "__main__":
    main()
