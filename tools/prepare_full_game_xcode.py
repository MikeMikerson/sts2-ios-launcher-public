"""Adapt the generated full-game Xcode project for signed device installs.

Idempotent: every change is applied once and recognized on later runs. The script refuses a project
shape it has not been written for instead of guessing.
"""

import hashlib
import json
import os
import plistlib
import re
import shutil
import struct
import zlib
from pathlib import Path

from xcode_local import configure_project, local_settings

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "src/STS2Mobile.FullGame"
EXPORT = ROOT / "analysis/full-game-ios/export"
STARTUP_ARGUMENTS = ["--log-file", "user://full-game-startup.log"]
# Verbose engine logging records every resource load and shader cache miss. It costs time on the
# phone, so it is only for a diagnostic build: STS2_FULL_GAME_VERBOSE=1 ./setup.sh
VERBOSE_VARIABLE = "STS2_FULL_GAME_VERBOSE"
# The single icon image the asset catalog keeps; Xcode makes every smaller size from it.
APP_ICON = "AppIcon-1024.png"
# This build does not request camera or photo-library access. The microphone is used only by QuickMatch's
# voice chat (native/voice_audio.mm), asked for the first time the player turns their mic on in a match, so a
# build carrying QuickMatch gets this text in place of the engine template's, and a build without it gets none.
UNUSED_PURPOSE_KEYS = ("NSCameraUsageDescription", "NSPhotoLibraryUsageDescription", "NSMicrophoneUsageDescription")
MICROPHONE_KEY = "NSMicrophoneUsageDescription"
MICROPHONE_PURPOSE = (
    "Voice chat with your Quick Match teammates. The microphone is used only while you turn your mic "
    "on in a match, and your voice goes only to the other players of that match."
)
# The built-in voice chat's mod, as ./setup.sh stages it (tools/setup_ios.py).
VOICE_MOD = ROOT / ".tools/builtin-mods/QuickMatch.dll"

# Host-owned native sources under src/STS2Mobile.FullGame/native that the app must define.
NATIVE_SOURCES = ("fmod_static_plugins.cpp",)
# Host-owned sources that need their own compile unit (Objective-C++ cannot join the export's C++ unit).
# name -> (file reference id, build file id, Xcode file type).
NATIVE_UNITS = {
    "orientation_guard.mm": ("B55555555555555555555555", "B66666666666666666666666", "sourcecode.cpp.objcpp"),
    "power_state.mm": ("B77777777777777777777777", "B88888888888888888888888", "sourcecode.cpp.objcpp"),
    "scene_lifecycle.mm": ("B99999999999999999999999", "BAAAAAAAAAAAAAAAAAAAAAAA", "sourcecode.cpp.objcpp"),
    "mod_import.mm": ("BBBBBBBBBBBBBBBBBBBBBBBB", "BCCCCCCCCCCCCCCCCCCCCCCC", "sourcecode.cpp.objcpp"),
    "game_files.mm": ("BDDDDDDDDDDDDDDDDDDDDDDD", "BEEEEEEEEEEEEEEEEEEEEEEE", "sourcecode.cpp.objcpp"),
    "setup_overlay.mm": ("BFFFFFFFFFFFFFFFFFFFFFFF", "C00000000000000000000000", "sourcecode.cpp.objcpp"),
    "voice_audio.mm": ("C11111111111111111111111", "C22222222222222222222222", "sourcecode.cpp.objcpp"),
}
PHASE_FILES = {"Sources": NATIVE_UNITS}
# iOS 27 stops an app built with its SDK during launch unless it uses the scene life cycle. The class
# is defined in native/scene_lifecycle.mm and moves the engine's window onto the scene.
SCENE_DELEGATE_CLASS = "STS2SceneDelegate"
EXPORT_PROFILE = "godot-4.5.1-ios-full-game-export-v9"
TRANSFORMATIONS = [
    "Persist the full-game startup log; verbose engine logging only on request.",
    "Use a plain launch screen and a single opaque asset-catalog icon.",
    "Remove unused permission purpose strings.",
    "Strip archives to non-global symbols, keeping load_all_fmod_plugins for the FMOD extension.",
    "Define the FMOD static plugin loader the extension requires on iOS.",
    "Compile the orientation guard that answers iOS on the app delegate level: portrait launcher, landscape game.",
    "Compile the power state reader and the launch-time render resolution choice.",
    "Adopt the scene life cycle iOS 27 requires; the scene delegate takes over the engine window.",
    'Compile the Files picker behind the Mods screen\'s "Add mod from Files" button.',
    "Compile QuickMatch's voice audio; the microphone text only in a build that carries QuickMatch.",
]


def add_build_file(text: str, name: str, phase: str) -> str:
    """Add a file in the export's app folder to the app target's Resources or Sources build phase."""
    # Xcode rewrites the comment as FullGame/<name> when the project is saved there.
    if f"{name} in {phase} */" in text:
        return text
    file_id, build_id, file_type = PHASE_FILES[phase][name]
    text = text.replace(
        "/* Begin PBXFileReference section */",
        "/* Begin PBXFileReference section */\n"
        f"\t\t{file_id} /* {name} */ = {{isa = PBXFileReference; lastKnownFileType = {file_type}; "
        f"path = FullGame/{name}; sourceTree = SOURCE_ROOT; }};",
    )
    text = text.replace(
        "/* Begin PBXBuildFile section */",
        "/* Begin PBXBuildFile section */\n"
        f"\t\t{build_id} /* {name} in {phase} */ = {{isa = PBXBuildFile; fileRef = {file_id} /* {name} */; }};",
    )
    text, phases = re.subn(
        rf"(isa = PBX{phase}BuildPhase;.*?files = \(\n)",
        r"\1" + f"\t\t\t\t{build_id} /* {name} in {phase} */,\n",
        text,
        flags=re.DOTALL,
    )
    if phases != 1:
        raise ValueError(f"Unsupported full-game {phase} build phase")
    return text


def use_app_store_launch_packaging(text: str, properties: dict) -> str:
    """A plain launch screen, and an asset-catalog icon without transparency (Apple refuses an icon with an alpha channel)."""
    text, launch_count = re.subn(
        r"^\s+[A-F0-9]+ /\* Launch Screen\.storyboard in Resources \*/,\n", "", text, flags=re.MULTILINE
    )
    catalogs = re.findall(r"^\s+[A-F0-9]+ /\* Images\.xcassets in Resources \*/,$", text, flags=re.MULTILINE)
    if (
        launch_count not in (0, 1)
        or len(catalogs) != 1
        or text.count("ASSETCATALOG_COMPILER_APPICON_NAME = AppIcon;") != 2
    ):
        raise ValueError("Unsupported full-game launch/icon shape")
    properties.pop("UILaunchStoryboardName", None)
    properties["UILaunchScreen"] = {}
    # The compiled asset catalog supplies the icon entries.
    properties.pop("CFBundleIcons", None)
    properties.pop("CFBundleIcons~ipad", None)
    use_single_opaque_icon(EXPORT / "FullGame/Images.xcassets/AppIcon.appiconset")
    return text


def use_single_opaque_icon(icon_set: Path) -> None:
    """Replace the engine's icon set with one opaque image."""
    target = icon_set / APP_ICON
    if not target.exists():
        target.write_bytes(opaque_png((icon_set / "Icon-1024.png").read_bytes()))
    for generated in icon_set.glob("Icon-*.png"):
        generated.unlink()
    (icon_set / "Contents.json").write_text(
        json.dumps(
            {
                "images": [{"idiom": "universal", "platform": "ios", "size": "1024x1024", "filename": APP_ICON}],
                "info": {"author": "xcode", "version": 1},
            },
            indent=2,
        )
        + "\n"
    )


def unfilter_row(kind: int, line: bytearray, previous: bytearray, step: int) -> None:
    """Undo one PNG scanline filter in place (PNG specification, section 9)."""
    if kind == 0:
        return
    if kind == 2:
        line[:] = bytes((value + above) & 0xFF for value, above in zip(line, previous, strict=True))
        return
    if kind not in (1, 3, 4):
        raise ValueError("Corrupt app icon PNG")
    for i in range(len(line)):
        left = line[i - step] if i >= step else 0
        if kind == 1:
            predicted = left
        elif kind == 3:
            predicted = (left + previous[i]) >> 1
        else:
            above, corner = previous[i], previous[i - step] if i >= step else 0
            estimate = left + above - corner
            to_left, to_above, to_corner = abs(estimate - left), abs(estimate - above), abs(estimate - corner)
            predicted = (
                left if to_left <= to_above and to_left <= to_corner else above if to_above <= to_corner else corner
            )
        line[i] = (line[i] + predicted) & 0xFF


def opaque_png(data: bytes) -> bytes:
    """Return an 8-bit RGBA PNG composed over black as an 8-bit RGB PNG.

    Apple's tools refuse an app icon with an alpha channel. iOS already shows transparent icon
    pixels as black, so the icon looks as it did on the phone before."""
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError("The app icon is not a PNG")
    chunks, position = [], 8
    while position < len(data):
        length, kind = struct.unpack(">I4s", data[position : position + 8])
        chunks.append((kind, data[position + 8 : position + 8 + length]))
        position += 12 + length
    width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", chunks[0][1])
    if chunks[0][0] != b"IHDR" or (depth, interlace) != (8, 0) or color not in (2, 6):
        raise ValueError("Unsupported app icon PNG: expected 8-bit RGB or RGBA without interlacing")
    if color == 2:
        return data
    raw = zlib.decompress(b"".join(body for kind, body in chunks if kind == b"IDAT"))
    stride = width * 4
    if len(raw) != height * (stride + 1):
        raise ValueError("Corrupt app icon PNG")
    previous, rows = bytearray(stride), bytearray()
    for row in range(height):
        start = row * (stride + 1)
        line = bytearray(raw[start + 1 : start + 1 + stride])
        unfilter_row(raw[start], line, previous, 4)
        rgb = bytearray(line)
        del rgb[3::4]
        for pixel, alpha in enumerate(line[3::4]):
            if alpha != 255:
                for i in range(pixel * 3, pixel * 3 + 3):
                    rgb[i] = (rgb[i] * alpha + 127) // 255
        rows += b"\x00" + rgb
        previous = line

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    return (
        data[:8]
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(rows), 9))
        + chunk(b"IEND", b"")
    )


def fill_purpose_strings(properties: dict, with_voice: bool) -> None:
    for key in UNUSED_PURPOSE_KEYS:
        properties.pop(key, None)
    if with_voice:
        properties[MICROPHONE_KEY] = MICROPHONE_PURPOSE


def keep_exported_symbols(text: str) -> str:
    """Strip archives to non-global symbols only.

    An archive build strips the app, and for an application Xcode's default removes every global symbol
    too. The FMOD extension looks up load_all_fmod_plugins in the app at run time, so a fully stripped
    app crashes at startup jumping to address zero. tools/mono/build_mono.py passes the same setting
    for its stripped build."""
    setting = 'STRIP_STYLE = "non-global";'
    if text.count(setting) == 2:
        return text
    text, count = re.subn(
        r"^(\s+)(PRODUCT_BUNDLE_IDENTIFIER = [^;\n]+;\n)", r"\1\2\1" + setting + "\n", text, flags=re.MULTILINE
    )
    if count != 2 or "STRIP_STYLE = " in text.replace(setting, ""):
        raise ValueError("Unsupported full-game strip settings")
    return text


def adopt_scene_lifecycle(properties: dict) -> None:
    """Declare one application scene whose delegate is the host's scene delegate."""
    existing = properties.get("UIApplicationSceneManifest")
    manifest = {
        "UIApplicationSupportsMultipleScenes": False,
        "UISceneConfigurations": {
            "UIWindowSceneSessionRoleApplication": [
                {
                    "UISceneConfigurationName": "Default Configuration",
                    "UISceneDelegateClassName": SCENE_DELEGATE_CLASS,
                }
            ],
        },
    }
    if existing is not None and existing != manifest:
        raise ValueError("The export already declares a different scene manifest")
    properties["UIApplicationSceneManifest"] = manifest


def add_native_source(name: str) -> None:
    """Compile a host source file into the app through the export's own C++ unit."""
    unit = EXPORT / "FullGame/dummy.cpp"
    marker = f"// --- host native source: {name} ---"
    existing = unit.read_text()
    if marker not in existing:
        unit.write_text(existing.rstrip("\n") + f"\n\n{marker}\n" + (HOST / "native" / name).read_text())


def startup_arguments(environment=os.environ) -> list[str]:
    choice = environment.get(VERBOSE_VARIABLE, "0")
    if choice not in ("0", "1"):
        raise ValueError(f"{VERBOSE_VARIABLE} must be 0 or 1")
    return (["--verbose"] if choice == "1" else []) + STARTUP_ARGUMENTS


def add_host_files(text: str) -> str:
    """Compile the host's native sources into the app."""
    if "/* dummy.cpp in Sources */" not in text:
        raise ValueError("Unsupported full-game native source phase")
    for source in NATIVE_SOURCES:
        add_native_source(source)
    for unit in NATIVE_UNITS:
        # Copied on every run, so an edited source reaches the next build without a new export.
        shutil.copyfile(HOST / "native" / unit, EXPORT / "FullGame" / unit)
        text = add_build_file(text, unit, "Sources")
    return text


def prepare() -> None:
    project = EXPORT / "FullGame.xcodeproj/project.pbxproj"
    info = EXPORT / "FullGame/FullGame-Info.plist"
    original_project, original_info = project.read_bytes(), info.read_bytes()
    text = original_project.decode()
    properties = plistlib.loads(original_info)
    if properties.get("CFBundleIdentifier") != "$(PRODUCT_BUNDLE_IDENTIFIER)":
        raise ValueError("Unsupported full-game exported bundle")
    if "sentry" in text.lower():
        raise ValueError("Sentry dependency in generated full-game project")
    properties["godot_cmdline"] = startup_arguments()
    text = use_app_store_launch_packaging(text, properties)
    fill_purpose_strings(properties, VOICE_MOD.is_file())
    adopt_scene_lifecycle(properties)
    text = keep_exported_symbols(text)
    text = add_host_files(text)
    settings = local_settings()
    text = configure_project(text, settings)
    project.write_text(text)
    info.write_bytes(plistlib.dumps(properties, sort_keys=False))
    profile = {
        "profile": EXPORT_PROFILE,
        "inputProjectSha256": hashlib.sha256(original_project).hexdigest(),
        "outputProjectSha256": hashlib.sha256(project.read_bytes()).hexdigest(),
        "inputPlistSha256": hashlib.sha256(original_info).hexdigest(),
        "outputPlistSha256": hashlib.sha256(info.read_bytes()).hexdigest(),
        "startupArguments": properties["godot_cmdline"],
        "transformations": TRANSFORMATIONS,
        "bundle": settings["bundle"],
    }
    (EXPORT / "full-game-export-profile.json").write_text(json.dumps(profile, indent=2) + "\n")
    print(f"Prepared full-game packaging; startup arguments: {' '.join(properties['godot_cmdline'])}")


def main() -> None:
    try:
        prepare()
    except ValueError as error:
        raise SystemExit(str(error)) from None


if __name__ == "__main__":
    main()
