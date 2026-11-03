"""Prepare the game files for the app's data folder from this Mac's Steam install of a supported version.

For the tests and for testing without a download: the app downloads the game itself. The app's build never runs this and never reads its output,
except for the scan, which uses it as a reference when it is there.

Output: analysis/game-files, the contents of Documents/game on the phone, and
analysis/full-game-reference, the Python tools' game pack and converted textures, which tests/GamePack.Tests compares the app's with.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
from pathlib import Path

import build_full_game_pack
import prepare_shader_warmup
import scan_no_game_files
import setup_ios
from dotnet_environment import dotnet_environment
from hashing import sha256_file

ROOT = setup_ios.ROOT
OUTPUT = ROOT / "analysis/game-files"
REFERENCE = ROOT / "analysis/full-game-reference"
MANAGED = ROOT / "analysis/full-game-managed/public"
WARMUP = ROOT / "analysis/full-game-warmup"
BUILD_TOOL = ROOT / "tools/mono/MonoBuildTool/bin/Debug/net10.0/MonoBuildTool"
GODOTSHARP = ROOT / ".tools/nuget/packages/godotsharp/4.5.1/lib/net8.0"
ORIGINAL_ASSEMBLIES = scan_no_game_files.ORIGINAL_ASSEMBLIES
UNCHANGED_ASSEMBLIES = scan_no_game_files.UNCHANGED_ASSEMBLIES
BUILT_IN_MODS = ROOT / "mods/builtin.json"
MANIFEST_NAME = "game-files.json"
GAME_PACK = "Slay the Spire 2.pck"
TEXTURES_PACK = "phone-textures.pck"  # in the reference only: the app makes its own


def tool_environment() -> dict:
    return dotnet_environment()


def stage_assemblies(spec: dict, contents: Path, environment: dict) -> None:
    game_dll = contents / "Resources/data_sts2_macos_arm64/sts2.dll"
    setup_ios.stage(
        "game-assemblies",
        setup_ios.stage_fingerprint("game-assemblies") | {"game": spec["game"]["assembly_sha256"]},
        [MANAGED],
        lambda: setup_ios.run(
            ["bash", "scripts/stage-game-assemblies.sh", str(MANAGED)],
            "Patch game assemblies",
            environment | {"STS2_GAME_DLL": str(game_dll)},
        ),
    )


def check_references(environment: dict) -> None:
    """Every game member the app's game-facing assemblies use has to exist in the patched game."""
    setup_ios.run(["dotnet", "build", "tools/mono/MonoBuildTool", "-v:q"], "Build Mono tool", environment)
    result = subprocess.run(
        [
            str(BUILD_TOOL),
            "check-game-references",
            str(MANAGED / "game/sts2.dll"),
            str(GODOTSHARP),
            str(ROOT / "src/STS2Mobile.GameHost/.godot/mono/temp/bin/Debug/STS2Mobile.GameHost.dll"),
            str(ROOT / "src/STS2Mobile.GameAdapters/bin/Debug/net9.0/STS2Mobile.GameAdapters.dll"),
        ],
        env=environment,
        capture_output=True,
        text=True,
    )
    if result.returncode:
        raise ValueError(f"The game stub does not match this game:\n{result.stdout}{result.stderr}")


def built_in_mods(catalog: Path = BUILT_IN_MODS) -> list[tuple[Path, str]]:
    """Each code mod's files and where they go in the original folder, each checked against the catalog's hash."""
    files = []
    for entry in json.loads(catalog.read_text())["mods"]:
        # An optional mod (QuickMatch) that ./setup.sh did not fetch: the app is built without it.
        if entry.get("optional") and not Path(entry["dll"]).expanduser().is_file():
            continue
        fields = [("dll", "dll"), ("manifest", "json")]
        if "pck" in entry:
            fields.append(("pck", "pck"))
        for field, suffix in fields:
            path = Path(entry[field]).expanduser()
            pin = entry[field + "Sha256"]
            if not path.is_file() or sha256_file(path) != pin:
                raise ValueError(f"Built-in mod {entry['id']}: {path.name} does not match its catalog")
            files.append((path, f"mods/{entry['id']}.{suffix}"))
    return files


def write_files(contents: Path, output: Path = OUTPUT) -> None:
    """The game's own files, as the app wants them. The app makes the assembly and pack it runs from those in original/."""
    data = contents / "Resources/data_sts2_macos_arm64"
    if output.exists():
        shutil.rmtree(output)
    original = output / "original"
    (original / "mods").mkdir(parents=True)
    for name in ORIGINAL_ASSEMBLIES:
        shutil.copy2(data / name, original / name)
    for path, name in built_in_mods():
        shutil.copy2(path, original / name)
    for name in UNCHANGED_ASSEMBLIES:
        shutil.copy2(data / name, output / name)
    shutil.copy2(contents / "Resources/release_info.json", output / "release_info.json")
    # A clone: this is 2 GB, and the copy shares its blocks with the installed game.
    subprocess.run(["cp", "-c", str(contents / "Resources" / GAME_PACK), str(original / GAME_PACK)], check=True)
    manifest = {
        path.relative_to(output).as_posix(): sha256_file(path)
        for path in sorted(output.rglob("*"))
        if path.is_file() and path.name != MANIFEST_NAME
    }
    (output / MANIFEST_NAME).write_text(json.dumps({"files": manifest}, indent=2) + "\n")


def write_reference(output: Path = REFERENCE) -> None:
    """The game pack and converted textures as the Python tools make them, on this Mac only, for the tests to compare the app's with."""
    if WARMUP.exists():
        shutil.rmtree(WARMUP)
    prepare_shader_warmup.prepare(setup_ios.ASSETS, WARMUP)
    extra = {f"warmup/{path.name}": path for path in sorted(WARMUP.iterdir()) if path.is_file()}
    if output.exists():
        shutil.rmtree(output)
    build_full_game_pack.build_game_pack(setup_ios.ASSETS, output / "FullGame.pck", setup_ios.TEXTURES, setup_ios.SCENES, extra)
    build_full_game_pack.build_texture_pack(setup_ios.TEXTURES, setup_ios.ASSETS, output / TEXTURES_PACK)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-path", help="Steam game directory or SlayTheSpire2.app")
    parser.add_argument("--check", action="store_true", help="only check that the installed game is a supported version")
    options = parser.parse_args()
    spec = json.loads(setup_ios.MANIFEST.read_text())
    contents = setup_ios.discover_game(options.game_path)
    setup_ios.check_game(contents, spec["game"])
    print(f"Steam v{spec['game']['version']} verified.", flush=True)
    if options.check:
        return
    environment = tool_environment()
    setup_ios.prepare_assets(spec, contents, environment)
    stage_assemblies(spec, contents, environment)
    setup_ios.run(["dotnet", "build", "src/STS2Mobile.GameHost", "-v:q"], "Build game host", environment)
    check_references(environment)
    write_files(contents)
    write_reference()
    print(f"Ready: {OUTPUT.relative_to(ROOT)}", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, subprocess.CalledProcessError) as error:
        raise SystemExit(f"Game files stopped: {error}") from None
