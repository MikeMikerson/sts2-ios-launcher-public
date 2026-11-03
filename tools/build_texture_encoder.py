"""Build the phone's texture encoder (astc-encoder and bcdec, src/STS2Mobile.GamePack/native) into vendor/texture-encoder.

Two outputs from one set of flags: a static library for the iPhone, which build_mono.py links into the Mono library, and a dylib
for the Mac's tests. The flags are part of the result: the Mac's bytes depend on them.
"""

from __future__ import annotations

import subprocess
import tarfile
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "vendor/texture-encoder"
LIBRARY = "libsts2_texture_encoder"
IOS_ARCHIVE = OUTPUT / f"ios/{LIBRARY}.a"
MAC_LIBRARY = OUTPUT / f"macos/{LIBRARY}.dylib"
ENCODER_SOURCE = ROOT / "src/STS2Mobile.GamePack/native/texture_encoder.cpp"
# Godot's list, from modules/astcenc/SCsub.
ASTC_SOURCES = (
    "averages_and_directions",
    "block_sizes",
    "color_quantize",
    "color_unquantize",
    "compress_symbolic",
    "compute_variance",
    "decompress_symbolic",
    "diagnostic_trace",
    "entry",
    "find_best_partitioning",
    "ideal_endpoints_and_weights",
    "image",
    "integer_sequence",
    "mathlib",
    "mathlib_softfloat",
    "partition_tables",
    "percentile_tables",
    "pick_best_endpoint_format",
    "quantization",
    "symbolic_physical",
    "weight_align",
    "weight_quant_xfer_tables",
)
# -ffp-contract=off: without it only 24 of the 40 largest textures match the Mac's output. Hidden symbols: the engine's library holds
# astcenc objects of its own, and only the four functions this library declares are for anyone else.
FLAGS = (
    "-std=c++17",
    "-O2",
    "-ffp-contract=off",
    "-fvisibility=hidden",
    "-fvisibility-inlines-hidden",
    "-DASTCENC_NEON=1",
    "-DASTCENC_SVE=0",
    "-DASTCENC_SSE=0",
    "-DASTCENC_AVX=0",
    "-DASTCENC_POPCNT=0",
    "-DASTCENC_F16C=0",
)
# The Mono library's own minimum (the publish log's link command).
TARGETS = {"ios": ("iphoneos", "-miphoneos-version-min=12.2"), "macos": ("macosx", "-mmacosx-version-min=13.0")}


def unpack_astc(archive: Path, destination: Path) -> Path:
    """The archive's Source folder and license, with nothing else from it. Returns the folder holding them."""
    with tarfile.open(archive) as tar:
        members = [
            member
            for member in tar.getmembers()
            if member.isfile() and member.name.count("/") >= 1 and member.name.split("/", 1)[1].startswith(("Source/astcenc", "LICENSE"))
        ]
        tar.extractall(destination, members=members, filter="data")
    folders = {member.name.split("/", 1)[0] for member in members}
    if len(folders) != 1:
        raise ValueError("The astc-encoder archive has an unsupported layout.")
    return destination / folders.pop()


def compile_objects(target: str, astc: Path, bcdec: Path, output: Path) -> list[Path]:
    sdk, version = TARGETS[target]
    output.mkdir(parents=True, exist_ok=True)
    compiler = ["xcrun", "--sdk", sdk, "clang++", "-arch", "arm64", version, *FLAGS]

    def build(source: Path, name: str, *more: str) -> Path:
        destination = output / f"{name}.o"
        subprocess.run([*compiler, *more, "-c", str(source), "-o", str(destination)], check=True)
        return destination

    with ThreadPoolExecutor() as pool:
        jobs = [pool.submit(build, astc / f"Source/astcenc_{name}.cpp", name) for name in ASTC_SOURCES]
        jobs.append(pool.submit(build, ENCODER_SOURCE, "texture_encoder", f"-I{astc / 'Source'}", f"-I{bcdec}"))
        return [job.result() for job in jobs]


def build(astc: Path, bcdec: Path) -> None:
    objects = OUTPUT / "objects"
    ios = compile_objects("ios", astc, bcdec, objects / "ios")
    IOS_ARCHIVE.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(["xcrun", "--sdk", "iphoneos", "ar", "rcs", str(IOS_ARCHIVE), *map(str, ios)], check=True)
    mac = compile_objects("macos", astc, bcdec, objects / "macos")
    MAC_LIBRARY.parent.mkdir(parents=True, exist_ok=True)
    subprocess.run(
        [
            "xcrun",
            "--sdk",
            "macosx",
            "clang++",
            "-arch",
            "arm64",
            TARGETS["macos"][1],
            "-dynamiclib",
            "-install_name",
            f"@rpath/{LIBRARY}.dylib",
            "-o",
            str(MAC_LIBRARY),
            *map(str, mac),
        ],
        check=True,
    )
