"""Preserve local Xcode signing choices and guard the generated project against stale inputs."""

import argparse
import json
import re
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SETTINGS = ROOT / ".local/signing.json"
PROJECT = ROOT / "analysis/full-game-ios/export/FullGame.xcodeproj/project.pbxproj"
# The "Verify prepared game" build phase. Its prefix is used by no other id in the project: the Godot template's
# ids, and the repeated-digit ids tools/prepare_full_game_xcode.py gives host sources (B5555..., C1111...).
PHASE_ID = "9E81F1ED0000000000000001"
PHASE_MARKER = f"{PHASE_ID} /* Verify prepared game */"


def validate(settings: dict) -> dict:
    if not re.fullmatch(r"[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+", settings["bundle"]):
        raise ValueError("Invalid local bundle identifier")
    if not re.fullmatch(r"[A-Z0-9]{0,10}", settings["team"]):
        raise ValueError("Invalid local development team")
    return settings


def capture(text: str, destination: Path = SETTINGS) -> None:
    bundles = set(re.findall(r'PRODUCT_BUNDLE_IDENTIFIER = "?([A-Za-z0-9.-]+)"?;', text))
    teams = set(re.findall(r'DEVELOPMENT_TEAM = "?([A-Z0-9]*)"?;', text))
    if len(bundles) != 1 or len(teams) > 1:
        raise ValueError("Use the same bundle identifier and signing team for both Xcode configurations")
    settings = validate({"bundle": bundles.pop(), "team": next(iter(teams), "")})
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_text(json.dumps(settings, indent=2) + "\n")


def local_settings(path: Path = SETTINGS) -> dict:
    if path.exists():
        return validate(json.loads(path.read_text()))
    settings = {"bundle": f"org.example.sts2ios.{uuid.uuid4().hex[:12]}", "team": ""}
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(settings, indent=2) + "\n")
    return settings


def configure_project(text: str, settings: dict | None = None) -> str:
    settings = validate(settings) if settings is not None else local_settings()
    text, count = re.subn(
        r"PRODUCT_BUNDLE_IDENTIFIER = [^;]+;", f'PRODUCT_BUNDLE_IDENTIFIER = "{settings["bundle"]}";', text
    )
    if count != 2:
        raise ValueError("Expected two app build configurations")
    text = re.sub(r"DEVELOPMENT_TEAM = [^;]*;", f'DEVELOPMENT_TEAM = "{settings["team"]}";', text)
    text = re.sub(r"CODE_SIGN_STYLE = [^;]+;", "CODE_SIGN_STYLE = Automatic;", text)
    text = text.replace("Apple Distribution", "Apple Development")
    text = text.replace('VALID_ARCHS = "arm64 x86_64";', "VALID_ARCHS = arm64;")
    # This phase reads source inputs outside the export directory.
    text = text.replace("ENABLE_USER_SCRIPT_SANDBOXING = YES;", "ENABLE_USER_SCRIPT_SANDBOXING = NO;")
    if "ENABLE_USER_SCRIPT_SANDBOXING" not in text:
        text = text.replace("PRODUCT_NAME = ", "ENABLE_USER_SCRIPT_SANDBOXING = NO;\n\t\t\t\tPRODUCT_NAME = ")
    if PHASE_MARKER in text:
        return text
    if PHASE_ID in text:
        raise ValueError(f"The id {PHASE_ID} is already used in the project")
    command = '"${SRCROOT}/../../../scripts/check-xcode-inputs.sh"'
    phase = "\n".join(
        [
            f"\t\t{PHASE_ID} /* Verify prepared game */ = {{",
            "\t\t\tisa = PBXShellScriptBuildPhase;",
            "\t\t\talwaysOutOfDate = 1;",
            "\t\t\tbuildActionMask = 2147483647;",
            "\t\t\tfiles = ();",
            "\t\t\tinputPaths = ();",
            "\t\t\toutputPaths = ();",
            '\t\t\tname = "Verify prepared game";',
            "\t\t\trunOnlyForDeploymentPostprocessing = 0;",
            "\t\t\tshellPath = /bin/sh;",
            f"\t\t\tshellScript = {json.dumps(command + '\n')};",
            "\t\t};\n",
        ]
    )
    marker = "/* Begin PBXShellScriptBuildPhase section */"
    if marker in text:
        text = text.replace(marker, marker + "\n" + phase)
    else:
        text = text.replace(
            "/* Begin PBXSourcesBuildPhase section */",
            marker
            + "\n"
            + phase
            + "/* End PBXShellScriptBuildPhase section */\n\n/* Begin PBXSourcesBuildPhase section */",
        )
    text, count = re.subn(
        r"(isa = PBXNativeTarget;.*?buildPhases = \(\n)",
        r"\1" + f"\t\t\t\t{PHASE_ID} /* Verify prepared game */,\n",
        text,
        flags=re.DOTALL,
    )
    if count != 1:
        raise ValueError("Expected exactly one app target")
    return text


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["capture"])
    parser.parse_args()
    if PROJECT.exists() and (PROJECT.parent.parent / "full-game-export-profile.json").exists():
        try:
            capture(PROJECT.read_text())
        except ValueError as error:
            raise SystemExit(str(error)) from None


if __name__ == "__main__":
    main()
