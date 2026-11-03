"""Build the app and its IPA from a tree where ./setup.sh --prepare-only has run. Run it with scripts/build-mono.sh.

The app holds no game file: a main pack of host files and none of the game's assemblies. The game goes into the app's data
folder.

Output goes to analysis/full-game-mono. The build has its own copy of the sources and its own package folder, because it
needs a different GodotSharp.dll from the export's. It always publishes from scratch: a stale AOT output is silently reused.
"""

from __future__ import annotations

import argparse
import json
import os
import shlex
import shutil
import subprocess
import sys
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE.parent))

import build_texture_encoder  # noqa: E402
import check_mono_build  # noqa: E402
import godot_pack  # noqa: E402
import mono_library as library  # noqa: E402
import pinvoke_table  # noqa: E402
import scan_no_game_files  # noqa: E402
from dotnet_environment import dotnet_environment  # noqa: E402
from hashing import sha256_file  # noqa: E402

LOGS = ROOT / ".work/logs"
LOG_PREFIX = "mono"
OUT = check_mono_build.OUT
TREE, PACKAGES, PUBLISH, EXPORT = OUT / "tree", OUT / "packages", OUT / "publish", OUT / "export"
PATCHED_LIBRARY, APP, IPA = OUT / "STS2Mobile.FullGame", OUT / "FullGame.app", OUT / "FullGame.ipa"
PUBLISH_LOG = LOGS / f"{LOG_PREFIX}-publish.log"
GAME_FILES = ROOT / "analysis/game-files"
PINS = json.loads((ROOT / "tools/dependencies.json").read_text())["mono"]
# The host export (scripts/export-host.sh): the Xcode project and the main pack this build starts from.
HOST_EXPORT = ROOT / "analysis/full-game-ios/export"
SOURCE_PACKAGES = ROOT / ".tools/nuget/packages"
DOTNET = ROOT / "tools/ios-dotnet"
PACK_TARGETS = (
    DOTNET / "packs/Microsoft.NET.Runtime.LibraryBuilder.Sdk" / PINS["runtime_version"] / "Sdk/AppleBuild.targets"
)
FRAMEWORK_BINARY = (
    "FullGame/dylibs/ExportRelease/STS2Mobile.FullGame_aot.xcframework/ios-arm64/"
    "STS2Mobile.FullGame.framework/STS2Mobile.FullGame"
)
INSTALL_NAME = "@rpath/STS2Mobile.FullGame.framework/STS2Mobile.FullGame"
PUBLISH_PROPERTIES = [
    "GodotTargetPlatform=ios",
    "TreatWarningsAsErrors=false",
    "PublishAot=false",
    "PublishAotUsingRuntimePack=false",
    "UseNativeAOTRuntime=false",
    "UseMonoRuntime=true",
    "RunAOTCompilation=true",
    "MonoNativeLib=Shared",
    # The pack's Sdk.props forces an executable and trimming, which fails on a library.
    "OutputType=Library",
    "GenerateRuntimeConfigurationFiles=true",
    "PublishTrimmed=false",
    # Workaround 1: the AOT compiler asserts in its dedup step.
    "iOSLikeDedup=false",
    "MonoRemovedAssemblies=" + "%3B".join(check_mono_build.REMOVED_ASSEMBLIES),
]
# The engine's executable: strip it as an archive build would, keeping what it exports.
STRIP_SETTINGS = ["DEPLOYMENT_POSTPROCESSING=YES", "STRIP_INSTALLED_PRODUCT=YES", "STRIP_STYLE=non-global"]


def environment(packages: Path = SOURCE_PACKAGES) -> dict:
    return dotnet_environment(packages=packages) | {"MSBUILDDISABLENODEREUSE": "1"}


def run(command: list[str], label: str, env: dict | None = None, cwd: Path = ROOT, log: Path | None = None) -> None:
    log = log or LOGS / f"{LOG_PREFIX}-{label}.log"
    log.parent.mkdir(parents=True, exist_ok=True)
    with log.open("a") as output:
        result = subprocess.run(
            command,
            cwd=cwd,
            env=env or environment(),
            stdout=output,
            stderr=subprocess.STDOUT,
        )
    if result.returncode:
        raise RuntimeError(f"Mono build step {label} failed. See {log.relative_to(ROOT)}")


def step(label: str) -> None:
    print(f"{time.strftime('%H:%M:%S')} {label}", flush=True)


def clone(source: Path, destination: Path) -> None:
    """Copy a tree with APFS clones: instant, and the copy can change without touching the original."""
    subprocess.run(["cp", "-cR", str(source), str(destination)], check=True)


def check_prerequisites() -> None:
    if not (HOST_EXPORT / "FullGame.xcodeproj").is_dir():
        raise ValueError("The export is missing. Run ./setup.sh --prepare-only first.")
    if not PACK_TARGETS.is_file() or sha256_file(PACK_TARGETS) != PINS["apple_build_targets"]["output_sha256"]:
        raise ValueError("The LibraryBuilder pack is missing or changed. Run ./setup.sh first.")
    # The same check the Xcode project runs: this export is the one made from the present sources.
    run(["sh", "scripts/check-xcode-inputs.sh"], "inputs")


def signing_arguments(unsigned: bool) -> list[str]:
    if unsigned:
        return [
            "-target",
            "FullGame",
            "-sdk",
            "iphoneos",
            "ARCHS=arm64",
            "ONLY_ACTIVE_ARCH=YES",
            f"CONFIGURATION_BUILD_DIR={OUT / 'xcode/Build/Products/Release-iphoneos'}",
            "CODE_SIGNING_ALLOWED=NO",
            "CODE_SIGNING_REQUIRED=NO",
            "DEVELOPMENT_TEAM=",
        ]
    device = ROOT / ".local/device.json"
    if not device.is_file():
        raise ValueError("Signing needs .local/device.json with team and dev_bundle_id. To skip signing, pass --unsigned.")
    values = json.loads(device.read_text())
    # With the iPhone named, Xcode registers it with the team: a free Apple ID's team gets no profile without one.
    destination = f"id={values['device_udid']}" if values.get("device_udid") else "generic/platform=iOS"
    return [
        "-scheme",
        "FullGame",
        "-destination",
        destination,
        "-derivedDataPath",
        str(OUT / "xcode"),
        "-allowProvisioningUpdates",
        "-allowProvisioningDeviceRegistration",
        f"DEVELOPMENT_TEAM={values['team']}",
        f"PRODUCT_BUNDLE_IDENTIFIER={values['dev_bundle_id']}",
        "CODE_SIGN_STYLE=Automatic",
    ]


def prepare_tree() -> None:
    """A copy of the sources, so this build's obj and bin folders stay apart from the host export's."""
    step("Copying the sources")
    shutil.rmtree(TREE, ignore_errors=True)
    TREE.mkdir(parents=True)
    shutil.copytree(ROOT / "src", TREE / "src", ignore=shutil.ignore_patterns(".godot", "bin", "obj"), symlinks=True)
    for name in ("global.json", "Directory.Build.props", ".editorconfig"):
        shutil.copy2(ROOT / name, TREE / name)
    # global.json finds the SDK at this path.
    (TREE / "tools").mkdir()
    (TREE / "tools/ios-dotnet").symlink_to(DOTNET)
    # The patcher embeds these (src/STS2Mobile.GamePatcher).
    shutil.copy2(ROOT / "tools/dependencies.json", TREE / "tools/dependencies.json")
    shutil.copytree(ROOT / "mods", TREE / "mods")
    # The built-in mods' files, embedded by the patcher and compiled against by the host, when setup fetched them.
    if (ROOT / ".tools/builtin-mods").is_dir():
        shutil.copytree(ROOT / ".tools/builtin-mods", TREE / ".tools/builtin-mods")


def patch_godotsharp() -> None:
    """Workarounds 3, 7 and 10, on a private copy of the package folder."""
    step("Patching GodotSharp")
    pins = PINS["godotsharp"]
    relative = Path(f"godotsharp/{pins['version']}/lib/net8.0/GodotSharp.dll")
    original = SOURCE_PACKAGES / relative
    if sha256_file(original) != pins["input_sha256"]:
        raise ValueError(f"Unexpected {relative}; the GodotSharp rewrites were not applied.")
    run(["dotnet", "build", "tools/mono/MonoBuildTool", "-v:q"], "tool")
    tool = str(check_mono_build.TOOL)
    work = OUT / "godotsharp"
    shutil.rmtree(work, ignore_errors=True)
    work.mkdir(parents=True)
    run([tool, "strip-fnptr-mods", str(original), str(work / "stripped.dll")], "godotsharp")
    run([tool, "rewrite-callables", str(work / "stripped.dll"), str(work / "GodotSharp.dll")], "godotsharp")
    if sha256_file(work / "GodotSharp.dll") != pins["output_sha256"]:
        raise ValueError("The GodotSharp rewrites gave an unexpected result.")
    shutil.rmtree(PACKAGES, ignore_errors=True)
    clone(SOURCE_PACKAGES, PACKAGES)
    (PACKAGES / relative).unlink()
    shutil.copy2(work / "GodotSharp.dll", PACKAGES / relative)


def publish() -> None:
    step("Publishing (AOT compiles the .NET libraries, about 2.5 minutes)")
    shutil.rmtree(PUBLISH, ignore_errors=True)
    PUBLISH_LOG.unlink(missing_ok=True)
    command = [
        "dotnet",
        "publish",
        "src/STS2Mobile.FullGame/STS2Mobile.FullGame.csproj",
        "-c",
        "ExportRelease",
        "-r",
        "ios-arm64",
        "--self-contained",
        "true",
        "-v:n",
        "-o",
        str(PUBLISH),
    ] + [f"-p:{value}" for value in PUBLISH_PROPERTIES]
    run(command, "publish", environment(PACKAGES), TREE, PUBLISH_LOG)


def edit_file(path: Path, edit) -> None:
    text = path.read_text()
    edited = edit(text)
    if edited != text:
        path.write_text(edited)


def relink() -> Path:
    """Replay the library builder's link with the startup mode, alignment and extra C files (workarounds 6, 8, 9)."""
    step("Relinking (about 20 seconds)")
    link, directory, name_id = library.parse_link_commands(PUBLISH_LOG.read_text(errors="replace"))
    declared = subprocess.run(
        [str(check_mono_build.TOOL), "list-pinvokes", *map(str, sorted(PUBLISH.glob("*.dll")))],
        env=environment(),
        capture_output=True,
        text=True,
        check=True,
    ).stdout.splitlines()
    if not build_texture_encoder.IOS_ARCHIVE.is_file():
        raise ValueError("The texture encoder is missing. Run ./setup.sh --prepare-only first.")
    libraries, defined = pinvoke_table.archive_symbols([*library.linked_archives(link), build_texture_encoder.IOS_ARCHIVE])
    entries = pinvoke_table.table_entries(declared, libraries, defined)
    if not entries:
        raise ValueError("The P/Invoke table came out empty.")
    table = OUT / "pinvoke-table.c"
    table.write_text(pinvoke_table.render(entries))
    edit_file(directory / "autoinit.c", library.edit_autoinit)
    resources = sorted(directory.glob("bundled_resource_*.c"))
    if not resources:
        raise ValueError(f"No embedded assemblies in {directory}.")
    for resource in resources:
        edit_file(resource, lambda text, name=resource.name: library.align_bundled_resource(text, name))
    sources = [HERE / "invoke_class_init.c", table, HERE / "game_assembly_hook.c", build_texture_encoder.IOS_ARCHIVE]
    log = LOGS / f"{LOG_PREFIX}-relink.log"
    log.unlink(missing_ok=True)
    split = library.split_link(library.add_link_sources(link, sources))
    objects = compile_sources(split, directory, log)
    run(split.object_link(objects), "relink", cwd=directory, log=log)
    run(shlex.split(name_id), "relink", cwd=directory, log=log)
    return directory / "libSTS2Mobile.FullGame.dylib"


def compile_sources(split: library.SplitLink, directory: Path, log: Path) -> dict[int, Path]:
    """Compile the link's sources in parallel, into the link's folder, which every publish makes anew.

    No object is kept from one build to the next: the AOT compiler writes a random module id into every .s file and compile-time
    addresses into System.Private.CoreLib's, so only the embedded assemblies' C files would ever be reused."""
    folder = directory / "objects"
    shutil.rmtree(folder, ignore_errors=True)
    folder.mkdir()
    objects = {index: folder / f"{index:03d}-{Path(split.arguments[index]).stem}.o" for index in split.sources}

    def compile_one(index: int) -> tuple[bool, str]:
        source = directory / split.arguments[index]
        result = subprocess.run(
            split.compile_command(source, objects[index]), cwd=directory, capture_output=True, text=True, env=environment()
        )
        output = result.stdout + result.stderr
        return result.returncode == 0, f"{source.name}: exit {result.returncode}\n{output}" if result.returncode or output else ""

    with ThreadPoolExecutor(os.cpu_count()) as pool:
        results = list(pool.map(compile_one, split.sources))
    with log.open("a") as output:
        output.write("".join(text for _, text in results))
    if not all(ok for ok, _ in results):
        raise RuntimeError(f"Mono build step relink failed to compile a source. See {log.relative_to(ROOT)}")
    return objects


def patch_library(linked: Path) -> None:
    """Workarounds 4 and 6."""
    step("Patching the library")
    if sha256_file(PUBLISH / "libmonosgen-2.0.a") != PINS["libmonosgen_sha256"]:
        raise ValueError("Unexpected libmonosgen-2.0.a; the binary patches were written for another Mono build.")
    data = bytearray(linked.read_bytes())
    library.check_code_at_file_offsets(data)
    symbols = library.Symbols(subprocess.run(["nm", str(linked)], capture_output=True, text=True, check=True).stdout)
    library.patch_key_from_id(data, symbols)
    library.patch_class_init(data, symbols)
    PATCHED_LIBRARY.write_bytes(data)
    PATCHED_LIBRARY.chmod(0o755)


def build_app(unsigned: bool) -> None:
    step("Building the app with Xcode")
    arguments = signing_arguments(unsigned)
    shutil.rmtree(EXPORT, ignore_errors=True)
    # The same depth as the original export, so its project finds the repository at the same relative path.
    clone(HOST_EXPORT, EXPORT)
    shutil.rmtree(EXPORT / "build", ignore_errors=True)
    framework = EXPORT / FRAMEWORK_BINARY
    if not framework.is_file():
        raise ValueError(f"The export has no framework at {FRAMEWORK_BINARY}.")
    framework.unlink()
    shutil.copy2(PATCHED_LIBRARY, framework)
    # Only the symbol table goes: code and data stay at the same offsets. PATCHED_LIBRARY keeps the symbols, for the check and
    # for reading crash reports. Every symbol that goes is local, so nothing at run time could look it up.
    run(["xcrun", "strip", "-x", "-no_code_signature_warning", str(framework)], "xcode")
    run(["install_name_tool", "-id", INSTALL_NAME, str(framework)], "xcode")
    # The export's pack, with the settings that mount the game.
    write_main_pack(HOST_EXPORT / "FullGame.pck", EXPORT / "FullGame.pck")
    log = LOGS / f"{LOG_PREFIX}-xcode.log"
    log.unlink(missing_ok=True)
    command = ["xcodebuild", "-project", "FullGame.xcodeproj", "-configuration", "Release", *arguments, *STRIP_SETTINGS, "build"]
    run(command, "xcode", cwd=EXPORT, log=log)
    shutil.rmtree(APP, ignore_errors=True)
    clone(OUT / "xcode/Build/Products/Release-iphoneos/FullGame.app", APP)


def write_main_pack(host_pack: Path, output: Path) -> None:
    files = dict(godot_pack.read_pack(host_pack))
    files["project.binary"] = library.game_mount_settings(files["project.binary"])
    with output.open("wb") as stream:
        godot_pack.write_pack(stream, files.items())


def write_ipa() -> None:
    step("Writing the IPA")
    payload = OUT / "ipa"
    shutil.rmtree(payload, ignore_errors=True)
    (payload / "Payload").mkdir(parents=True)
    clone(APP, payload / "Payload/FullGame.app")
    IPA.unlink(missing_ok=True)
    subprocess.run(["ditto", "-c", "-k", "--norsrc", "--keepParent", str(payload / "Payload"), str(IPA)], check=True)
    shutil.rmtree(payload)


def scan_ipa() -> None:
    """Fail on any game file in what this build produced."""
    step("Scanning the IPA for game files")
    reference = GAME_FILES if (GAME_FILES / "original/sts2.dll").is_file() else None
    problems = scan_no_game_files.scan(IPA, reference)
    for problem in problems:
        print(f"    {problem}", flush=True)
    if problems:
        raise ValueError("The IPA holds game files.")
    print("ok  no game files in the IPA" + (" (also compared with analysis/game-files)" if reference else ""), flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unsigned", action="store_true", help="build without signing, for a check of the output only")
    parser.add_argument("--reuse-publish", action="store_true", help="skip the publish and repeat the steps after it")
    options = parser.parse_args()
    signing_arguments(options.unsigned)  # stop now, not after the publish, when signing values are missing
    check_prerequisites()
    if not options.reuse_publish:
        prepare_tree()
        patch_godotsharp()
        publish()
    elif not PUBLISH_LOG.is_file():
        raise ValueError(f"No publish log to reuse: {PUBLISH_LOG.relative_to(ROOT)}")
    patch_library(relink())
    build_app(options.unsigned)
    step("Checking the output")
    if check_mono_build.check(APP, require_signature=not options.unsigned):
        raise ValueError("The built app failed its check.")
    write_ipa()
    scan_ipa()
    print(f"Ready: {IPA.relative_to(ROOT)}")


if __name__ == "__main__":
    try:
        main()
    except (ValueError, RuntimeError, OSError, subprocess.CalledProcessError) as error:
        print(f"Mono build stopped: {error}", file=sys.stderr)
        sys.exit(1)
