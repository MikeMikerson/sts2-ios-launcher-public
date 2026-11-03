"""Confirm workarounds 1-11 and the size steps in the built Mono app, by testing the built files.

scripts/build-mono.sh runs this at its end. Run it alone with: tools/python/bin/python3 tools/mono/check_mono_build.py
"""

from __future__ import annotations

import hashlib
import json
import mmap
import os
import plistlib
import struct
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
sys.path.insert(0, str(HERE.parent))

import mono_library as library  # noqa: E402
from hashing import sha256_file  # noqa: E402

OUT = ROOT / "analysis/full-game-mono"
PUBLISH = OUT / "publish"
UNSTRIPPED = OUT / "STS2Mobile.FullGame"
AOT_OUTPUT = OUT / "tree/src/STS2Mobile.FullGame/.godot/mono/temp/obj/ExportRelease/ios-arm64/mobile"
TOOL = HERE / "MonoBuildTool/bin/Debug/net10.0/MonoBuildTool"
PINS = json.loads((ROOT / "tools/dependencies.json").read_text())["mono"]
# The app's assemblies that use the game. Their references resolve only once the game is there, so they must not be AOT
# compiled.
INTERPRETED = ("STS2Mobile_GameHost", "STS2Mobile_GameAdapters")
INTERPRETED_FILES = ("STS2Mobile.GameHost.dll", "STS2Mobile.GameAdapters.dll")
# What patches the game on the phone, before the game exists: nothing in these refers to it, and interpreting them is slow.
PATCHER_MODULES = ("STS2Mobile_GamePatcher", "STS2Mobile_GamePack", "Mono_Cecil")
AOT_MODULE = "_mono_aot_module_{}_info"
ENTRY_POINT = "_godotsharp_game_main_init"
INTERP_WRAPPER = "wrapper_other_object_interp_in_static"
SCENE_DELEGATE = "STS2SceneDelegate"
SCENE_DELEGATE_KEY = ":UIApplicationSceneManifest:UISceneConfigurations:UIWindowSceneSessionRoleApplication:0:UISceneDelegateClassName"
# GodotSharp has these once rewritten: workarounds 3, 7 and 10 (BUILDING.md lists them).
GODOTSHARP_FACTS = {
    "trampoline_is_function_pointer": True,
    "trampoline_has_modifiers": False,
    "holder_type": True,
    "generic_from": 19,
    "generic_from_rewritten": 19,
    "generic_from_with_own_trampoline": 0,
    "function_pointer_methods": 689,
    "function_pointer_methods_inlinable": 0,
}
# Assemblies with code that the launcher, the game and Sentry do not refer to. build_mono.py keeps them out of the publish.
# Empty facades stay, because they cost a few KB each and assemblies name them in strings. Never add protobuf-net:
# protobuf-net.Core refers to it only in strings, so a reference check cannot see that it is used.
REMOVED_ASSEMBLIES = (
    "Microsoft.CSharp",
    "Microsoft.VisualBasic.Core",
    "Mono.Cecil.Mdb",
    "Mono.Cecil.Pdb",
    "System.ComponentModel.Annotations",
    "System.Diagnostics.TextWriterTraceListener",
    "System.Formats.Tar",
    "System.Linq.AsyncEnumerable",
    "System.Linq.Parallel",
    "System.Linq.Queryable",
    "System.Net.Http.Json",
    "System.Net.HttpListener",
    "System.Net.Mail",
    "System.Net.ServerSentEvents",
    "System.Net.WebClient",
    "System.Net.WebProxy",
    "System.Private.DataContractSerialization",
    "System.Reflection.DispatchProxy",
    "System.Runtime.InteropServices.JavaScript",
)
# What the app's assemblies may refer to that is not in the publish: the game's files arrive at run time.
GAME_ASSEMBLIES = ("sts2", "SmartFormat", "SmartFormat.ZString", "Steamworks.NET", "0Harmony", "Sentry", "Sentry.Godot")
GAME_FILES = ROOT / "analysis/game-files"
# A stripped library has its exports and the system calls it imports, and no more.
MOST_SYMBOLS_WHEN_STRIPPED = 5000


class Facts:
    """What the checks read from the built files, read once."""

    def __init__(self, app: Path):
        self.app = app
        self.binary = app / "Frameworks/STS2Mobile.FullGame.framework/STS2Mobile.FullGame"
        self.handle = self.binary.open("rb")
        self.data = mmap.mmap(self.handle.fileno(), 0, prot=mmap.PROT_READ)
        # The app's library is stripped. Its code and data sit at the offsets of this copy, which size_steps confirms.
        self.symbols = library.Symbols(run(["nm", str(UNSTRIPPED)]))
        self.exports = run(["nm", "-g", "-U", "-j", str(self.binary)]).split()
        self.corelib_aot = (AOT_OUTPUT / "System.Private.CoreLib.dll.s").read_text(errors="replace")

    def embedded_at(self, name: str) -> int:
        """Return where a published assembly is embedded in the library, byte for byte, or -1."""
        return self.data.find((PUBLISH / name).read_bytes())


def run(command: list[str]) -> str:
    env = os.environ | {"DOTNET_ROOT": str(ROOT / "tools/ios-dotnet")}
    return subprocess.run(command, capture_output=True, text=True, check=True, env=env).stdout


def report(*problems: str | None) -> list[str]:
    return [problem for problem in problems if problem]


def startup_mode(facts: Facts) -> list[str]:
    called = library.functions_called(facts.data, facts.symbols, "_runtime_init_callback")
    return report(
        None if sha256_file(PACK_TARGETS) == PINS["apple_build_targets"]["output_sha256"] else "AppleBuild.targets is not the edited one",
        None if INTERP_WRAPPER in facts.corelib_aot else "CoreLib was not AOT compiled in full+interp mode",
        None if "_mono_jit_set_aot_mode" in called else "startup does not call mono_jit_set_aot_mode",
        "startup calls mono_jit_set_aot_only" if "_mono_jit_set_aot_only" in called else None,
        None if "_mono_gc_init_finalizer_thread" in called else "startup does not start the finalizer thread (workaround 11)",
    )


def workaround_1(facts: Facts) -> list[str]:
    return report(
        "the library has an aot-instances module" if AOT_MODULE.format("aot_instances") in facts.symbols.data else None,
        "the publish AOT compiled aot-instances" if (AOT_OUTPUT / "aot-instances.dll.s").exists() else None,
    )


def workaround_2(facts: Facts) -> list[str]:
    problems = [f"{name} was AOT compiled" for name in INTERPRETED if AOT_MODULE.format(name) in facts.symbols.data]
    problems += [f"{name} was not AOT compiled" for name in PATCHER_MODULES if AOT_MODULE.format(name) not in facts.symbols.data]
    problems += [f"{name} is not embedded" for name in INTERPRETED_FILES if facts.embedded_at(name) < 0]
    count = facts.exports.count(ENTRY_POINT)
    if count != 1:
        problems.append(f"{ENTRY_POINT} is exported {count} times")
    return problems


def godotsharp(facts: Facts, keys: tuple[str, ...]) -> list[str]:
    path = PUBLISH / "GodotSharp.dll"
    found = json.loads(run([str(TOOL), "verify-godotsharp", str(path)]))
    problems = [f"{key} is {found.get(key)}, expected {GODOTSHARP_FACTS[key]}" for key in keys if found.get(key) != GODOTSHARP_FACTS[key]]
    if sha256_file(path) != PINS["godotsharp"]["output_sha256"]:
        problems.append("the published GodotSharp.dll is not the pinned rewrite")
    if facts.embedded_at("GodotSharp.dll") < 0:
        problems.append("the library does not embed the published GodotSharp.dll")
    if AOT_MODULE.format("GodotSharp") not in facts.symbols.data:
        problems.append("GodotSharp was not AOT compiled")
    return problems


def workaround_3(facts: Facts) -> list[str]:
    return godotsharp(facts, ("trampoline_is_function_pointer", "trampoline_has_modifiers"))


def workaround_4(facts: Facts) -> list[str]:
    site = facts.symbols.function(library.KEY_FROM_ID)[0] + library.KEY_FROM_ID_SITE
    (instruction,) = struct.unpack_from("<I", facts.data, site)
    return report(None if instruction == library.KEY_FROM_ID_PATCHED else f"key_from_id has {instruction:#x}")


def workaround_5(facts: Facts) -> list[str]:
    defined = [name for name in facts.symbols.code if name.startswith("_GlobalizationNative_")]
    return report(
        None if defined else "the library has no GlobalizationNative code",
        None if "GlobalizationNative_" in facts.corelib_aot else "CoreLib's AOT code does not call GlobalizationNative directly",
    )


def workaround_6(facts: Facts) -> list[str]:
    problems = []
    for function, offsets in library.CLASS_INIT_SITES.items():
        called = library.functions_called(facts.data, facts.symbols, function)
        if called.count(library.CLASS_INIT_SHIM) != len(offsets):
            problems.append(f"{function} calls the shim {called.count(library.CLASS_INIT_SHIM)} times, expected {len(offsets)}")
        if library.CLASS_INIT in called:
            problems.append(f"{function} still calls {library.CLASS_INIT}")
    return problems


def workaround_7(facts: Facts) -> list[str]:
    return godotsharp(facts, ("holder_type", "generic_from", "generic_from_rewritten", "generic_from_with_own_trampoline"))


def workaround_8(facts: Facts) -> list[str]:
    arrays = {name: address for name, address in facts.symbols.data.items() if name.startswith("_bundled_resource_") and name.endswith("_data")}
    unaligned = [name for name, address in arrays.items() if address % 16]
    problems = [f"{len(unaligned)} of {len(arrays)} embedded arrays are not aligned"] if unaligned else []
    assemblies = len(list(PUBLISH.glob("*.dll")))
    if len(arrays) < assemblies:
        problems.append(f"{len(arrays)} embedded arrays for {assemblies} assemblies")
    for name in ("GodotSharp.dll", "System.Private.CoreLib.dll", "STS2Mobile.GameHost.dll"):
        at = facts.embedded_at(name)
        if at < 0 or at % 16:
            problems.append(f"{name} is embedded at file offset {at}")
    return problems


def workaround_9(facts: Facts) -> list[str]:
    start = facts.symbols.function(library.PINVOKE_INSTALLER)[0]
    called = library.functions_called(facts.data, facts.symbols, library.PINVOKE_INSTALLER)
    table = (OUT / "pinvoke-table.c").read_text()
    return report(
        None if start in library.init_functions(facts.data) else "the override is not installed when the library loads",
        None if "_mono_loader_install_pinvoke_override" in called else "the installer does not call Mono",
        None if '"libSystem.Native", "SystemNative_GetTimestamp"' in table else "the table lacks SystemNative_GetTimestamp",
        None if facts.data.find(b"SystemNative_GetTimestamp\0") >= 0 else "the library lacks the table's strings",
    )


def workaround_10(facts: Facts) -> list[str]:
    return godotsharp(facts, ("function_pointer_methods", "function_pointer_methods_inlinable"))


def runtime_and_app(facts: Facts) -> list[str]:
    plist = run(["/usr/libexec/PlistBuddy", "-c", f"Print {SCENE_DELEGATE_KEY}", str(facts.app / "Info.plist")]).strip()
    classes = run(["otool", "-ov", str(facts.app / "FullGame")])
    return report(
        None if sha256_file(PUBLISH / "libmonosgen-2.0.a") == PINS["libmonosgen_sha256"] else "libmonosgen-2.0.a is not the pinned one",
        None if (facts.app / "FullGame.pck").is_file() else "the app has no main pack",
        None if plist == SCENE_DELEGATE and f" {SCENE_DELEGATE}\n" in classes else "the app lacks the scene delegate iOS 27 requires",
    )


def game_assembly_hook(facts: Facts) -> list[str]:
    start = facts.symbols.function(library.GAME_ASSEMBLY_HOOK_INSTALLER)[0]
    return report(None if start in library.init_functions(facts.data) else "the assembly hook is not installed when the library loads")


def texture_encoder(facts: Facts) -> list[str]:
    """In the library, reached through the override table, with none of astc-encoder's symbols for the engine's copy to meet."""
    facts.symbols.function("_sts2_texture_encoder_convert")  # raises if it is not there
    return [f"astc-encoder's {name} is exported" for name in sorted(facts.exports) if "astcenc" in name or "bcdec" in name]


def loaded_bytes_hash(data) -> str:
    """Hash the sections, without the load commands, which stripping and the install name change."""
    parts = library.segments(data)
    end = next(segment["fileoff"] for segment in parts if segment["name"] == "__LINKEDIT")
    start = min(section["offset"] for segment in parts for section in segment["sections"] if section["offset"])
    with memoryview(data) as view, view[start:end] as loaded:
        return hashlib.sha256(loaded).hexdigest()


# native/voice_audio.mm's functions, which the host finds by name in the app (STS2Mobile.Platform/VoiceAudio.cs).
VOICE_FUNCTIONS = (
    "sts2_voice_capture_state", "sts2_voice_request_permission", "sts2_voice_start_capture", "sts2_voice_stop_capture",
    "sts2_voice_capture_status", "sts2_voice_read", "sts2_voice_queued", "sts2_voice_play", "sts2_voice_stop_output",
    "sts2_voice_route", "sts2_voice_last_error", "sts2_voice_audio_state",
)
VOICE_MOD = ROOT / ".tools/builtin-mods/QuickMatch.dll"


def voice_audio(facts: Facts) -> list[str]:
    """The app exports every voice function, and says what the microphone is for exactly when it carries QuickMatch."""
    exported = set(run(["nm", "-g", "-U", "-j", str(facts.app / "FullGame")]).split())
    with (facts.app / "Info.plist").open("rb") as handle:
        purpose = plistlib.load(handle).get("NSMicrophoneUsageDescription")
    with_voice = VOICE_MOD.is_file()
    return report(
        *(f"the app does not export {name}" for name in VOICE_FUNCTIONS if "_" + name not in exported),
        None if bool(purpose) == with_voice else
        ("the app with QuickMatch has no microphone text" if with_voice else "the app without QuickMatch asks for the microphone"),
    )


def size_steps(facts: Facts) -> list[str]:
    with UNSTRIPPED.open("rb") as handle, mmap.mmap(handle.fileno(), 0, prot=mmap.PROT_READ) as unstripped:
        same = loaded_bytes_hash(facts.data) == loaded_bytes_hash(unstripped)
    symbols = len(run(["nm", "-j", str(facts.binary)]).splitlines())
    still_there = [name for name in REMOVED_ASSEMBLIES if (PUBLISH / f"{name}.dll").exists()]
    return report(
        None if same else "the app's library differs from the unstripped one in more than its symbols",
        None if symbols <= MOST_SYMBOLS_WHEN_STRIPPED else f"the app's library has {symbols} symbols, so it was not stripped",
        f"still published: {', '.join(still_there)}" if still_there else None,
    )


def assemblies_present(facts: Facts) -> list[str]:
    """Everything the app's assemblies refer to is in the publish, and when the game's files are here, the same for the game."""
    game = [*sorted(GAME_FILES.glob("*.dll")), *sorted((GAME_FILES / "original").glob("*.dll"))]
    roots = [*sorted(PUBLISH.glob("STS2Mobile.*.dll")), *game]
    # The built-in mods (QuickMatch) are woven at preparation and load from the game's folder too.
    built_in = [entry["id"] for entry in json.loads((ROOT / "mods/builtin.json").read_text())["mods"]]
    command = [str(TOOL), "check-assembly-references", str(PUBLISH), ",".join([*GAME_ASSEMBLIES, *built_in]), *map(str, roots)]
    result = subprocess.run(command, capture_output=True, text=True, env=os.environ | {"DOTNET_ROOT": str(ROOT / "tools/ios-dotnet")})
    if result.returncode:
        return [line for line in (result.stdout + result.stderr).splitlines() if line] or ["the reference check failed"]
    return []


def signature(facts: Facts) -> list[str]:
    result = subprocess.run(["codesign", "--verify", str(facts.app)], capture_output=True, text=True)
    return report(None if result.returncode == 0 else f"the app's signature is not valid: {result.stderr.strip()}")


PACK_TARGETS = ROOT / "tools/ios-dotnet/packs/Microsoft.NET.Runtime.LibraryBuilder.Sdk" / PINS["runtime_version"] / "Sdk/AppleBuild.targets"
CHECKS = [
    ("1 no AOT dedup", workaround_1),
    ("2 game-facing assemblies interpreted, one entry point", workaround_2),
    ("3 no modifiers on Callable._trampoline", workaround_3),
    ("4 key_from_id patched", workaround_4),
    ("5 direct globalization calls", workaround_5),
    ("6 class-init calls go to the shim", workaround_6),
    ("7 generic Callable.From rewritten", workaround_7),
    ("8 embedded assemblies aligned", workaround_8),
    ("9 P/Invoke override installed at load", workaround_9),
    ("10 function pointer methods not inlined", workaround_10),
    ("startup mode and full+interp", startup_mode),
    ("pinned runtime, app contents", runtime_and_app),
    ("game assembly hook installed at load", game_assembly_hook),
    ("texture encoder linked, its internals hidden", texture_encoder),
    ("voice audio exported, microphone text with QuickMatch", voice_audio),
    ("library stripped, unused assemblies removed", size_steps),
    ("what the app and the game refer to is present", assemblies_present),
]


def check(app: Path, require_signature: bool = True) -> int:
    """Print one line per check. Return the number of checks that failed."""
    facts = Facts(app)
    checks = CHECKS + ([("app signature", signature)] if require_signature else [])
    failed = 0
    for name, function in checks:
        try:
            problems = function(facts)
        except library.MonoBuildError as error:  # for example a symbol the library should have
            problems = [str(error)]
        print(f"{'ok' if not problems else 'FAILED'}  {name}" + "".join(f"\n    {problem}" for problem in problems), flush=True)
        failed += bool(problems)
    return failed


if __name__ == "__main__":
    paths = [argument for argument in sys.argv[1:] if not argument.startswith("--")]
    app = Path(paths[0]) if paths else OUT / "FullGame.app"
    sys.exit(1 if check(app, require_signature="--unsigned" not in sys.argv) else 0)
