"""The source edits and binary patches of the Mono library, and the Mach-O reading they and the check share.

Everything here works on text and bytes, so tests can use small fixtures. Each workaround is a pinned,
hash-guarded step here.

The binary patches are guarded by the pinned libmonosgen-2.0.a, the source of the code they change, and by the exact
address and encoding of each site. The hash of the linked library itself cannot be the guard: the library embeds
absolute build paths (the assemblies' PDB paths), so it differs from one checkout to another.
"""

from __future__ import annotations

import re
import shlex
import struct
from bisect import bisect_right
from pathlib import Path

from godot_pack import read_settings, write_settings

CLASS_INIT = "_mono_runtime_class_init_full"
CLASS_INIT_SHIM = "_sts2_invoke_class_init"
PINVOKE_INSTALLER = "_sts2_install_pinvoke_override"
GAME_ASSEMBLY_HOOK_INSTALLER = "_sts2_install_game_assembly_hook"
# Added to the main pack's settings: the first autoload mounts the game, and the engine reads its settings as it starts.
GAME_MOUNT_SETTINGS = {
    "autoload/GameMount": "res://GameMount.cs",
    "application/config/project_settings_override": "user://game/game-settings.godot",
}

# Offsets inside the functions of libmonosgen-2.0.a at the pinned hash.
KEY_FROM_ID = "_key_from_id"
KEY_FROM_ID_SITE = 0x54
KEY_FROM_ID_ORIGINAL = 0x9A8003F7  # csel x23, xzr, x0, eq: passes NULL to strcmp for a name without a dot
KEY_FROM_ID_PATCHED = 0x9A800297  # csel x23, x20, x0, eq: x20 is the id, so the extension check is false
CLASS_INIT_SITES = {"_mono_jit_runtime_invoke": (0x14C, 0x814), "_mono_interp_transform_method": (0xE8,)}

AOT_ONLY_CALL = "mono_jit_set_aot_only (true);"
INTERP_MODE_CALL = "mono_jit_set_aot_mode (MONO_AOT_MODE_INTERP);"
RUNTIME_READY = "preload_assemblies_with_exported_symbols ();"
FINALIZER_THREAD_CALL = "extern void mono_gc_init_finalizer_thread (void);\n    mono_gc_init_finalizer_thread ();"
BUNDLED_RESOURCE = re.compile(r"^(uint8_t bundled_resource_[A-Za-z0-9_]+_data\[\])( __attribute__\(\(aligned\(16\)\)\))? =", re.M)
LINK_ANCHOR = "-Wl,-headerpad_max_install_names"

LC_SEGMENT_64 = 0x19
MH_MAGIC_64 = 0xFEEDFACF
# A pointer in the file holds the target's address in its low bits, also when it is a dyld fixup.
POINTER_TARGET_MASK = (1 << 36) - 1


class MonoBuildError(ValueError):
    pass


# Source edits, made in the library builder's working directory before the link.


def edit_autoinit(text: str) -> str:
    """Startup mode, full+interp and not AOT only, and workaround 11: starting the finalizer thread, which nothing else does in
    library mode. Without it no finalizer ever runs, so every Godot object a managed wrapper holds stays, and iOS ends the app at
    its memory limit after about nine minutes of play. Accepts an already edited file (a repeated relink)."""
    if not (text.count(INTERP_MODE_CALL) == 1 and AOT_ONLY_CALL not in text):
        if text.count(AOT_ONLY_CALL) != 1:
            raise MonoBuildError(f"autoinit.c does not have exactly one call of: {AOT_ONLY_CALL}")
        text = text.replace(AOT_ONLY_CALL, INTERP_MODE_CALL)
    if FINALIZER_THREAD_CALL in text:
        return text
    if text.count(RUNTIME_READY) != 1:
        raise MonoBuildError(f"autoinit.c does not have exactly one call of: {RUNTIME_READY}")
    return text.replace(RUNTIME_READY, f"{FINALIZER_THREAD_CALL}\n\n    {RUNTIME_READY}")


def align_bundled_resource(text: str, name: str = "bundled resource") -> str:
    """Workaround 8. Mono finds a method's exception clauses by aligning the absolute address after its IL, so an
    assembly embedded at an odd address silently loses every try/catch/finally in interpreted code."""
    edited, count = BUNDLED_RESOURCE.subn(r"\1 __attribute__((aligned(16))) =", text)
    if count != 1:
        raise MonoBuildError(f"{name} does not define exactly one embedded array.")
    return edited


def game_mount_settings(data: bytes) -> bytes:
    """The main pack's project settings.

    The build adds these rather than project.godot, because the headless editor refuses a C# autoload it has not compiled
    yet. Custom features stay first, then the autoload that has to run first.
    """
    settings = read_settings(data)
    if any(key in GAME_MOUNT_SETTINGS for key, _ in settings):
        raise MonoBuildError("The host's settings already name the game mount.")
    added = [(key, variant_string(value)) for key, value in GAME_MOUNT_SETTINGS.items()]
    features = [item for item in settings if item[0] == "_custom_features"]
    return write_settings(features + added + [item for item in settings if item[0] != "_custom_features"])


def variant_string(text: str) -> bytes:
    encoded = text.encode()
    return struct.pack("<II", 4, len(encoded)) + encoded + bytes(-len(encoded) % 4)


# The publish log's link commands.


def parse_link_commands(log: str) -> tuple[str, Path, str]:
    """Return the library builder's link command, its working directory and its install_name_tool command."""
    lines = log.splitlines()
    start = next((i for i, line in enumerate(lines) if "_BuildNativeLibrary:" in line), None)
    if start is None:
        raise MonoBuildError("The publish log has no _BuildNativeLibrary step. Publish with -v:n.")

    def after(prefix: str, text: list[str]) -> str | None:
        return next((line.strip()[len(prefix) :] for line in text if line.strip().startswith(prefix)), None)

    link = after("Running: xcrun clang", lines[start + 1 : start + 3])
    directory = after("Using working directory: ", lines[start + 1 : start + 4])
    name_id = after("Running: xcrun install_name_tool -id", lines)
    if link is None or directory is None or name_id is None:
        raise MonoBuildError("The link commands were not found in the publish log.")
    return f"xcrun clang{link}", Path(directory), f"xcrun install_name_tool -id{name_id}"


def add_link_sources(link: str, sources: list[Path]) -> str:
    """Put extra C files before the AOT objects, so a patched bl in the Mono runtime can reach them (+-128 MB)."""
    if LINK_ANCHOR not in link:
        raise MonoBuildError(f"The link command has no {LINK_ANCHOR}.")
    quoted = " ".join(shlex.quote(str(path)) for path in sources)
    return link.replace(LINK_ANCHOR, f"{LINK_ANCHOR} {quoted}", 1)


def linked_archives(link: str) -> list[Path]:
    return [Path(token) for token in shlex.split(link) if token.endswith(".a")]


# The link's sources compile one by one, in parallel: the one clang call compiled 2.7 GB of AOT assembly and 210 MB of embedded
# assemblies in series. The objects keep the sources' places, so the link sees the same inputs and makes the same library.

SOURCE_SUFFIXES = (".c", ".m", ".s")
COMPILE_FLAGS_WITH_VALUE = ("-D", "-I", "-isysroot", "-arch")
COMPILE_FLAG_PREFIXES = ("-D", "-I", "-mios-version-min=")
LINK_FLAGS_WITH_VALUE = ("-o", "-framework", "-exported_symbols_list", "-force_load")
LINK_FLAG_PREFIXES = ("-l", "-L", "-Wl,")
LINK_FLAGS = ("-dynamiclib",)


class SplitLink:
    """A link command as its driver, the flags that also apply to compiling, and its arguments with the sources marked."""

    def __init__(self, driver: list[str], compile_flags: list[str], arguments: list[str], sources: list[int]):
        self.driver, self.compile_flags, self.arguments, self.sources = driver, compile_flags, arguments, sources

    def compile_command(self, source: Path, output: Path) -> list[str]:
        return [*self.driver, "-c", *self.compile_flags, str(source), "-o", str(output)]

    def object_link(self, objects: dict[int, Path]) -> list[str]:
        """The link command with each source replaced by its object, in the same place."""
        if set(objects) != set(self.sources):
            raise MonoBuildError("Every source of the link needs its object.")
        return [*self.driver, *(str(objects[i]) if i in objects else token for i, token in enumerate(self.arguments))]


def split_link(link: str) -> SplitLink:
    """Refuses a flag it does not know, because it cannot tell whether that flag changes what a source compiles to."""
    tokens = shlex.split(link)
    if tokens[:2] != ["xcrun", "clang"]:
        raise MonoBuildError("The link command does not start with xcrun clang.")
    arguments = tokens[2:]
    compile_flags: list[str] = []
    sources: list[int] = []
    index = 0
    while index < len(arguments):
        token = arguments[index]
        if token in COMPILE_FLAGS_WITH_VALUE or token in LINK_FLAGS_WITH_VALUE:
            if index + 1 == len(arguments):
                raise MonoBuildError(f"The link command ends at {token}.")
            if token in COMPILE_FLAGS_WITH_VALUE:
                compile_flags += arguments[index : index + 2]
            index += 2
            continue
        if token.startswith(COMPILE_FLAG_PREFIXES):
            compile_flags.append(token)
        elif token.startswith("-"):
            if token not in LINK_FLAGS and not token.startswith(LINK_FLAG_PREFIXES):
                raise MonoBuildError(f"The link command has a flag the split does not know: {token}")
        elif token.endswith(SOURCE_SUFFIXES):
            sources.append(index)
        index += 1
    if not sources:
        raise MonoBuildError("The link command has no source files.")
    return SplitLink(tokens[:2], compile_flags, arguments, sources)


# Reading a linked library. Its __TEXT has vmaddr 0 and fileoff 0, so a code address is also a file offset.


class Symbols:
    """Code and data symbols of `nm` output."""

    def __init__(self, nm_output: str):
        self.code: dict[str, int] = {}
        self.data: dict[str, int] = {}
        for line in nm_output.splitlines():
            parts = line.split()
            if len(parts) == 3:
                table = self.code if parts[1] in "tT" else self.data if parts[1] in "dDsSbB" else None
                if table is not None:
                    table[parts[2]] = int(parts[0], 16)
        self.starts = sorted(set(self.code.values()))
        self.names = {address: name for name, address in self.code.items()}

    def function(self, name: str) -> tuple[int, int]:
        """Return the address range of a function: up to the next code symbol."""
        if name not in self.code:
            raise MonoBuildError(f"The library has no symbol {name}.")
        start = self.code[name]
        index = bisect_right(self.starts, start)
        if index == len(self.starts):
            raise MonoBuildError(f"{name} is the last code symbol: its end is unknown.")
        return start, self.starts[index]

    def target_name(self, address: int) -> str | None:
        name = self.names.get(address)
        return name.removesuffix(".island") if name else None


def branch_calls(data: bytes | bytearray, start: int, end: int) -> list[tuple[int, int]]:
    """Return (address, target) of every bl in a range of code."""
    calls = []
    for address in range(start, end, 4):
        (instruction,) = struct.unpack_from("<I", data, address)
        if instruction >> 26 == 0b100101:
            offset = instruction & 0x3FFFFFF
            calls.append((address, address + ((offset - (1 << 26) if offset & (1 << 25) else offset) << 2)))
    return calls


def functions_called(data: bytes | bytearray, symbols: Symbols, name: str) -> list[str | None]:
    return [symbols.target_name(target) for _, target in branch_calls(data, *symbols.function(name))]


def segments(data: bytes | bytearray) -> list[dict]:
    """Parse the 64-bit Mach-O segments and sections of a thin library."""
    magic, _, _, _, count, _, _, _ = struct.unpack_from("<8I", data, 0)
    if magic != MH_MAGIC_64:
        raise MonoBuildError("Not a thin 64-bit Mach-O file.")
    result, offset = [], 32
    for _ in range(count):
        command, size = struct.unpack_from("<2I", data, offset)
        if command == LC_SEGMENT_64:
            name = bytes(data[offset + 8 : offset + 24]).rstrip(b"\0").decode()
            vmaddr, _, fileoff, _ = struct.unpack_from("<4Q", data, offset + 24)
            (section_count,) = struct.unpack_from("<I", data, offset + 64)
            sections = []
            for index in range(section_count):
                at = offset + 72 + index * 80
                section = bytes(data[at : at + 16]).rstrip(b"\0").decode()
                address, length = struct.unpack_from("<2Q", data, at + 32)
                (position,) = struct.unpack_from("<I", data, at + 48)
                sections.append({"name": section, "address": address, "size": length, "offset": position})
            result.append({"name": name, "vmaddr": vmaddr, "fileoff": fileoff, "sections": sections})
        offset += size
    return result


def check_code_at_file_offsets(data: bytes | bytearray) -> None:
    text = next((segment for segment in segments(data) if segment["name"] == "__TEXT"), None)
    if text is None or text["vmaddr"] != 0 or text["fileoff"] != 0:
        raise MonoBuildError("The library's __TEXT segment does not start at address 0 and file offset 0.")


def init_functions(data: bytes | bytearray) -> set[int]:
    """Return the addresses of the functions that run when the library loads."""
    found = set()
    for segment in segments(data):
        for section in segment["sections"]:
            if section["name"] == "__mod_init_func":
                for at in range(section["offset"], section["offset"] + section["size"], 8):
                    (pointer,) = struct.unpack_from("<Q", data, at)
                    found.add(pointer & POINTER_TARGET_MASK)
    return found


# Binary patches.


def patch_key_from_id(data: bytearray, symbols: Symbols) -> int:
    """Workaround 4. Mono 10.0.12's key_from_id calls strcmp(NULL, ".dll") for an assembly name without a dot."""
    site = symbols.function(KEY_FROM_ID)[0] + KEY_FROM_ID_SITE
    (instruction,) = struct.unpack_from("<I", data, site)
    if instruction != KEY_FROM_ID_ORIGINAL:
        raise MonoBuildError(f"Expected {KEY_FROM_ID_ORIGINAL:#x} at {KEY_FROM_ID}+{KEY_FROM_ID_SITE:#x}, found {instruction:#x}.")
    struct.pack_into("<I", data, site, KEY_FROM_ID_PATCHED)
    return site


def encode_bl(address: int, target: int) -> int:
    delta = (target - address) >> 2
    if not -(1 << 25) <= delta < (1 << 25):
        raise MonoBuildError(f"{target:#x} is out of bl range from {address:#x}.")
    return 0x94000000 | (delta & 0x3FFFFFF)


def patch_class_init(data: bytearray, symbols: Symbols) -> list[int]:
    """Workaround 6. Point Mono's eager class-init calls at the shim in invoke_class_init.c. The interpreter's static
    field opcodes still initialize a class lazily."""
    init, shim = symbols.function(CLASS_INIT)[0], symbols.function(CLASS_INIT_SHIM)[0]
    patched = []
    for function, offsets in CLASS_INIT_SITES.items():
        start, end = symbols.function(function)
        found = tuple(address - start for address, target in branch_calls(data, start, end) if target == init)
        if found != offsets:
            raise MonoBuildError(f"Expected class-init calls in {function} at {offsets}, found {found}.")
        for offset in offsets:
            struct.pack_into("<I", data, start + offset, encode_bl(start + offset, shim))
            patched.append(start + offset)
    return patched
