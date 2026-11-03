"""The Godot 4.5 file formats the build reads and writes: resource packs, binary project settings and
compiled GDScript.

Each reader accepts only the shape that the pinned engine and game produce, and raises PackError for
anything else, so a different engine or game build fails here instead of being half understood.
"""

import hashlib
import os
import struct
from collections.abc import Iterable
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import BinaryIO

import zstandard

# Pack header (core/io/file_access_pack.h): magic "GDPC", pack format version, engine major, minor
# and patch, pack flags, file base, directory offset, then reserved words up to HEADER_SIZE.
PACK_MAGIC = 0x43504447
PACK_FORMAT_VERSION = 3
ENGINE_VERSION = (4, 5, 1)
HEADER_FORMAT = "<6I2Q"
HEADER_SIZE = 104
# The one pack flag the supported packs set: entry offsets count from the file base.
RELATIVE_FILE_BASE = 2
# Directory entry: name length, name (NUL padded to whole words), offset, size, MD5, entry flags.
# Any entry flag means encrypted or removed data.
ENTRY_FORMAT = "<QQ16sI"
ALIGNMENT = 32
# Far above any real resource path, so a corrupt length cannot make the reader swallow the file.
MAX_NAME_LENGTH = 16384

# Binary project settings (project.binary): "ECFG", a count, then per setting a length-prefixed
# UTF-8 key and a length-prefixed value in the engine's Variant encoding.
SETTINGS_MAGIC = b"ECFG"

# Compiled GDScript (modules/gdscript/gdscript_tokenizer_buffer.cpp): "GDSC", the tokenizer
# version, and the size of the Zstandard-compressed body once decompressed (0 when stored as is).
GDSCRIPT_MAGIC = b"GDSC"
TOKENIZER_VERSION = 101
GDSCRIPT_HEADER_SIZE = 12
# The body starts with four counts (identifiers, constants, lines, tokens), followed by the
# identifier table: per identifier a length in characters, then that many UTF-32 code units with
# every byte XORed with IDENTIFIER_XOR. The XOR only keeps the names from reading as plain text.
GDSCRIPT_COUNTS_SIZE = 16
IDENTIFIER_XOR = 0xB6
# Far above any real script, so a corrupt count cannot make the reader walk the whole body.
MAX_IDENTIFIERS = 100000


class PackError(ValueError):
    pass


@dataclass(frozen=True)
class PackEntry:
    name: str
    offset: int
    size: int
    md5: bytes


def read_directory(stream: BinaryIO) -> list[PackEntry]:
    """The entries of a pack, with offsets from the start of the file."""
    header = stream.read(HEADER_SIZE)
    if len(header) != HEADER_SIZE:
        raise PackError("Truncated pack header")
    magic, version, major, minor, patch, flags, base, directory = struct.unpack_from(HEADER_FORMAT, header)
    if (
        magic != PACK_MAGIC
        or version != PACK_FORMAT_VERSION
        or (major, minor, patch) != ENGINE_VERSION
        or flags != RELATIVE_FILE_BASE
    ):
        raise PackError("Unsupported pack header")
    size = stream.seek(0, os.SEEK_END)
    stream.seek(directory)
    (count,) = struct.unpack("<I", stream.read(4))
    entries, names = [], set()
    for _ in range(count):
        (length,) = struct.unpack("<I", stream.read(4))
        if not 0 < length < MAX_NAME_LENGTH:
            raise PackError("Invalid resource name length")
        name = stream.read(length).rstrip(b"\0").decode("utf-8")
        offset, entry_size, md5, entry_flags = struct.unpack(ENTRY_FORMAT, stream.read(struct.calcsize(ENTRY_FORMAT)))
        if name in names or name.startswith("/") or ".." in PurePosixPath(name).parts:
            raise PackError(f"Duplicate or unsafe pack entry path: {name}")
        if entry_flags or base + offset + entry_size > size:
            raise PackError(f"Encrypted, removed or truncated pack entry: {name}")
        names.add(name)
        entries.append(PackEntry(name, base + offset, entry_size, md5))
    return entries


def read_entry(stream: BinaryIO, entry: PackEntry) -> bytes:
    stream.seek(entry.offset)
    data = stream.read(entry.size)
    if hashlib.md5(data).digest() != entry.md5:
        raise PackError(f"Pack entry MD5 mismatch: {entry.name}")
    return data


def read_pack(path: Path) -> list[tuple[str, bytes]]:
    """Every entry of a pack, each checked against its recorded MD5."""
    with path.open("rb") as stream:
        return [(entry.name, read_entry(stream, entry)) for entry in read_directory(stream)]


def write_pack(stream: BinaryIO, files: Iterable[tuple[str, bytes]]) -> list[tuple[str, bytes]]:
    """Write the files as a pack in the given order. Returns each entry's name and MD5."""

    def pad() -> None:
        stream.write(bytes(-stream.tell() % ALIGNMENT))

    # The header holds the file base and directory offset, so it is written last.
    stream.write(bytes(HEADER_SIZE))
    pad()
    base = stream.tell()
    index = []
    for name, data in files:
        pad()
        index.append((name, stream.tell() - base, len(data), hashlib.md5(data).digest()))
        stream.write(data)
    pad()
    directory = stream.tell()
    stream.write(struct.pack("<I", len(index)))
    for name, offset, size, md5 in index:
        encoded = name.encode("utf-8")
        encoded += bytes(-len(encoded) % 4)
        stream.write(struct.pack("<I", len(encoded)) + encoded + struct.pack(ENTRY_FORMAT, offset, size, md5, 0))
    stream.seek(0)
    stream.write(
        struct.pack(
            HEADER_FORMAT, PACK_MAGIC, PACK_FORMAT_VERSION, *ENGINE_VERSION, RELATIVE_FILE_BASE, base, directory
        )
    )
    return [(name, md5) for name, _, _, md5 in index]


def read_settings(data: bytes) -> list[tuple[str, bytes]]:
    """Each project setting's key and its value's encoded bytes, in file order."""
    if data[:4] != SETTINGS_MAGIC:
        raise PackError("Unsupported project settings container")
    (count,) = struct.unpack_from("<I", data, 4)
    position, result, seen = 8, [], set()
    for _ in range(count):
        (length,) = struct.unpack_from("<I", data, position)
        key = data[position + 4 : position + 4 + length].decode("utf-8")
        position += 4 + length
        (length,) = struct.unpack_from("<I", data, position)
        value = data[position + 4 : position + 4 + length]
        position += 4 + length
        if len(value) != length or key in seen:
            raise PackError(f"Truncated or duplicate project setting: {key}")
        seen.add(key)
        result.append((key, value))
    if position != len(data):
        raise PackError("Trailing bytes after project settings")
    return result


def write_settings(settings: list[tuple[str, bytes]]) -> bytes:
    output = bytearray(SETTINGS_MAGIC + struct.pack("<I", len(settings)))
    for key, value in settings:
        encoded = key.encode("utf-8")
        output += struct.pack("<I", len(encoded)) + encoded + struct.pack("<I", len(value)) + value
    return bytes(output)


def gdscript_identifiers(data: bytes) -> list[str]:
    """The identifier table of a compiled GDScript: every name the script uses."""
    if data[:4] != GDSCRIPT_MAGIC or len(data) < GDSCRIPT_HEADER_SIZE + GDSCRIPT_COUNTS_SIZE:
        raise PackError("Unsupported GDScript container")
    version, size = struct.unpack_from("<II", data, 4)
    if version != TOKENIZER_VERSION:
        raise PackError("Unsupported GDScript tokenizer version")
    body = data[GDSCRIPT_HEADER_SIZE:]
    if size:
        body = zstandard.ZstdDecompressor().decompress(body, max_output_size=size)
        if len(body) != size:
            raise PackError("GDScript decompression mismatch")
    (count,) = struct.unpack_from("<I", body)
    if count > MAX_IDENTIFIERS:
        raise PackError("Unreasonable identifier count")
    position = GDSCRIPT_COUNTS_SIZE
    result = []
    for _ in range(count):
        (length,) = struct.unpack_from("<I", body, position)
        position += 4
        end = position + length * 4
        if end > len(body):
            raise PackError("Truncated GDScript identifier")
        result.append(bytes(byte ^ IDENTIFIER_XOR for byte in body[position:end]).decode("utf-32-le"))
        position = end
    return result
