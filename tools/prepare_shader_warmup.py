"""Build the shader warmup inventory for the full-game host from the staged game resources.

On the phone, the first use of each particle or canvas shader stalls the game while it compiles: on an
iPhone 11 Pro, seven particle shaders in the first combat make one 9.5 second frame. The host
compiles them on a loading screen instead. It cannot discover them cheaply on the device, because
the particle materials live inside about 330 scenes whose textures would all have to be loaded.

This tool reads the game's text scenes (never writing to the asset tree) and writes, into a new
directory:
  particles_NNN.tscn  a few GPUParticles2D nodes each, one per distinct particle material. The
                      engine compiles a scene's materials in one blocking step when it is loaded,
                      so small scenes keep the loading screen responsive.
  inventory.json      counts, a SHA-256 over the scenes, and the canvas shaders to compile

Particle materials are distinct when they differ in which properties they set or in any switch
value; numeric values are uniforms and do not change the compiled shader. For the same reason each
curve and gradient texture is replaced by one small shared texture of the same type: the shader
depends on a texture being present, not on what it holds. The most used materials come first.
"""

import argparse
import hashlib
import json
import re
from pathlib import Path

HEADER = re.compile(r"^\[(\w+)((?: [^\]\n]*)?)\]$", re.MULTILINE)
REFERENCE = re.compile(r'SubResource\("([^"]+)"\)')
SWITCH_VALUE = re.compile(r"true|false|\d+")
IGNORED_PROPERTIES = {"resource_name", "resource_local_to_scene", "script"}
MATERIAL_TYPE = "ParticleProcessMaterial"
MATERIALS_PER_SCENE = 4
# Texture types whose content cannot change the shader, and the stand-in every material shares.
SHARED_TEXTURES = {
    "CurveTexture": 'curve = SubResource("shared_curve")',
    "CurveXYZTexture": (
        'curve_x = SubResource("shared_curve")\n'
        'curve_y = SubResource("shared_curve")\n'
        'curve_z = SubResource("shared_curve")'
    ),
    "GradientTexture1D": 'gradient = SubResource("shared_gradient")',
}
SHARED_BLOCKS = [
    '[sub_resource type="Curve" id="shared_curve"]',
    '[sub_resource type="Gradient" id="shared_gradient"]',
    *(f'[sub_resource type="{kind}" id="shared_{kind}"]\n{body}' for kind, body in SHARED_TEXTURES.items()),
]


def sub_resources(scene_text: str) -> dict[str, tuple[str, str]]:
    """id -> (type, body) for every sub-resource of a text scene."""
    headers = list(HEADER.finditer(scene_text))
    found = {}
    for index, header in enumerate(headers):
        if header.group(1) != "sub_resource":
            continue
        attributes = dict(re.findall(r'(\w+)="([^"]*)"', header.group(2)))
        end = headers[index + 1].start() if index + 1 < len(headers) else len(scene_text)
        found[attributes["id"]] = (attributes["type"], scene_text[header.end() : end].strip("\n"))
    return found


def signature(body: str) -> tuple[str, ...]:
    """What decides the generated shader: the properties set, and the value of each switch."""
    parts = []
    for line in body.splitlines():
        name, separator, value = line.partition(" = ")
        if separator and name not in IGNORED_PROPERTIES:
            parts.append(f"{name}={value}" if SWITCH_VALUE.fullmatch(value) else name)
    return tuple(sorted(parts))


def closure(resources: dict[str, tuple[str, str]], root: str) -> list[str] | None:
    """Sub-resources the material needs, dependencies first. None if it reaches outside the scene."""
    ordered, visiting = [], set()

    def visit(identifier: str) -> bool:
        if identifier in ordered:
            return True
        if identifier in visiting or identifier not in resources:
            return False
        if resources[identifier][0] in SHARED_TEXTURES:
            return True
        if "ExtResource(" in resources[identifier][1]:
            return False
        visiting.add(identifier)
        if not all(visit(dependency) for dependency in REFERENCE.findall(resources[identifier][1])):
            return False
        ordered.append(identifier)
        return True

    return ordered if visit(root) else None


def collect(asset_root: Path) -> tuple[list[list[tuple[str, str, str]]], dict]:
    """Distinct particle materials, most used first, each as (type, id, body) blocks with the material last."""
    distinct: dict[tuple[str, ...], list[tuple[str, str, str]]] = {}
    uses: dict[tuple[str, ...], int] = {}
    seen = skipped = 0
    for scene in sorted(asset_root.rglob("*.tscn")):
        resources = sub_resources(scene.read_text(errors="replace"))
        for identifier, (kind, body) in resources.items():
            if kind != MATERIAL_TYPE:
                continue
            seen += 1
            needed = closure(resources, identifier)
            if needed is None:
                skipped += 1
                continue
            key = signature(body)
            uses[key] = uses.get(key, 0) + 1
            if key not in distinct:
                types = {name: resources[name][0] for name in REFERENCE.findall(body) if name in resources}
                shared = REFERENCE.sub(
                    lambda match: (
                        f'SubResource("shared_{types[match.group(1)]}")'  # noqa: B023
                        if types.get(match.group(1)) in SHARED_TEXTURES  # noqa: B023
                        else match.group(0)
                    ),
                    body,
                )
                distinct[key] = [
                    *((resources[name][0], name, resources[name][1]) for name in needed[:-1]),
                    (kind, identifier, shared),
                ]
    ordered = sorted(distinct, key=lambda key: (-uses[key], key))
    return [distinct[key] for key in ordered], {"particleMaterialsSeen": seen, "particleMaterialsSkipped": skipped}


def render_scene(materials: list[list[tuple[str, str, str]]]) -> str:
    """One scene holding these materials, each on a hidden, idle particle node."""
    resources, nodes = list(SHARED_BLOCKS), ['[node name="ShaderWarmupParticles" type="Node2D"]']
    for index, blocks in enumerate(materials):
        prefix = f"m{index}_"
        for kind, identifier, body in blocks:
            renamed = REFERENCE.sub(
                lambda match: (
                    match.group(0)
                    if match.group(1).startswith("shared_")
                    else f'SubResource("{prefix}{match.group(1)}")'  # noqa: B023
                ),
                body,
            )
            resources.append(f'[sub_resource type="{kind}" id="{prefix}{identifier}"]\n{renamed}'.rstrip("\n"))
        nodes.append(
            f'[node name="P{index}" type="GPUParticles2D" parent="."]\nvisible = false\nemitting = false\namount = 1\n'
            f'process_material = SubResource("{prefix}{blocks[-1][1]}")'
        )
    return "\n\n".join([f"[gd_scene load_steps={len(resources) + 1} format=3]", *resources, *nodes]) + "\n"


def canvas_shaders(asset_root: Path) -> list[str]:
    return sorted(f"res://{path.relative_to(asset_root).as_posix()}" for path in asset_root.rglob("*.gdshader"))


def prepare(asset_root: Path, output: Path) -> dict:
    materials, counts = collect(asset_root)
    if not materials:
        raise ValueError("No particle materials found; refusing to write an empty warmup inventory.")
    output.mkdir(parents=True)  # a new directory only: never merge into an earlier inventory
    digest, scenes = hashlib.sha256(), []
    for start in range(0, len(materials), MATERIALS_PER_SCENE):
        name = f"particles_{len(scenes):03d}.tscn"
        scene = render_scene(materials[start : start + MATERIALS_PER_SCENE])
        (output / name).write_text(scene)
        digest.update(scene.encode())
        scenes.append(name)
    inventory = {
        "format": 1,
        **counts,
        "particleMaterials": len(materials),
        "particleScenes": scenes,
        "canvasShaders": canvas_shaders(asset_root),
        "particleScenesSha256": digest.hexdigest(),
    }
    (output / "inventory.json").write_text(json.dumps(inventory, indent=1) + "\n")
    return inventory


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True, help="staged game resource tree (read only)")
    parser.add_argument("--output", type=Path, required=True, help="new directory to write the inventory into")
    options = parser.parse_args()
    try:
        result = prepare(options.assets, options.output)
    except ValueError as error:
        raise SystemExit(str(error)) from None
    print(
        f"Shader warmup inventory: {result['particleMaterials']} distinct particle materials "
        f"from {result['particleMaterialsSeen']} "
        f"({result['particleMaterialsSkipped']} skipped), {len(result['canvasShaders'])} canvas shaders."
    )


if __name__ == "__main__":
    main()
