"""Make a simulator app from the built device app, install it, and copy the game files in. Run it with scripts/simulator.sh.

The simulator app is the device app's own binaries, marked for the arm64 simulator and signed ad hoc, not a second build. A real
simulator build cannot exist from these inputs: Godot's template has an x86_64 simulator library, and FMOD's and Spine's
extensions have device builds only. This way every Mono patch and all the AOT code are the ones the phone runs. One library is
added, simulator_metal_shim.m, because the simulator's Metal refuses the renderer otherwise.

It runs on the Mac's GPU and memory, so it says nothing about the phone's speed, heat or memory limits. The output is for local
testing and holds no game file: those go into the simulator's data folder.
"""

from __future__ import annotations

import argparse
import json
import plistlib
import shutil
import struct
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]

DEVICE_APP = ROOT / "analysis/full-game-mono/FullGame.app"
APP = ROOT / "analysis/full-game-simulator/FullGame.app"
GAME_FILES = ROOT / "analysis/game-files"
DEVICE_NAME = "iPhone 18 Pro"
MINIMUM_OS, SDK_VERSION = "15.0", "27.0"
SHIM_SOURCE = HERE / "simulator_metal_shim.m"
SHIM_NAME = "libsts2_simulator_metal.dylib"

MH_MAGIC_64 = 0xFEEDFACF
LC_SEGMENT_64 = 0x19
LC_LOAD_DYLIB = 0x0C
MACH_HEADER_SIZE = 32
SECTION_SIZE = 80
SECTION_TYPE_MASK = 0xFF
# S_ZEROFILL, S_GB_ZEROFILL, S_THREAD_LOCAL_ZEROFILL
ZERO_FILL_TYPES = {0x01, 0x0C, 0x12}


class SimulatorError(ValueError):
    pass


def is_mach_o(path: Path) -> bool:
    with path.open("rb") as stream:
        return stream.read(4) == struct.pack("<I", MH_MAGIC_64)


def clear_zero_fill_offsets(data: bytearray) -> int:
    """The simulator's dyld refuses a zero-fill section with a file offset, and FMOD's extension has one. Return how many it cleared."""
    magic, _, _, _, count, _, _, _ = struct.unpack_from("<8I", data, 0)
    if magic != MH_MAGIC_64:
        raise SimulatorError("Not a thin 64-bit Mach-O file.")
    cleared, offset = 0, 32
    for _ in range(count):
        command, size = struct.unpack_from("<2I", data, offset)
        if command == LC_SEGMENT_64:
            (section_count,) = struct.unpack_from("<I", data, offset + 64)
            for index in range(section_count):
                at = offset + 72 + index * 80
                (file_offset,) = struct.unpack_from("<I", data, at + 48)
                (flags,) = struct.unpack_from("<I", data, at + 64)
                if flags & SECTION_TYPE_MASK in ZERO_FILL_TYPES and file_offset:
                    struct.pack_into("<I", data, at + 48, 0)
                    cleared += 1
        offset += size
    return cleared


def add_load_dylib(data: bytearray, name: str) -> None:
    """Add a library to what a binary loads, in the free space after its load commands, so the app also starts from the home screen."""
    magic, _, _, _, count, size, _, _ = struct.unpack_from("<8I", data, 0)
    if magic != MH_MAGIC_64:
        raise SimulatorError("Not a thin 64-bit Mach-O file.")
    first_content, offset = len(data), MACH_HEADER_SIZE
    for _ in range(count):
        command, command_size = struct.unpack_from("<2I", data, offset)
        if command == LC_SEGMENT_64:
            (section_count,) = struct.unpack_from("<I", data, offset + 64)
            for index in range(section_count):
                (file_offset,) = struct.unpack_from("<I", data, offset + 72 + index * 80 + 48)
                if file_offset:
                    first_content = min(first_content, file_offset)
        offset += command_size
    encoded = name.encode() + b"\0"
    command_size = 24 + (len(encoded) + 7) // 8 * 8
    if offset + command_size > first_content:
        raise SimulatorError("The binary has no room for another load command.")
    data[offset : offset + command_size] = struct.pack("<6I", LC_LOAD_DYLIB, command_size, 24, 2, 0x10000, 0x10000) + encoded.ljust(command_size - 24, b"\0")
    struct.pack_into("<2I", data, 16, count + 1, size + command_size)


def add_entitlements_section(data: bytearray, entitlements: bytes) -> None:
    """Without entitlements the simulator's keychain answers -34018. It reads them from a __TEXT,__entitlements section of the
    executable, as Xcode's simulator builds have, and ad hoc signing refuses them. The plist goes in the free space before the code."""
    magic, _, _, _, count, size, _, _ = struct.unpack_from("<8I", data, 0)
    if magic != MH_MAGIC_64:
        raise SimulatorError("Not a thin 64-bit Mach-O file.")
    text, first_content, offset = None, len(data), MACH_HEADER_SIZE
    for _ in range(count):
        command, command_size = struct.unpack_from("<2I", data, offset)
        if command == LC_SEGMENT_64:
            if bytes(data[offset + 8 : offset + 24]).rstrip(b"\0") == b"__TEXT":
                text = offset
            (section_count,) = struct.unpack_from("<I", data, offset + 64)
            for index in range(section_count):
                (file_offset,) = struct.unpack_from("<I", data, offset + 72 + index * 80 + 48)
                if file_offset:
                    first_content = min(first_content, file_offset)
        offset += command_size
    if text is None:
        raise SimulatorError("The binary has no __TEXT segment.")
    commands_end = MACH_HEADER_SIZE + size
    position = (first_content - len(entitlements)) & ~7
    if commands_end + SECTION_SIZE > position or any(data[commands_end:first_content]):
        raise SimulatorError("The binary has no room for its entitlements.")
    (vmaddr,) = struct.unpack_from("<Q", data, text + 24)
    (file_offset,) = struct.unpack_from("<Q", data, text + 40)
    header = (
        b"__entitlements".ljust(16, b"\0")
        + b"__TEXT".ljust(16, b"\0")
        + struct.pack("<2Q", vmaddr + position - file_offset, len(entitlements))
        + struct.pack("<8I", position, 0, 0, 0, 0, 0, 0, 0)
    )
    data[position : position + len(entitlements)] = entitlements
    # First in the segment: the lowest address of its sections.
    at = text + 72
    data[at + SECTION_SIZE : commands_end + SECTION_SIZE] = data[at:commands_end]
    data[at : at + SECTION_SIZE] = header
    (command_size,) = struct.unpack_from("<I", data, text + 4)
    (section_count,) = struct.unpack_from("<I", data, text + 64)
    struct.pack_into("<I", data, text + 4, command_size + SECTION_SIZE)
    struct.pack_into("<I", data, text + 64, section_count + 1)
    struct.pack_into("<I", data, 20, size + SECTION_SIZE)


def simulator_entitlements(bundle: str) -> bytes:
    identifier = f"FAKETEAMID.{bundle}"
    return plistlib.dumps({"application-identifier": identifier, "keychain-access-groups": [identifier]})


def build_shim(app: Path) -> None:
    subprocess.run(
        [
            "xcrun",
            "--sdk",
            "iphonesimulator",
            "clang",
            "-arch",
            "arm64",
            f"-mios-simulator-version-min={MINIMUM_OS}",
            "-dynamiclib",
            "-fobjc-arc",
            "-framework",
            "Foundation",
            "-framework",
            "Metal",
            "-install_name",
            f"@rpath/{SHIM_NAME}",
            str(SHIM_SOURCE),
            "-o",
            str(app / "Frameworks" / SHIM_NAME),
        ],
        check=True,
        capture_output=True,
    )


def retag(path: Path) -> None:
    temporary = path.with_name(path.name + ".simulator")
    subprocess.run(
        ["vtool", "-arch", "arm64", "-set-build-version", "iossim", MINIMUM_OS, SDK_VERSION, "-replace", "-output", str(temporary), str(path)],
        check=True,
        capture_output=True,
    )
    data = bytearray(temporary.read_bytes())
    if clear_zero_fill_offsets(data):
        temporary.write_bytes(data)
    temporary.chmod(path.stat().st_mode)
    temporary.replace(path)


def edit_info(app: Path) -> None:
    path = app / "Info.plist"
    info = plistlib.loads(path.read_bytes())
    info |= {"CFBundleSupportedPlatforms": ["iPhoneSimulator"], "DTPlatformName": "iphonesimulator", "DTSDKName": f"iphonesimulator{SDK_VERSION}"}
    path.write_bytes(plistlib.dumps(info))


def sign(path: Path) -> None:
    subprocess.run(["codesign", "--force", "--sign", "-", str(path)], check=True, capture_output=True)


def make_app() -> None:
    if not DEVICE_APP.is_dir():
        raise SimulatorError("The device app is missing. Run scripts/build-mono.sh first (--unsigned is enough).")
    shutil.rmtree(APP, ignore_errors=True)
    APP.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["cp", "-cR", str(DEVICE_APP), str(APP)], check=True)
    (APP / "embedded.mobileprovision").unlink(missing_ok=True)
    binaries = [path for path in APP.rglob("*") if path.is_file() and is_mach_o(path)]
    if not binaries:
        raise SimulatorError("The device app has no binaries.")
    for binary in binaries:
        retag(binary)
    executable = APP / "FullGame"
    data = bytearray(executable.read_bytes())
    add_load_dylib(data, f"@rpath/{SHIM_NAME}")
    add_entitlements_section(data, simulator_entitlements(bundle_id()))
    executable.write_bytes(data)
    build_shim(APP)
    edit_info(APP)
    for framework in sorted((APP / "Frameworks").iterdir()):
        sign(framework)
    sign(APP)
    print(f"Ready: {APP.relative_to(ROOT)} ({len(binaries)} binaries retagged)", flush=True)


def simctl(*arguments: str) -> str:
    return subprocess.run(["xcrun", "simctl", *arguments], check=True, capture_output=True, text=True).stdout.strip()


def find_device() -> str:
    """The named simulator on the newest runtime that has it."""
    runtimes = json.loads(simctl("list", "devices", "available", "--json"))["devices"]
    for runtime in sorted(runtimes, reverse=True):
        for device in runtimes[runtime]:
            if device["name"] == DEVICE_NAME:
                return device["udid"]
    raise SimulatorError(f"No {DEVICE_NAME} simulator. Add one in Device Hub.")


def bundle_id() -> str:
    return plistlib.loads((APP / "Info.plist").read_bytes())["CFBundleIdentifier"]


def install(device: str) -> None:
    subprocess.run(["xcrun", "simctl", "boot", device], capture_output=True)  # fails harmlessly when it is already booted
    simctl("bootstatus", device, "-b")
    simctl("install", device, str(APP))
    print(f"Installed on {DEVICE_NAME} ({device})", flush=True)


def copy_game_files(device: str) -> None:
    """Into Documents/game, as scripts/prepare-game-files.sh's output goes to the phone. Clones, so it takes no space or time."""
    if not (GAME_FILES / "original").is_dir():
        raise SimulatorError("No game files. Run scripts/prepare-game-files.sh first.")
    documents = Path(simctl("get_app_container", device, bundle_id(), "data")) / "Documents"
    game = documents / "game"
    game.mkdir(parents=True, exist_ok=True)
    subprocess.run(["cp", "-cR", f"{GAME_FILES}/.", str(game)], check=True)
    print(f"Game files copied to {game}", flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--no-install", action="store_true", help="only make the app")
    parser.add_argument("--game-files", action="store_true", help="also copy analysis/game-files into the app's data folder")
    parser.add_argument(
        "--print-target", action="store_true", help="only print the simulator's UDID and the simulator app's bundle id"
    )
    options = parser.parse_args()
    if options.print_target:
        if not (APP / "Info.plist").is_file():
            raise SimulatorError("No simulator app. Run scripts/simulator.sh first.")
        print(find_device(), bundle_id())
        return
    make_app()
    if options.no_install:
        return
    device = find_device()
    install(device)
    if options.game_files:
        copy_game_files(device)


if __name__ == "__main__":
    try:
        main()
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        print(f"Simulator app stopped: {error}", file=sys.stderr)
        sys.exit(1)
