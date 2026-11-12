"""Profile names that tie each staged output to the step and game version that made it.

A tool accepts another tool's output only under the profile it expects, so a change to what a step
writes needs a new profile name here.
"""

import json
from pathlib import Path

ASSET_PROFILE = "v0.113.0-full-assets-v1"
TEXTURE_PROFILE = "v0.113.0-textures-astc4x4-v1"
SCENE_PROFILE = "v0.113.0-phone-scenes-v1"


def load_asset_report(assets: Path) -> dict:
    """The report tools/stage_full_game.py wrote into an asset staging tree."""
    report = json.loads((assets / "asset-report.json").read_text())
    if report.get("profile") != ASSET_PROFILE:
        raise ValueError("Unsupported asset profile")
    return report
