"""Limit how much particle pre-simulation the game's scenes ask for in one frame, for the phone.

Many ambient emitters (clouds, fog, dust) set `preprocess`: they start as if they had already been
running for that many seconds. The engine simulates that time in fixed steps inside a single frame,
one GPU compute pass per step. The main menu's two cloud layers ask for 400 seconds each at 30
steps per second: 24,000 passes whenever the menu is built. On an iPhone 11 Pro that is a 2 second
frame and about 800 MB of graphics driver working memory, enough for iOS to end the app when a
finished run returns to the menu. One boss background asks for 51,000 steps.

This tool keeps the pre-simulated time, so every effect starts in the same state, and lowers the
simulation rate (`fixed_fps`) of only those emitters whose start would exceed STEP_LIMIT steps.
A scene with many emitters (one event background has 75) gets a lower per-emitter limit, chosen
so the whole scene stays within SCENE_STEP_BUDGET where that is possible. The engine interpolates
particle motion between steps, and every affected emitter is a slow ambient effect with a lifetime
of seconds to minutes.

It reads the asset staging tree (never writing to it), checks each scene against the tree's hash
report, and writes into a new directory the adjusted scenes and `scene-report.json`, which lists
every emitter changed. tools/build_full_game_pack.py takes that directory with --scenes.

Not covered: an emitter whose pre-simulation is set by a parent scene overriding an instanced
child, or from code. None was found doing so with a large value.
"""

import argparse
import hashlib
import json
import re
from pathlib import Path

from profiles import SCENE_PROFILE, load_asset_report

STEP_LIMIT = 300
# About 100 MB of driver working memory and a quarter of a second on an iPhone 11 Pro.
SCENE_STEP_BUDGET = 3000
STEP_LIMITS = (STEP_LIMIT, 200, 150, 100, 60, 40, 30, 20)
# The engine's default simulation rate for particles, also used when a scene sets the rate to 0.
DEFAULT_RATE = 30
EMITTER = re.compile(
    r'^(\[node name="([^"\n]*)" type="GPUParticles(?:2D|3D)"[^\]\n]*\]\n)(.*?)(?=^\[|\Z)', re.MULTILINE | re.DOTALL
)
PREPROCESS = re.compile(r"^preprocess = ([0-9.eE+-]+)$", re.MULTILINE)
FIXED_FPS = re.compile(r"^fixed_fps = (\d+)$", re.MULTILINE)


def limited_rate(preprocess: float, rate: int, step_limit: int = STEP_LIMIT) -> int | None:
    """The simulation rate that keeps this emitter's start within the limit, or None if it already is."""
    effective = rate if rate > 0 else DEFAULT_RATE
    if preprocess * effective <= step_limit:
        return None
    return max(1, int(step_limit // preprocess))


def scene_steps(text: str, step_limit: int, adjustable_only: bool = False) -> int:
    """Pre-simulation steps the scene asks for at once, with emitters held to the limit where they can be."""
    total = 0
    for _, _, body in EMITTER.findall(text):
        preprocess = PREPROCESS.search(body)
        if not preprocess:
            continue
        stored = FIXED_FPS.search(body)
        rate = int(stored.group(1)) if stored else DEFAULT_RATE
        if "interpolate = false" not in body:
            rate = limited_rate(float(preprocess.group(1)), rate, step_limit) or rate
        elif adjustable_only:
            continue
        total += int(float(preprocess.group(1)) * (rate if rate > 0 else DEFAULT_RATE))
    return total


def scene_limit(text: str) -> int:
    """The largest per-emitter limit that keeps the adjustable emitters within the scene budget, or the smallest."""
    return next(
        (limit for limit in STEP_LIMITS if scene_steps(text, limit, adjustable_only=True) <= SCENE_STEP_BUDGET),
        STEP_LIMITS[-1],
    )


def adjust_scene(text: str, step_limit: int = STEP_LIMIT) -> tuple[str, list[dict]]:
    """Return the scene with over-limit emitters slowed, and what was changed."""
    changes = []

    def adjust(match: re.Match) -> str:
        header, name, body = match.groups()
        if "interpolate = false" in body:
            return match.group(0)  # without interpolation a lower rate would be visible; leave it
        preprocess = PREPROCESS.search(body)
        if not preprocess:
            return match.group(0)
        seconds = float(preprocess.group(1))
        stored = FIXED_FPS.search(body)
        before = int(stored.group(1)) if stored else DEFAULT_RATE
        after = limited_rate(seconds, before, step_limit)
        if after is None:
            return match.group(0)
        body = FIXED_FPS.sub(f"fixed_fps = {after}", body) if stored else f"fixed_fps = {after}\n{body}"
        effective = before if before > 0 else DEFAULT_RATE
        changes.append(
            {
                "node": name,
                "preprocessSeconds": seconds,
                "fixedFpsBefore": before,
                "fixedFpsAfter": after,
                "stepsBefore": int(seconds * effective),
                "stepsAfter": int(seconds * after),
            }
        )
        return header + body

    return EMITTER.sub(adjust, text), changes


def prepare(assets: Path, output: Path) -> dict:
    assets, output = assets.resolve(), output.resolve()
    if output.is_relative_to(assets):
        raise ValueError("The adjusted scenes must be written outside the asset staging tree")
    asset_report = load_asset_report(assets)
    scenes = []
    output.mkdir(parents=True)  # a new directory only
    for entry in sorted(asset_report["staged"], key=lambda item: item["path"]):
        name = entry["path"]
        if not name.endswith(".tscn"):
            continue
        data = (assets / name).read_bytes()
        if hashlib.sha256(data).hexdigest() != entry["sha256"]:
            raise ValueError(f"Staged scene changed: {name}")
        text = data.decode("utf-8")
        limit = scene_limit(text)
        adjusted, changes = adjust_scene(text, limit)
        if not changes:
            continue
        target = output / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(adjusted.encode("utf-8"))
        scenes.append(
            {
                "path": name,
                "sourceSha256": entry["sha256"],
                "outputSha256": hashlib.sha256(adjusted.encode("utf-8")).hexdigest(),
                "stepLimit": limit,
                "sceneSteps": scene_steps(adjusted, limit),
                "emitters": changes,
            }
        )
    if not scenes:
        raise ValueError("No emitter exceeds the limit; nothing to adjust.")
    emitters = [emitter for scene in scenes for emitter in scene["emitters"]]
    report = {
        "profile": SCENE_PROFILE,
        "assetProfile": asset_report["profile"],
        "assetPackSha256": asset_report["packSha256"],
        "stepLimit": STEP_LIMIT,
        "sceneStepBudget": SCENE_STEP_BUDGET,
        "scenesOverBudget": [scene["path"] for scene in scenes if scene["sceneSteps"] > SCENE_STEP_BUDGET],
        "emittersAdjusted": len(emitters),
        "stepsBefore": sum(emitter["stepsBefore"] for emitter in emitters),
        "stepsAfter": sum(emitter["stepsAfter"] for emitter in emitters),
        "scenes": scenes,
    }
    (output / "scene-report.json").write_text(json.dumps(report, indent=1) + "\n")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True, help="asset staging tree (read only)")
    parser.add_argument("--output", type=Path, required=True, help="new directory for the adjusted scenes")
    options = parser.parse_args()
    try:
        done = prepare(options.assets, options.output)
    except ValueError as error:
        raise SystemExit(str(error)) from None
    print(
        f"Adjusted {done['emittersAdjusted']} emitters in {len(done['scenes'])} scenes: "
        f"{done['stepsBefore']} pre-simulation steps down to {done['stepsAfter']}. "
        f"Scenes still over {done['sceneStepBudget']} steps: {len(done['scenesOverBudget'])}."
    )


if __name__ == "__main__":
    main()
