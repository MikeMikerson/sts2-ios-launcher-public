"""The SHA-256 file digest that staging reports, manifests and pins record."""

import hashlib
from pathlib import Path


def sha256_file(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()
