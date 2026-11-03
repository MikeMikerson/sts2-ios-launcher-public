"""Stage the prebuilt FMOD device extensions, and the macOS wrapper the exporting editor loads, with a provenance manifest."""

import argparse
import json
import shutil
import zipfile
from pathlib import Path

from hashing import sha256_file

EXPECTED_VERSION = "2.03.06"
DEVICE_LIBRARIES = (
    "libfmod_iphoneos.a",
    "libfmodL_iphoneos.a",
    "libfmodstudio_iphoneos.a",
    "libfmodstudioL_iphoneos.a",
)
# Godot's editor loads the extension to export the project. These never reach the app.
MACOS_WRAPPER = "libGodotFmod.macos.template_release.framework"
MACOS_LIBRARIES = ("libfmod.dylib", "libfmodstudio.dylib")


def stage_binaries(addon_archive: Path, output: Path, godot_license: Path) -> dict:
    """Copy verified archive inputs into a new directory; the app needs no SDK headers."""
    output = output.resolve()
    if output.exists():
        raise ValueError("Output must be a new directory")
    if not godot_license.is_file():
        raise ValueError("Missing integration license")
    output.mkdir(parents=True)
    try:
        with zipfile.ZipFile(addon_archive) as archive:
            prefix = "fmod/libs/iOS/"
            for entry in archive.infolist():
                if entry.is_dir() or not entry.filename.startswith(prefix):
                    continue
                name = entry.filename[len(prefix) :]
                if name in DEVICE_LIBRARIES:
                    relative = Path("lib/device") / name
                elif name.startswith("libGodotFmod.ios."):
                    relative = Path("gdextension/ios") / name
                else:
                    continue
                target = (output / relative).resolve()
                if not target.is_relative_to(output):
                    raise ValueError("Unsafe archive member")
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(archive.read(entry))
            prefix = "fmod/libs/macos/"
            for entry in archive.infolist():
                if entry.is_dir() or not entry.filename.startswith(prefix):
                    continue
                name = entry.filename[len(prefix) :]
                if name in MACOS_LIBRARIES or name.startswith(MACOS_WRAPPER + "/"):
                    target = (output / "gdextension/macos" / name).resolve()
                    if not target.is_relative_to(output):
                        raise ValueError("Unsafe archive member")
                    target.parent.mkdir(parents=True, exist_ok=True)
                    target.write_bytes(archive.read(entry))
        for library in MACOS_LIBRARIES:
            if not (output / "gdextension/macos" / library).is_file():
                raise ValueError(f"Missing macOS library: {library}")
        for library in DEVICE_LIBRARIES:
            if not (output / "lib/device" / library).is_file():
                raise ValueError(f"Missing device library: {library}")
        if not list((output / "gdextension/ios").glob("*.xcframework/Info.plist")):
            raise ValueError("Missing FMOD extension metadata")
        shutil.copy2(godot_license, output / "GODOT_FMOD_LICENSE.txt")
        files = [
            {"path": str(path.relative_to(output)), "sha256": sha256_file(path)}
            for path in sorted(output.rglob("*"))
            if path.is_file()
        ]
        report = {
            "version": EXPECTED_VERSION,
            "binaries_source": "https://github.com/utopia-rise/fmod-gdextension/releases/tag/6.1.0-4.5.0",
            "archive_sha256": sha256_file(addon_archive),
            "files": files,
            "scope": "Prebuilt device libraries; no SDK headers.",
        }
        (output / "manifest.json").write_text(json.dumps(report, indent=2) + "\n")
        return report
    except Exception:
        shutil.rmtree(output)
        raise


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--addon-archive", type=Path, required=True)
    parser.add_argument("--godot-license", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    try:
        result = stage_binaries(args.addon_archive, args.output, args.godot_license)
    except ValueError as error:
        raise SystemExit(str(error)) from None
    print(f"Staged FMOD {result['version']}: {len(result['files'])} files")


if __name__ == "__main__":
    main()
