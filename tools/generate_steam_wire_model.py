"""Generate the static protobuf model source from the hash-gated SteamKit2 contract inventory.

The inventory is produced by `tools/SteamAotPatcher model-roots`. Generation is deterministic, so
`--check` can prove that the committed model still matches the inventory it was generated from.
"""

import argparse
import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
MODEL = ROOT / "src/STS2Mobile.SteamWire/SteamWireModel.cs"
DEPENDENCIES = ROOT / "tools/dependencies.json"


def supported_inputs() -> set[str]:
    """The SteamKit2 assemblies, one per target framework, that the model was generated from."""
    return set(json.loads(DEPENDENCIES.read_text())["steamkit2"]["sha256"].values())


def render(inventory: dict) -> str:
    if inventory["inputSha256"] not in supported_inputs():
        raise ValueError("Unsupported SteamKit2 inventory input")
    roots = inventory["roots"]
    if roots != sorted(set(roots)) or not roots:
        raise ValueError("Contract roots must be unique and ordered")
    lines = [
        "// Generated from the hash-gated SteamKit2 contract inventory.",
        "using ProtoBuf;",
        "using ProtoBuf.Meta;",
        "namespace STS2Mobile.SteamWire;",
        "[ProtoModel]",
    ]
    # Cecil separates nested types with '/', C# with '.'.
    lines += [f"[ProtoSerializable(typeof(global::{root.replace('/', '.')}))]" for root in roots]
    lines.append("public sealed partial class SteamWireModel : TypeModel;")
    return "\n".join(lines) + "\n"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("inventory", type=Path)
    parser.add_argument(
        "--check", action="store_true", help="fail if the committed model differs instead of writing it"
    )
    options = parser.parse_args()
    try:
        generated = render(json.loads(options.inventory.read_text()))
    except ValueError as error:
        raise SystemExit(str(error)) from None
    if options.check:
        if MODEL.read_text() != generated:
            raise SystemExit("SteamWireModel.cs does not match the contract inventory; regenerate it.")
        print("SteamWireModel.cs matches the contract inventory.")
    else:
        MODEL.write_text(generated)
        print(f"Wrote {MODEL.relative_to(ROOT)}.")


if __name__ == "__main__":
    main()
