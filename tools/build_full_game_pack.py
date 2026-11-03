"""The Mac's way of making the game pack, from the staged game resources: the reference for STS2Mobile.GamePack, which does
this on the phone. Fails closed on anything it has not been told about. It also writes the
converted textures as a pack of their own, the reference for what the phone's TextureConversion makes.
"""

import argparse
import hashlib
import json
from collections.abc import Iterator
from pathlib import Path

from godot_pack import PackError, write_pack
from hashing import sha256_file
from profiles import SCENE_PROFILE, TEXTURE_PROFILE, load_asset_report

SETTINGS = "project.binary"
UID_CACHE = ".godot/uid_cache.bin"
CLASS_CACHE = ".godot/global_script_class_cache.cfg"
EXTENSION_LIST = ".godot/extension_list.cfg"
# Game entries that never ship: desktop-only payloads and registrations the host replaces.
SKIPPED_GAME_PREFIXES = ("steam/", "RiderTestRunner/", "SlayTheSpire2.app/")
TEXTURE_MANIFEST = "texture-overlay.json"


def game_entries(assets: Path, report: dict):
    """Yield (name, path, sha256) for every staged game file that ships."""
    for entry in report["staged"]:
        name = entry["path"]
        if (
            name in (SETTINGS, EXTENSION_LIST)
            or name.endswith(".gdextension")
            or name.startswith(SKIPPED_GAME_PREFIXES)
        ):
            continue
        if name.startswith("/") or ".." in Path(name).parts:
            raise PackError(f"Unsafe staged resource path: {name}")
        yield name, assets / name, entry["sha256"]


def texture_overlay(
    directory: Path, staged: dict[str, str], asset_pack_sha256: str
) -> tuple[dict[str, tuple[Path, str]], set[str]]:
    """Converted textures to ship in place of staged originals (tools/transcode_textures_astc.py).

    Returns the files to add or replace, and the original texture files to leave out.
    The overlay is only accepted if it was made from exactly the staged inputs being packed.
    """
    report = json.loads((directory / "texture-report.json").read_text())
    if report.get("profile") != TEXTURE_PROFILE or report.get("assetPackSha256") != asset_pack_sha256:
        raise PackError("Converted textures were not made for this asset profile")
    replaced, removed = {}, set()
    for texture in report["textures"]:
        source, remap, output = texture["source"], texture["import"], texture["output"]
        if staged.get(source) != texture["sourceSha256"] or staged.get(remap) != texture["sourceImportSha256"]:
            raise PackError(f"Converted texture was not made from the staged original: {source}")
        if (
            output in staged
            or output in replaced
            or source in removed
            or ".." in Path(output).parts
            or output.startswith("/")
        ):
            raise PackError(f"Converted texture has an unusable path: {output}")
        replaced[remap] = (directory / remap, texture["importSha256"])
        replaced[output] = (directory / output, texture["outputSha256"])
        removed.add(source)
    return replaced, removed


def scene_overlay(directory: Path, staged: dict[str, str], asset_pack_sha256: str) -> dict[str, tuple[Path, str]]:
    """Scenes adjusted for the phone (tools/limit_particle_preprocess.py) to ship in place of staged originals."""
    report = json.loads((directory / "scene-report.json").read_text())
    if report.get("profile") != SCENE_PROFILE or report.get("assetPackSha256") != asset_pack_sha256:
        raise PackError("Adjusted scenes were not made for this asset profile")
    replaced = {}
    for scene in report["scenes"]:
        name = scene["path"]
        if not name.endswith(".tscn") or staged.get(name) != scene["sourceSha256"] or name in replaced:
            raise PackError(f"Adjusted scene was not made from the staged original: {name}")
        replaced[name] = (directory / name, scene["outputSha256"])
    return replaced


def read_verified(path: Path, sha256: str, name: str) -> bytes:
    data = path.read_bytes()
    if hashlib.sha256(data).hexdigest() != sha256:
        raise PackError(f"Staged resource changed: {name}")
    return data


def shipped_game_files(
    assets: Path, report: dict, staged: dict[str, str], textures: Path | None, scenes: Path | None
) -> dict[str, tuple[Path, str]]:
    """name -> (file, sha256) for every game file in the pack, with the phone overlays applied."""
    shipped = {name: (path, sha256) for name, path, sha256 in game_entries(assets, report)}
    if textures is not None:
        replaced, removed = texture_overlay(textures.resolve(), staged, report["packSha256"])
        if (
            not removed <= shipped.keys()
            or not {name for name in replaced if name.endswith(".import")} <= shipped.keys()
        ):
            raise PackError("Converted textures name files that do not ship")
        for name in removed:
            del shipped[name]
        shipped.update(replaced)
    if scenes is not None:
        adjusted = scene_overlay(scenes.resolve(), staged, report["packSha256"])
        if not adjusted.keys() <= shipped.keys():
            raise PackError("Adjusted scenes name files that do not ship")
        shipped.update(adjusted)
    return shipped


def require_new_destination(assets: Path, output: Path) -> Path:
    partial = output.with_name(f"{output.name}.partial")
    if output.exists() or partial.exists():
        raise PackError("Use a new pack destination")
    if output.is_relative_to(assets):
        raise PackError("The pack must be written outside the asset staging tree")
    return partial


def build_game_pack(
    assets: Path,
    output: Path,
    textures: Path | None = None,
    scenes: Path | None = None,
    extra: dict[str, Path] | None = None,
) -> dict:
    """The game's files alone, to mount from the app's data folder over a main pack of host files.

    `extra` adds files made from the game, by pack name: the shader warmup scenes and inventory.

    It keeps the game's own project settings, UID cache and class cache: on the phone, the host turns the settings into
    the file it reads at startup, and the engine adds the two caches when the pack is mounted.
    """
    assets, output = assets.resolve(), output.resolve()
    partial = require_new_destination(assets, output)
    report = load_asset_report(assets)
    staged = {entry["path"]: entry["sha256"] for entry in report["staged"]}
    shipped = shipped_game_files(assets, report, staged, textures, scenes)
    for required in (UID_CACHE, CLASS_CACHE):
        if required not in shipped:
            raise PackError(f"The game has no {required}")

    def contents() -> Iterator[tuple[str, bytes]]:
        yield SETTINGS, read_verified(assets / SETTINGS, staged[SETTINGS], SETTINGS)
        for name, (path, sha256) in shipped.items():
            yield name, read_verified(path, sha256, name)
        for name, path in (extra or {}).items():
            if name in shipped:
                raise PackError(f"An added file shadows a game file: {name}")
            yield name, path.read_bytes()

    partial.parent.mkdir(parents=True, exist_ok=True)
    try:
        with partial.open("wb") as stream:
            index = write_pack(stream, contents())
        output_sha256 = sha256_file(partial)
        partial.rename(output)
    except BaseException:
        partial.unlink(missing_ok=True)
        raise
    return {"entries": len(index), "outputBytes": output.stat().st_size, "outputSha256": output_sha256}


def build_texture_pack(textures: Path, assets: Path, output: Path) -> dict:
    """The converted textures as one file: the reference for STS2Mobile.GamePack's TextureConversion, compared by tests.

    The first entry says which game files they replace, and the others follow in the report's order.
    """
    textures, assets, output = textures.resolve(), assets.resolve(), output.resolve()
    partial = require_new_destination(assets, output)
    asset_report = load_asset_report(assets)
    staged = {entry["path"]: entry["sha256"] for entry in asset_report["staged"]}
    texture_overlay(textures, staged, asset_report["packSha256"])
    report = json.loads((textures / "texture-report.json").read_text())
    manifest = {
        "profile": report["profile"],
        "assetPackSha256": report["assetPackSha256"],
        "textures": [{field: texture[field] for field in ("source", "import", "output")} for texture in report["textures"]],
    }

    def contents() -> Iterator[tuple[str, bytes]]:
        yield TEXTURE_MANIFEST, json.dumps(manifest).encode()
        for texture in report["textures"]:
            yield texture["import"], read_verified(textures / texture["import"], texture["importSha256"], texture["import"])
            yield texture["output"], read_verified(textures / texture["output"], texture["outputSha256"], texture["output"])

    partial.parent.mkdir(parents=True, exist_ok=True)
    try:
        with partial.open("wb") as stream:
            index = write_pack(stream, contents())
        output_sha256 = sha256_file(partial)
        partial.rename(output)
    except BaseException:
        partial.unlink(missing_ok=True)
        raise
    return {"entries": len(index), "outputBytes": output.stat().st_size, "outputSha256": output_sha256}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True, help="asset staging tree (read only)")
    parser.add_argument("--output", type=Path, required=True, help="new game pack; must not exist")
    parser.add_argument(
        "--textures",
        type=Path,
        help="converted textures from tools/transcode_textures_astc.py to ship in place of the originals",
    )
    parser.add_argument(
        "--scenes",
        type=Path,
        help="scenes adjusted for the phone by tools/limit_particle_preprocess.py to ship in place of the originals",
    )
    options = parser.parse_args()
    try:
        built = build_game_pack(options.assets, options.output, options.textures, options.scenes)
    except ValueError as error:
        raise SystemExit(str(error)) from None
    print(f"Built game pack with {built['entries']} entries ({built['outputBytes']} bytes).")


if __name__ == "__main__":
    main()
