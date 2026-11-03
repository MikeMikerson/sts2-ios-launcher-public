"""Fail if an IPA holds any of the game's files. The app's build runs this on what it produced.

It works with no game files around, from what the app's parts must look like: nothing with a game file's name, a main pack
of known host files only, and no name from the game's code in the app's binaries. When prepared game files are there, it
also looks for pieces of them, and for main pack entries that are byte for byte game files.

    tools/python/bin/python3 tools/scan_no_game_files.py <app.ipa> [--game-files analysis/game-files]
"""

from __future__ import annotations

import argparse
import fnmatch
import hashlib
import mmap
import re
import sys
import tempfile
import zipfile
from pathlib import Path

import godot_pack

# The game's folder also has System.IO.Hashing.dll, but that is Microsoft's package, which the app gets from NuGet through
# SteamKit2. The app's newer copy answers the game's reference.
# The app makes the assembly it runs from the first of these, with the second group's help, so they sit apart in the game
# files.
ORIGINAL_ASSEMBLIES = ("sts2.dll", "Sentry.dll", "Sentry.Godot.dll")
UNCHANGED_ASSEMBLIES = ("Steamworks.NET.dll", "SmartFormat.dll", "SmartFormat.ZString.dll")
GAME_ASSEMBLIES = (*ORIGINAL_ASSEMBLIES, *UNCHANGED_ASSEMBLIES)
GAME_FILE_NAMES = {*GAME_ASSEMBLIES, "release_info.json", "game-settings.godot", "builtin-mods.json"}
# Kinds of file only the game brings: a managed assembly loose in the app, sound banks, Spine skeletons and atlases.
GAME_FILE_PATTERNS = ("*.dll", "*.bank", "*.skel", "*.spine", "*.atlas", "*.ctex")
MAIN_PACK = "FullGame.pck"
# The pack of the prepared game files, whose entries the main pack must not hold.
GAME_PACKS = ("Slay the Spire 2.pck",)
# Public files of a third party's runtime that the game ships too, and the host has from vendor/ and not from the game.
VENDORED_FILES = (Path(__file__).resolve().parent.parent / "vendor/spine/4.2-godot4.5.1/bin/spine_godot_extension.gdextension",)
# Everything the host's main pack may hold. Anything else stops the build, so a new host file has to be named here.
MAIN_PACK_ALLOWED = (
    "project.binary",
    ".godot/extension_list.cfg",
    ".godot/global_script_class_cache.cfg",
    ".godot/uid_cache.bin",
    ".godot/exported/*/export-*-Launcher.scn",
    ".godot/imported/icon.png-*.ctex",
    "Launcher.tscn.remap",
    "addons/fmod/fmod.gdextension",
    "addons/spine/spine_godot_extension.gdextension",
    "icon.png",
    "icon.png.import",
    # The launcher's three typefaces, under the SIL Open Font License, with their licences (src/STS2Mobile.FullGame/fonts).
    "fonts/Grenze.ttf.import",
    "fonts/AtkinsonHyperlegibleNext.ttf.import",
    "fonts/AtkinsonHyperlegibleMono.ttf.import",
    ".godot/imported/Grenze.ttf-*.fontdata",
    ".godot/imported/AtkinsonHyperlegibleNext.ttf-*.fontdata",
    ".godot/imported/AtkinsonHyperlegibleMono.ttf-*.fontdata",
    "fonts/*-OFL.txt",
    # Empty entries Godot's export writes for each script of the launcher's assembly.
    "*.cs",
)
# A scripts-only entry must be empty or Godot's one-byte placeholder: its text never ships.
PLACEHOLDER_LIMIT = 1
# Names from the game's code that its stub does not declare, so they appear only if game code is embedded. In the stub or
# the host code, they would fail this scan.
GAME_CODE_MARKERS = (b"AtlasManager", b"NCursorManager", b"ReleaseInfoManager", b"NCombatRoom", b"CardPlayInput")
SAMPLE_SIZE = 4096
SAMPLES = 16


def allowed_in_main_pack(name: str) -> bool:
    return any(fnmatch.fnmatchcase(name, pattern) for pattern in MAIN_PACK_ALLOWED)


def scan_names(names: list[str]) -> list[str]:
    problems = []
    for name in names:
        base = name.rsplit("/", 1)[-1]
        if base in GAME_FILE_NAMES or any(fnmatch.fnmatchcase(base, pattern) for pattern in GAME_FILE_PATTERNS):
            problems.append(f"the IPA has {name}")
        elif base.endswith(".pck") and base != MAIN_PACK:
            problems.append(f"the IPA has another pack: {name}")
    return problems


def scan_main_pack(path: Path) -> list[str]:
    files = dict(godot_pack.read_pack(path))
    problems = [f"the main pack has {name}" for name in sorted(files) if not allowed_in_main_pack(name)]
    problems += [
        f"the main pack has script text in {name}"
        for name, data in files.items()
        if name.endswith(".cs") and len(data) > PLACEHOLDER_LIMIT
    ]
    return problems


def scan_binary(path: Path) -> list[str]:
    """A binary of the app must not name a game assembly as a file, or a name only game code has."""
    if path.stat().st_size == 0:
        return []
    problems = []
    with path.open("rb") as stream, mmap.mmap(stream.fileno(), 0, prot=mmap.PROT_READ) as data:
        for name in GAME_ASSEMBLIES:
            if data.find(name.encode() + b"\0") >= 0:
                problems.append(f"{path.name} embeds an assembly named {name}")
        for marker in GAME_CODE_MARKERS:
            if data.find(marker) >= 0:
                problems.append(f"{path.name} contains the game's name {marker.decode()}")
    return problems


def is_binary(path: Path) -> bool:
    with path.open("rb") as stream:
        return stream.read(4) in (b"\xcf\xfa\xed\xfe", b"\xca\xfe\xba\xbe")


def samples_of(path: Path) -> list[bytes]:
    data = path.read_bytes()
    step = max((len(data) - SAMPLE_SIZE) // SAMPLES, 1)
    return [data[at : at + SAMPLE_SIZE] for at in range(0, max(len(data) - SAMPLE_SIZE, 1), step)]


def scan_against_game(app: Path, game_files: Path) -> list[str]:
    """Look for pieces of the prepared assemblies in every file, and for main pack entries that are game files."""
    problems = []
    samples = {name: samples_of(game_files / "original" / name) for name in ORIGINAL_ASSEMBLIES}
    samples |= {name: samples_of(game_files / name) for name in UNCHANGED_ASSEMBLIES}
    for path in sorted(p for p in app.rglob("*") if p.is_file() and not p.is_symlink() and p.stat().st_size):
        with path.open("rb") as stream, mmap.mmap(stream.fileno(), 0, prot=mmap.PROT_READ) as contents:
            for name, pieces in samples.items():
                found = sum(contents.find(piece) >= 0 for piece in pieces)
                if found:
                    problems.append(f"{found} of {len(pieces)} pieces of {name} are in {path.name}")
    game_entries = {}
    for pack in GAME_PACKS:
        with (game_files / "original" / pack).open("rb") as stream:
            game_entries |= {entry.md5: entry.name for entry in godot_pack.read_directory(stream) if entry.size > PLACEHOLDER_LIMIT}
    for path in VENDORED_FILES:
        game_entries.pop(hashlib.md5(path.read_bytes()).digest(), None)
    for name, data in godot_pack.read_pack(app / MAIN_PACK):
        if name not in ("project.binary",) and hashlib.md5(data).digest() in game_entries:
            problems.append(f"the main pack's {name} is the game's {game_entries[hashlib.md5(data).digest()]}")
    return problems


def scan(ipa: Path, game_files: Path | None = None) -> list[str]:
    with zipfile.ZipFile(ipa) as archive:
        names = [name for name in archive.namelist() if not name.endswith("/")]
        problems = scan_names(names)
        apps = {match.group(1) for match in map(re.compile(r"^(Payload/[^/]+\.app)/").match, names) if match}
        if len(apps) != 1:
            return problems + [f"the IPA holds {len(apps)} apps"]
        with tempfile.TemporaryDirectory() as temporary:
            archive.extractall(temporary)
            app = Path(temporary) / apps.pop()
            if not (app / MAIN_PACK).is_file():
                return problems + ["the app has no main pack"]
            problems += scan_main_pack(app / MAIN_PACK)
            for path in sorted(p for p in app.rglob("*") if p.is_file() and not p.is_symlink()):
                if is_binary(path):
                    problems += scan_binary(path)
            if game_files is not None:
                problems += scan_against_game(app, game_files)
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("ipa", type=Path)
    parser.add_argument("--game-files", type=Path, help="prepared game files to compare with, if there are any")
    options = parser.parse_args()
    problems = scan(options.ipa, options.game_files)
    for problem in problems:
        print(problem)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
