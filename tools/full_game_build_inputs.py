"""Guard a full-game build against inputs that change underneath it.

`snapshot` records a hash of every managed source file and staged input the build reads.
`verify` is run after compilation: it fails if any of those changed while the build was running. A
build that fails here must be treated as intermediate output and never installed.
"""

import argparse
import json
from pathlib import Path

from hashing import sha256_file

ROOT = Path(__file__).resolve().parents[1]
SOURCE_PROJECTS = (
    "STS2Mobile.FullGame",
    "STS2Mobile.Platform",
    "STS2Mobile.Steam",
    "STS2Mobile.Launcher",
    "STS2Mobile.GameAdapters",
    "STS2Mobile.GameHost",
    "STS2Mobile.GameStub",
    "STS2Mobile.SteamWire",
)
SOURCE_SUFFIXES = {
    ".cs",
    ".csproj",
    ".props",
    ".godot",
    ".cfg",
    ".tscn",
    ".json",
    ".gdextension",
    ".dll",
    ".mm",
    ".cpp",
    ".h",
    ".gd",
    ".py",
    ".sh",
    ".lock",
}
IGNORED_DIRECTORIES = {"bin", "obj", ".godot"}

def snapshot(root: Path = ROOT) -> dict[str, str]:
    files = {}
    for name in ("global.json", "setup.sh", "Directory.Build.props", ".editorconfig"):
        if (root / name).is_file():
            files[name] = sha256_file(root / name)
    for project in SOURCE_PROJECTS:
        for path in sorted((root / "src" / project).rglob("*")):
            relative = path.relative_to(root)
            if path.is_file() and path.suffix in SOURCE_SUFFIXES and not IGNORED_DIRECTORIES & set(relative.parts):
                files[str(relative)] = sha256_file(path)
    for folder in ("mods", "tools", "scripts"):
        for path in sorted((root / folder).rglob("*")):
            relative = path.relative_to(root)
            if (
                path.is_file()
                and path.suffix in SOURCE_SUFFIXES
                # tools/mono only feeds the Mono build, which guards its own steps by hash.
                and not (IGNORED_DIRECTORIES | {"ios-dotnet", "python", "__pycache__", "mono"}) & set(relative.parts)
            ):
                files[str(relative)] = sha256_file(path)
    # The built-in mods setup fetched, which the patcher embeds and the host compiles against.
    for path in sorted((root / ".tools/builtin-mods").glob("*")):
        if path.is_file():
            files[str(path.relative_to(root))] = sha256_file(path)
    return files


def changed_inputs(before: dict[str, str], after: dict[str, str]) -> list[str]:
    return sorted(name for name in before.keys() | after.keys() if before.get(name) != after.get(name))


def verify(before_path: Path) -> None:
    changed = changed_inputs(json.loads(before_path.read_text()), snapshot())
    if changed:
        raise ValueError(f"Build inputs changed during compilation; output is intermediate only: {', '.join(changed)}")
    print(f"Build inputs unchanged ({len(json.loads(before_path.read_text()))} files).")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("snapshot").add_argument("output", type=Path)
    commands.add_parser("verify").add_argument("snapshot", type=Path)
    options = parser.parse_args()
    if options.command == "snapshot":
        options.output.write_text(json.dumps(snapshot(), indent=2) + "\n")
        return
    try:
        verify(options.snapshot)
    except ValueError as error:
        raise SystemExit(str(error)) from None


if __name__ == "__main__":
    main()
