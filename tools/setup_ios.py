"""Prepare the toolchain and build the app. No game file takes part.

tools/prepare_game_files.py uses the game functions below, on a Mac with the game installed.
"""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import os
import platform
import re
import shutil
import subprocess
import sys
import tarfile
import urllib.parse
import zipfile
from pathlib import Path

import install_app
from dotnet_environment import dotnet_environment

ROOT = Path(__file__).resolve().parents[1]
MANIFEST = ROOT / "tools/dependencies.json"
STATE = ROOT / ".work/setup"
CACHE = ROOT / ".tools/cache"
LOGS = ROOT / ".work/logs"
PYTHON = ROOT / "tools/python/bin/python3"
ASSETS = ROOT / "analysis/full-game-assets-clean-v1"
TEXTURES = ROOT / "analysis/full-game-textures-astc-v1"
SCENES = ROOT / "analysis/full-game-scenes-phone-v1"
STEAM = ROOT / "analysis/steam-managed"
BUILTIN_MODS = ROOT / ".tools/builtin-mods"
RELAY_FILE = ROOT / ".local/QuickMatchRelay.cfg"
LIBRARY_BUILDER = "Microsoft.NET.Runtime.LibraryBuilder.Sdk"
# The source files each prepared stage is made from, including the shared tool modules its scripts
# import. A stage is prepared again when any of them changes.
STAGE_SOURCES = {
    "fmod": ["tools/stage_fmod_dependencies.py", "tools/hashing.py"],
    "spine": ["tools/setup_ios.py"],
    "texture-encoder": ["tools/build_texture_encoder.py", "src/STS2Mobile.GamePack/native"],
    "assets": ["tools/stage_full_game.py", "tools/godot_pack.py", "tools/hashing.py", "tools/profiles.py"],
    "textures": [
        "tools/transcode_textures_astc.py",
        "tools/texture_transcode",
        "tools/hashing.py",
        "tools/profiles.py",
    ],
    "scenes": ["tools/limit_particle_preprocess.py", "tools/profiles.py"],
    "steam": [
        "src/STS2Mobile.Steam",
        "src/STS2Mobile.SteamWire",
        "tools/SteamAotPatcher",
        "tools/Shared",
        "tools/Directory.Build.props",
        "tools/dependencies.json",
        "tools/generate_steam_wire_model.py",
        "scripts/stage-steam.sh",
        "Directory.Build.props",
        ".editorconfig",
    ],
    "game-assemblies": [
        "src/STS2Mobile.Platform",
        "src/STS2Mobile.GameAdapters",
        "src/STS2Mobile.GameStub",
        "mods",
        "src/STS2Mobile.GamePatcher",
        "tools/FullGamePatcher",
        "tools/Shared",
        "tools/Directory.Build.props",
        "tools/dependencies.json",
        "scripts/stage-game-assemblies.sh",
        "Directory.Build.props",
        ".editorconfig",
    ],
}


# Not hashing.sha256_file: `setup.sh --check` runs this file under the system Python 3.9, which has
# no hashlib.file_digest, and some downloads are pinned by SHA-512.
def digest(path: Path, algorithm: str = "sha256") -> str:
    result = hashlib.new(algorithm)
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def safe_member(name: str) -> Path:
    path = Path(name)
    if path.is_absolute() or ".." in path.parts or "\\" in name:
        raise ValueError(f"Unsafe archive member: {name}")
    return path


def download(name: str, specification: dict) -> Path:
    CACHE.mkdir(parents=True, exist_ok=True)
    target = CACHE / f"{name}-{Path(specification['url']).name}"
    algorithm = "sha512" if "sha512" in specification else "sha256"
    expected = specification[algorithm]
    if target.exists():
        if digest(target, algorithm) != expected:
            raise ValueError(f"Cached download checksum mismatch; remove {target.relative_to(ROOT)} and retry.")
        return target
    print(f"Downloading {name}.", flush=True)
    partial = target.with_name(f"{target.name}.partial")
    subprocess.run(
        [
            "curl",
            "--fail",
            "--location",
            "--retry",
            "3",
            "--silent",
            "--show-error",
            "--proto",
            "=https",
            "--tlsv1.2",
            specification["url"],
            "--output",
            str(partial),
        ],
        check=True,
    )
    if digest(partial, algorithm) != expected:
        partial.unlink()
        raise ValueError(f"Download checksum mismatch: {name}")
    partial.replace(target)
    return target


def normalize_install(path: Path) -> Path:
    """Accept a Steam game folder, app bundle, Contents, or the managed DLL directory."""
    path = path.expanduser().resolve()
    candidates = [path, path / "Contents", path / "SlayTheSpire2.app/Contents"]
    if path.name == "data_sts2_macos_arm64":
        candidates.append(path.parent.parent)
    for candidate in candidates:
        if (candidate / "Resources/data_sts2_macos_arm64/sts2.dll").is_file():
            return candidate
    raise ValueError("Game path does not contain the macOS SlayTheSpire2.app installation.")


def discover_game(explicit: str | None, home: Path | None = None) -> Path:
    if explicit:
        return normalize_install(Path(explicit))
    home = home or Path.home()
    steam = home / "Library/Application Support/Steam"
    libraries = [steam]
    config = steam / "steamapps/libraryfolders.vdf"
    if config.is_file():
        libraries += [
            Path(value.replace("\\\\", "\\")) for value in re.findall(r'"path"\s+"([^"\n]+)"', config.read_text())
        ]
    matches = []
    for library in libraries:
        try:
            match = normalize_install(library / "steamapps/common/Slay the Spire 2")
            if match not in matches:
                matches.append(match)
        except ValueError:
            pass
    if len(matches) != 1:
        raise ValueError("Install the supported macOS game in Steam, or choose it with --game-path PATH.")
    return matches[0]


def check_game(contents: Path, profile: dict) -> None:
    for relative, key in [
        ("Resources/Slay the Spire 2.pck", "pack_sha256"),
        ("Resources/data_sts2_macos_arm64/sts2.dll", "assembly_sha256"),
    ]:
        path = contents / relative
        if not path.is_file() or digest(path) != profile[key]:
            raise ValueError(
                f"Unsupported game input: {path.name}. This checkout supports Steam v{profile['version']} only. "
                "The installation was not changed."
            )


def check_xcode(required: str, environment: dict) -> None:
    if platform.system() != "Darwin" or platform.machine() != "arm64":
        raise ValueError("Setup requires an Apple silicon Mac.")
    developer = Path(environment.get("DEVELOPER_DIR", "/Applications/Xcode.app/Contents/Developer"))
    if not (developer / "usr/bin/xcodebuild").is_file():
        raise ValueError(
            f"Install Xcode {required} and open it once to finish setup. "
            "Set DEVELOPER_DIR for a nonstandard installation."
        )
    environment["DEVELOPER_DIR"] = str(developer)
    version = subprocess.check_output(["xcodebuild", "-version"], env=environment, text=True)
    if not re.search(rf"^Xcode {re.escape(required)}(?:\s|$)", version):
        raise ValueError(
            f"This checkout is validated with Xcode {required}; select that installation using DEVELOPER_DIR."
        )
    result = subprocess.run(["xcodebuild", "-checkFirstLaunchStatus"], env=environment, capture_output=True)
    if result.returncode:
        raise ValueError("Open Xcode and complete its license and first-launch component installation before setup.")


def run(command: list[str], label: str, environment: dict) -> None:
    LOGS.mkdir(parents=True, exist_ok=True)
    print(f"{label}...", flush=True)
    log = LOGS / f"{label.lower().replace(' ', '-')}.log"
    with log.open("w") as output:
        result = subprocess.run(command, cwd=ROOT, env=environment, stdout=output, stderr=subprocess.STDOUT)
    if result.returncode:
        raise RuntimeError(f"{label} failed. See {log.relative_to(ROOT)}")


def tree_hashes(path: Path) -> dict[str, str]:
    return {str(p.relative_to(path)): digest(p) for p in sorted(path.rglob("*")) if p.is_file()}


def source_fingerprint(paths: list[Path]) -> dict[str, str]:
    result = {}
    for path in paths:
        files = path.rglob("*") if path.is_dir() else [path]
        for file in files:
            if (
                not file.name.endswith(".uid")
                and file.name not in {"icon.png", "icon.png.import", "release_info.json"}
                and file.is_file()
                and not {"bin", "obj", ".godot", "__pycache__", "staged", "addons", "warmup"}
                & set(file.relative_to(ROOT).parts)
            ):
                result[str(file.relative_to(ROOT))] = digest(file)
    return result


def stage_fingerprint(name: str) -> dict[str, str]:
    return source_fingerprint([ROOT / path for path in STAGE_SOURCES[name]])


def stage(name: str, inputs: dict, outputs: list[Path], action) -> None:
    """Reuse only complete stages whose input fingerprint and every output still match."""
    stamp = STATE / f"{name}.json"
    if stamp.exists():
        saved = json.loads(stamp.read_text())
        if saved["inputs"] == inputs and all(path.is_dir() for path in outputs):
            observed = {str(path.relative_to(ROOT)): tree_hashes(path) for path in outputs}
            if observed != saved["outputs"]:
                raise ValueError(
                    f"Prepared {name} files changed. Remove its generated output directories and rerun setup."
                )
            print(f"Reusing verified {name}.", flush=True)
            return
    # Only dedicated, generated output directories can be replaced.
    for path in outputs:
        relative = path.resolve().relative_to(ROOT.resolve())
        if not relative.parts or relative.parts[0] not in {"analysis", "vendor"}:
            raise ValueError(f"Refusing to replace a source directory: {relative}")
        if path.exists():
            shutil.rmtree(path)
    action()
    observed = {str(path.relative_to(ROOT)): tree_hashes(path) for path in outputs}
    temporary = stamp.with_suffix(".partial")
    temporary.write_text(json.dumps({"inputs": inputs, "outputs": observed}, indent=2) + "\n")
    temporary.replace(stamp)


def unpack_tar(archive: Path, destination: Path) -> None:
    destination.mkdir(parents=True, exist_ok=True)
    with tarfile.open(archive) as tar:
        # Python's data filter rejects escaping links, device nodes and unsafe paths.
        tar.extractall(destination, filter="data")


def provision_dotnet(spec: dict, environment: dict) -> None:
    dotnet = ROOT / "tools/ios-dotnet"
    dotnet.mkdir(parents=True, exist_ok=True)
    for name, version in (("sdk", spec["dotnet_sdk"]), ("runtime", spec["dotnet_runtime"])):
        marker = dotnet / f".{name}-{version}"
        if not marker.exists():
            unpack_tar(download(f"dotnet-{name}", spec["downloads"][f"dotnet-{name}"]), dotnet)
            marker.write_text(f"{version}\n")
    environment.update(dotnet_environment(environment))
    workload_marker = dotnet / f".ios-workload-{spec['workload_set']}"
    if not workload_marker.exists():
        run(
            [str(dotnet / "dotnet"), "workload", "install", "ios", "--version", spec["workload_set"]],
            "Install iOS workload",
            environment,
        )
        workload_marker.write_text(f"{spec['workload_set']}\n")


def edit_apple_build_targets(targets: Path, pins: dict) -> None:
    """Make the LibraryBuilder pack compile in full+interp mode, for the Mono build (the host export does not use the pack)."""
    if digest(targets) != pins["input_sha256"]:
        raise ValueError(f"Unexpected {targets.name} in the LibraryBuilder pack; the FullInterp edit was not applied.")
    full = "<_AOTMode Condition=\"'$(UseMonoJustInterp)' != 'true'\">Full</_AOTMode>"
    text = targets.read_bytes().decode()
    targets.write_bytes(text.replace(full, full.replace(">Full<", ">FullInterp<"), 1).encode())
    if digest(targets) != pins["output_sha256"]:
        raise ValueError(f"The FullInterp edit of {targets.name} gave an unexpected result.")


def provision_library_builder(spec: dict, packs: Path | None = None) -> Path:
    pins = spec["mono"]
    pack = (packs or ROOT / "tools/ios-dotnet/packs") / LIBRARY_BUILDER / pins["runtime_version"]
    targets = pack / "Sdk/AppleBuild.targets"
    if (pack / ".installed").exists():
        if digest(targets) != pins["apple_build_targets"]["output_sha256"]:
            raise ValueError(f"{targets} changed. Remove {pack} and rerun setup.")
        return pack
    archive = download("library-builder", spec["downloads"]["library-builder"])
    partial = pack.with_name(f"{pack.name}.partial")
    if partial.exists():
        shutil.rmtree(partial)
    with zipfile.ZipFile(archive) as bundle:
        for info in bundle.infolist():
            target = partial / safe_member(info.filename)
            if not info.is_dir():
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(bundle.read(info))
    edit_apple_build_targets(partial / "Sdk/AppleBuild.targets", pins["apple_build_targets"])
    (partial / ".installed").write_text(f"{pins['runtime_version']}\n")
    if pack.exists():
        shutil.rmtree(pack)
    partial.replace(pack)
    return pack


def provision_godot(downloads: dict, environment: dict) -> None:
    godot_root = ROOT / ".tools/godot"
    editor = godot_root / "Godot_mono.app/Contents/MacOS/Godot"
    godot_marker = godot_root / ".installed"
    if not godot_marker.exists():
        archive = download("godot", downloads["godot"])
        godot_root.mkdir(parents=True, exist_ok=True)
        # ditto preserves framework symlinks and executable modes in signed app bundles.
        run(["/usr/bin/ditto", "-x", "-k", str(archive), str(godot_root)], "Extract Godot", environment)
        if not editor.is_file():
            raise ValueError("Godot archive has an unsupported layout.")
        godot_marker.write_text(f"{downloads['godot']['sha512']}\n")
    (godot_root / "_sc_").touch()
    template = godot_root / "editor_data/export_templates/4.5.1.stable.mono/ios.zip"
    if not template.is_file() or not template.with_name("version.txt").is_file():
        archive = download("godot-templates", downloads["godot-templates"])
        with zipfile.ZipFile(archive) as bundle:
            template.parent.mkdir(parents=True, exist_ok=True)
            template.write_bytes(bundle.read("templates/ios.zip"))
            (template.parent / "version.txt").write_bytes(bundle.read("templates/version.txt"))
    environment["STS2_GODOT_EDITOR"] = str(editor)


def provision_native(spec: dict) -> None:
    from stage_fmod_dependencies import stage_binaries

    fmod_archive = download("fmod", spec["downloads"]["fmod"])
    fmod_license = download("fmod-license", spec["downloads"]["fmod-license"])
    fmod = ROOT / "vendor/fmod/2.03.06"
    stage(
        "fmod",
        {"archive": digest(fmod_archive), "license": digest(fmod_license)} | stage_fingerprint("fmod"),
        [fmod],
        lambda: stage_binaries(fmod_archive, fmod, fmod_license),
    )
    spine_archive = download("spine", spec["downloads"]["spine"])
    spine = ROOT / "vendor/spine/4.2-godot4.5.1"

    def extract_spine():
        spine.mkdir(parents=True)
        with zipfile.ZipFile(spine_archive) as archive:
            for info in archive.infolist():
                path = safe_member(info.filename)
                if info.is_dir() or not (
                    str(path).startswith(("bin/ios/", "bin/macos/"))
                    or path.name in {"spine_godot_extension.gdextension", "LICENSE", "LICENSE.txt"}
                ):
                    continue
                target = spine / path
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(info))
        if not (spine / "bin/spine_godot_extension.gdextension").is_file():
            raise ValueError("Spine archive has an unsupported layout.")
        (spine / "manifest.json").write_text(
            json.dumps(
                {
                    "spine_series": "4.2",
                    "source": spec["downloads"]["spine"]["url"],
                    "archive_sha256": digest(spine_archive),
                    "license": "https://esotericsoftware.com/spine-runtimes-license",
                    "files": [{"path": path, "sha256": value} for path, value in tree_hashes(spine).items()],
                },
                indent=2,
            )
            + "\n"
        )

    stage("spine", {"archive": digest(spine_archive)} | stage_fingerprint("spine"), [spine], extract_spine)
    provision_texture_encoder(spec)


def provision_builtin_mods(spec: dict) -> None:
    """The code mods built into the app (mods/builtin.json), each file checked against its pin, into .tools/builtin-mods.

    A mod comes from its pinned release. Until the release exists (its URL is still a <placeholder>), a copy of the same
    files in the mod's local_stand_in folder is used; with neither, the app is built without that mod.
    """
    if BUILTIN_MODS.exists():
        shutil.rmtree(BUILTIN_MODS)
    for mod, entry in spec.get("builtin_mods", {}).items():
        files = entry["files"]
        released = not any(item["url"].startswith("<") for item in files.values())
        stand_in = ROOT / entry["local_stand_in"]
        if not released and not all((stand_in / name).is_file() for name in files):
            print(f"{mod}: no release pinned yet and no copy in {entry['local_stand_in']}; building without it.", flush=True)
            continue
        sources = {}
        for name, item in files.items():
            source = download(f"{mod}-{name}", item) if released else stand_in / name
            if digest(source) != item["sha256"]:
                raise ValueError(f"{mod}: {source.relative_to(ROOT)} does not match its pin in tools/dependencies.json.")
            sources[name] = source
        BUILTIN_MODS.mkdir(parents=True, exist_ok=True)
        for name, source in sources.items():
            shutil.copy2(source, BUILTIN_MODS / name)
        print(f"{mod} {entry['version']} built in ({'release' if released else 'local stand-in'}).", flush=True)


def relay_address(url: str) -> str:
    """wss://<host> or https://<host>, as the app accepts it (src/STS2Mobile.Platform/RelaySettings.cs); ValueError otherwise."""
    parts = urllib.parse.urlsplit(url.strip())
    try:
        port = parts.port
    except ValueError:
        port = -1
    host = parts.hostname or ""
    if (
        parts.scheme not in {"wss", "https"}
        or not host
        or port not in (None, 443)
        or parts.username
        or parts.password
        or parts.path not in ("", "/")
        or parts.query
        or parts.fragment
        or "." not in host
        or host.endswith(".")
        or not re.fullmatch(r"[a-z0-9.-]+", host)
        or re.fullmatch(r"[0-9.]+", host)
    ):
        raise ValueError("a relay address is wss://<host> or https://<host> on port 443, with nothing after the host.")
    return f"{parts.scheme}://{host}"


def relay_from_file(text: str, name: str) -> str | None:
    """The relay a QuickMatchRelay.cfg names, or None when it names none or says enabled = false.

    The format the app reads: `key = value` lines (`relay = https://<host>`, `enabled = true|false`) and # comments. Any
    problem stops setup, where the app would silently drop the relay."""
    values: dict[str, str] = {}
    for number, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        key, equals, value = line.partition("=")
        key, value = key.strip().lower(), value.strip()
        if not equals or not key:
            raise ValueError(f"{name} line {number}: expected key = value")
        if key in values:
            raise ValueError(f"{name} line {number}: {key} is set twice")
        if key == "enabled" and value.lower() not in {"true", "on", "yes", "false", "off", "no"}:
            raise ValueError(f"{name} line {number}: enabled is true or false")
        if key not in {"enabled", "relay"}:
            raise ValueError(f'{name} line {number}: unknown setting "{key}" (known: enabled, relay)')
        if key == "relay":
            try:
                value = relay_address(value)
            except ValueError as error:
                raise ValueError(f"{name} line {number}: {error}") from None
        values[key] = value
    if values.get("enabled", "true").lower() in {"false", "off", "no"}:
        return None
    return values.get("relay")


def provision_quickmatch_relay() -> None:
    """QuickMatch's relay, chosen by whoever builds the app, never by a tracked file.

    QUICKMATCH_RELAY=wss://<host> (or https://<host>) in the environment, else .local/QuickMatchRelay.cfg, both checked as
    the app checks them. With neither, the app has no relay and QuickMatch matches over Steam lobby chat.
    """
    if not (BUILTIN_MODS / "QuickMatch.dll").is_file():
        return
    url = os.environ.get("QUICKMATCH_RELAY", "").strip()
    if url:
        try:
            relay, source = relay_address(url), "QUICKMATCH_RELAY"
        except ValueError as error:
            raise ValueError(f"QUICKMATCH_RELAY: {error}") from None
    elif RELAY_FILE.is_file():
        source = RELAY_FILE.relative_to(ROOT).as_posix()
        relay = relay_from_file(RELAY_FILE.read_text(), source)
        if relay is None:
            print(f"QuickMatch: {source} sets no relay; matches use Steam lobby chat.", flush=True)
            return
    else:
        print("QuickMatch: no relay set (QUICKMATCH_RELAY or .local/QuickMatchRelay.cfg); matches use Steam lobby chat.")
        return
    (BUILTIN_MODS / "QuickMatchRelay.cfg").write_text(f"relay = {relay}\n")
    print(f"QuickMatch relay built in, from {source}.", flush=True)


def provision_texture_encoder(spec: dict) -> None:
    import build_texture_encoder as encoder

    astc_archive = download("astc-encoder", spec["downloads"]["astc-encoder"])
    bcdec_header = download("bcdec", spec["downloads"]["bcdec"])

    def build() -> None:
        astc = encoder.unpack_astc(astc_archive, encoder.OUTPUT / "sources")
        bcdec = encoder.OUTPUT / "sources/bcdec"
        bcdec.mkdir(parents=True)
        shutil.copy2(bcdec_header, bcdec / "bcdec.h")
        encoder.build(astc, bcdec)

    stage(
        "texture-encoder",
        {"astc-encoder": digest(astc_archive), "bcdec": digest(bcdec_header)} | stage_fingerprint("texture-encoder"),
        [encoder.OUTPUT],
        build,
    )


def prepare_assets(spec: dict, contents: Path, environment: dict) -> None:
    """Stage the game's resources, then the phone textures and scenes made from them."""
    stage(
        "assets",
        {"pack": spec["game"]["pack_sha256"]} | stage_fingerprint("assets"),
        [ASSETS],
        lambda: run(
            [
                str(PYTHON),
                "tools/stage_full_game.py",
                "--pack",
                str(contents / "Resources/Slay the Spire 2.pck"),
                "--output",
                str(ASSETS),
            ],
            "Prepare game assets",
            environment,
        ),
    )
    report = {"assets": digest(ASSETS / "asset-report.json")}
    stage(
        "textures",
        stage_fingerprint("textures") | report,
        [TEXTURES],
        lambda: run(
            [str(PYTHON), "tools/transcode_textures_astc.py", "--assets", str(ASSETS), "--output", str(TEXTURES)],
            "Convert phone textures",
            environment,
        ),
    )
    stage(
        "scenes",
        stage_fingerprint("scenes") | report,
        [SCENES],
        lambda: run(
            [str(PYTHON), "tools/limit_particle_preprocess.py", "--assets", str(ASSETS), "--output", str(SCENES)],
            "Prepare phone scenes",
            environment,
        ),
    )


def prepare_steam(environment: dict) -> None:
    stage(
        "steam",
        stage_fingerprint("steam"),
        [STEAM],
        lambda: run(["bash", "scripts/stage-steam.sh", str(STEAM)], "Prepare SteamKit2", environment),
    )


def pipeline(environment: dict, prepare_only: bool, install: bool = False) -> None:
    prepare_steam(environment)
    run(
        [str(PYTHON), "tools/prepare_full_game_project.py", "--steam", str(STEAM / "steam")],
        "Prepare host project",
        environment,
    )
    run(["bash", "scripts/export-host.sh"], "Export host", environment)
    if install:
        # Signed with .local/device.json; its own steps print as it goes, and its logs are .work/logs/mono-*.log.
        print("Build app (about 3.5 minutes)...", flush=True)
        subprocess.run(["bash", "scripts/build-mono.sh"], cwd=ROOT, env=environment, check=True)
        install_app.install()
        return
    if prepare_only:
        print("Ready: build the app with scripts/build-mono.sh")
        return
    run(["bash", "scripts/build-mono.sh", "--unsigned"], "Build app", environment)
    print("Ready: analysis/full-game-mono/FullGame.ipa")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="check Xcode without downloading or building")
    parser.add_argument("--prepare-only", action="store_true", help="prepare the toolchain and export, without the app build")
    parser.add_argument("--install", action="store_true", help="build the app signed for your iPhone and install it there")
    parser.add_argument("--team", help="with --install: the development team, when Xcode has more than one")
    parser.add_argument("--device", help="with --install: the iPhone's UDID, when more than one is connected")
    options = parser.parse_args()
    if (options.team or options.device) and not options.install:
        parser.error("--team and --device go with --install")
    if options.install and (options.prepare_only or options.check):
        parser.error("--install builds and installs the app; leave out --prepare-only and --check")
    spec = json.loads(MANIFEST.read_text())
    environment = os.environ.copy()
    check_xcode(spec["xcode"], environment)
    print(f"Xcode {spec['xcode']} verified.", flush=True)
    if options.check:
        return
    if options.install:
        # Before any download, so a missing Apple ID or iPhone stops setup in seconds.
        install_app.configure(options.team, options.device)
    STATE.mkdir(parents=True, exist_ok=True)
    with (STATE / "setup.lock").open("w") as lock:
        try:
            fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise ValueError("Another setup is running in this checkout.") from None
        provision_dotnet(spec, environment)
        provision_library_builder(spec)
        provision_godot(spec["downloads"], environment)
        provision_native(spec)
        provision_builtin_mods(spec)
        provision_quickmatch_relay()
        pipeline(environment, options.prepare_only, options.install)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, subprocess.CalledProcessError) as error:
        print(f"Setup stopped: {error}", file=sys.stderr)
        sys.exit(1)
