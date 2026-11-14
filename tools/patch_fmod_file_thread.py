"""Stop the FMOD extension's file thread from touching a file's reference count.

fmod-gdextension 6.1.0-4.5.0 reads banks on its own thread (GodotFileRunner::run in src/callback/file_callbacks.cpp). For
each read it copies the handle's Ref<FileAccess>, reads, calls the request's done callback, and only then drops the copy.
Once done has run, FMOD may close the file on another thread (godotFileClose), which drops the handle's own reference at
the same moment. Godot's RefCounted::unreference locks the object's instance binding mutex when the count reaches one,
while the other thread, at zero, destroys the object and that mutex. On the phone that ended in std::mutex::lock throwing
and std::terminate after 53 minutes of play (FullGame-2026-11-14-090828.ips).

The release library is prebuilt and FMOD's SDK is behind a login, so this changes two instructions instead of rebuilding:
the reference taken for the copy becomes a no-op, and the drop after done branches straight back to the loop. That is
the source change `FileAccess *file = handle->file.ptr();`: the handle keeps the file alive until it is closed, and FMOD
closes a file only after its reads are done or cancelled (a cancel already waits for the read in progress).
"""

import hashlib
from pathlib import Path

LIBRARY = "libGodotFmod.ios.template_release.universal.dylib"
INPUT_SHA256 = "8d8d7f7037607615326f63e2ecd00c933bdfe430e95df75b6a6506c46deec3f8"
CHANGE = "Stop the FMOD file thread copying and dropping the file's Ref, which raced with closing the file."

# (file offset, original instruction, replacement), little-endian 32-bit words. __TEXT starts at file offset 0.
PATCHES = (
    # bl RefCounted::reference (the Ref copy) -> nop
    (0x4AF98, 0x94018F41, 0xD503201F),
    # cbz x27, loop (before RefCounted::unreference and the destroy) -> b loop (0x4aefc)
    (0x4B02C, 0xB4FFF69B, 0x17FFFFB4),
)


def word(data: bytes, offset: int) -> int:
    return int.from_bytes(data[offset : offset + 4], "little")


def patch_bytes(data: bytes, input_sha256: str = INPUT_SHA256) -> bytes:
    """The library with the file thread fixed. Refuses any library but the pinned one."""
    if hashlib.sha256(data).hexdigest() != input_sha256:
        raise ValueError("Unexpected FMOD extension library: the file thread patch is for 6.1.0-4.5.0 only")
    patched = bytearray(data)
    for offset, original, replacement in PATCHES:
        if word(data, offset) != original:
            raise ValueError(f"Unexpected instruction at {offset:#x} in the FMOD extension library")
        patched[offset : offset + 4] = replacement.to_bytes(4, "little")
    return bytes(patched)


def is_patched(data: bytes) -> bool:
    """Whether a built library carries both replacements (the build check reads the app's copy)."""
    return all(len(data) >= offset + 4 and word(data, offset) == replacement for offset, _, replacement in PATCHES)


def patch_file(path: Path) -> dict:
    data = path.read_bytes()
    patched = patch_bytes(data)
    path.write_bytes(patched)
    return {
        "inputSha256": hashlib.sha256(data).hexdigest(),
        "outputSha256": hashlib.sha256(patched).hexdigest(),
        "change": CHANGE,
    }
