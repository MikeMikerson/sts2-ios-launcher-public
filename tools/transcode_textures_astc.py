"""Re-encode the game's desktop block-compressed textures as ASTC for the phone.

The desktop game ships 1,244 textures as S3TC or BPTC. The iPhone's GPU cannot sample those, so the
engine unpacks each one to RGBA8 when it loads: four to eight times the memory and a pause per load.
On an iPhone 11 Pro that puts the game at 1,965 MB on its main menu, enough for iOS to end the
app when a finished run returns there.

This tool reads the asset staging tree (never writing to it), verifies every input against the
tree's own hash report, and writes into a new directory:
  .godot/imported/<name>.astc.ctex   the texture as ASTC 4x4, same size, same container header
  <path>.import                      the import remap, now naming the ASTC file
  texture-report.json                per texture: input and output hashes and the measured
                                     difference from the decoded original
tools/build_full_game_pack.py takes that directory with --textures and ships these files in place
of the originals. ASTC 4x4 uses the same 8 bits per pixel as BPTC, so quality is preserved (the
report records the PSNR of each texture) and the pack does not grow. A texture whose result falls
below the quality bar is not converted: it stays in its original format and the report lists it.

Encoding takes several minutes. If a run stops after encoding, --finish completes the report from
the encoded files without encoding again; every file is still hashed into the report.

The work is done by Godot's own encoder in tools/texture_transcode/transcode.gd.
"""

import argparse
import json
import os
import re
import subprocess
from pathlib import Path

from hashing import sha256_file
from profiles import TEXTURE_PROFILE, load_asset_report

ROOT = Path(__file__).resolve().parents[1]
# Below this the result would be visibly different from what the desktop game shows.
MINIMUM_PSNR = 40.0
REMAP = re.compile(r'^path\.(?:s3tc|bptc)="res://(\.godot/imported/[^"\n]+)\.(s3tc|bptc)\.ctex"$', re.MULTILINE)


def rewrite_import(text: str) -> tuple[str, str, str] | None:
    """Point an import remap at the ASTC file.

    Returns (new text, source path, output path), or None if it has no block-compressed remap.
    """
    matches = REMAP.findall(text)
    if not matches:
        return None
    if len(matches) != 1:
        raise ValueError("An import with more than one block-compressed remap is not supported")
    stem, variant = matches[0]
    return REMAP.sub(f'path.astc="res://{stem}.astc.ctex"', text), f"{stem}.{variant}.ctex", f"{stem}.astc.ctex"


def plan(assets: Path, report: dict) -> list[dict]:
    """Every texture to convert, with its inputs checked against the asset tree's hash report."""
    staged = {entry["path"]: entry["sha256"] for entry in report["staged"]}
    textures = []
    for name in sorted(path for path in staged if path.endswith(".import")):
        if sha256_file(assets / name) != staged[name]:
            raise ValueError(f"Staged import changed: {name}")
        rewritten = rewrite_import((assets / name).read_text())
        if rewritten is None:
            continue
        text, source, output = rewritten
        if source not in staged or output in staged:
            raise ValueError(f"Import names a texture outside the staged set: {name}")
        if sha256_file(assets / source) != staged[source]:
            raise ValueError(f"Staged texture changed: {source}")
        textures.append(
            {
                "import": name,
                "sourceImportSha256": staged[name],
                "importText": text,
                "source": source,
                "sourceSha256": staged[source],
                "output": output,
            }
        )
    return textures


def read_results(assets: Path, work: Path) -> dict[str, dict]:
    results = {}
    for path in sorted(work.glob("results-*.jsonl")):
        for line in path.read_text().splitlines():
            result = json.loads(line)
            results[str(Path(result.pop("source")).relative_to(assets))] = result
    return results


def run_workers(assets: Path, output: Path, textures: list[dict], workers: int, godot: Path) -> dict[str, dict]:
    """Encode in parallel Godot processes. Returns the worker's result per source path; any failure stops everything."""
    work = output / ".work"
    work.mkdir()
    processes = []
    for index in range(workers):
        jobs = [
            {"source": str(assets / texture["source"]), "output": str(output / texture["output"])}
            for texture in textures[index::workers]
        ]
        (work / f"jobs-{index}.json").write_text(json.dumps(jobs))
        log = (work / f"worker-{index}.log").open("w")
        processes.append(
            (
                subprocess.Popen(
                    [
                        str(godot),
                        "--headless",
                        "--path",
                        str(ROOT / "tools/texture_transcode"),
                        "-s",
                        "res://transcode.gd",
                        "--",
                        str(work / f"jobs-{index}.json"),
                        str(work / f"results-{index}.jsonl"),
                    ],
                    stdout=log,
                    stderr=subprocess.STDOUT,
                ),
                log,
            )
        )
    failed = [process.wait() for process, _ in processes]
    for _, log in processes:
        log.close()
    if any(failed):
        raise RuntimeError("A texture encoding worker failed; no report was written.")
    return read_results(assets, work)


def transcode(assets: Path, output: Path, workers: int, godot: Path, finish: bool = False) -> dict:
    if workers < 1:
        raise ValueError("At least one texture worker is required")
    assets, output = assets.resolve(), output.resolve()
    if output.is_relative_to(assets):
        raise ValueError("The converted textures must be written outside the asset staging tree")
    asset_report = load_asset_report(assets)
    textures = plan(assets, asset_report)
    if not textures:
        raise ValueError("No block-compressed textures found.")
    if finish:
        if (output / "texture-report.json").exists() or not (output / ".work").is_dir():
            raise ValueError("--finish needs a directory where encoding ran and no report was written")
        results = read_results(assets, output / ".work")
    else:
        output.mkdir(parents=True)  # a new directory only
        (output / ".godot/imported").mkdir(parents=True)
        results = run_workers(assets, output, textures, workers, godot)
    errors = [f"{source}: {result['error']}" for source, result in results.items() if "error" in result]
    if errors or set(results) != {texture["source"] for texture in textures}:
        raise RuntimeError(
            "Texture encoding is incomplete or failed; no report was written.\n  " + "\n  ".join(errors[:20])
        )
    converted, kept = [], []
    for texture in textures:
        result, text = results[texture["source"]], texture.pop("importText")
        psnr = round(result["psnr"], 2)
        if psnr < MINIMUM_PSNR:
            # Not good enough to stand in for the original: ship the original, unconverted.
            (output / texture["output"]).unlink(missing_ok=True)
            (output / texture["import"]).unlink(missing_ok=True)
            kept.append(
                {"source": texture["source"], "psnr": psnr, "width": result["width"], "height": result["height"]}
            )
            continue
        target = output / texture["import"]
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(text)
        texture.update(
            importSha256=sha256_file(target),
            outputSha256=sha256_file(output / texture["output"]),
            width=result["width"],
            height=result["height"],
            psnr=psnr,
            maxDifference=result["maxDifference"],
        )
        converted.append(texture)
    if not converted:
        raise ValueError("No texture met the quality bar; no report was written.")
    worst = min(converted, key=lambda texture: texture["psnr"])
    report = {
        "profile": TEXTURE_PROFILE,
        "assetProfile": asset_report["profile"],
        "assetPackSha256": asset_report["packSha256"],
        "astcBlock": "4x4",
        "minimumPsnr": MINIMUM_PSNR,
        "lowestPsnr": worst["psnr"],
        "lowestPsnrTexture": worst["source"],
        "megapixels": round(sum(texture["width"] * texture["height"] for texture in converted) / 1e6, 1),
        "keptOriginal": kept,
        "textures": converted,
    }
    (output / "texture-report.json").write_text(json.dumps(report, indent=1) + "\n")
    return report


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--assets", type=Path, required=True, help="asset staging tree (read only)")
    parser.add_argument("--output", type=Path, required=True, help="new directory for the converted textures")
    parser.add_argument("--workers", type=int, default=max(1, os.cpu_count() or 1))
    parser.add_argument("--godot", type=Path, default=ROOT / "scripts/godot.sh")
    parser.add_argument(
        "--finish", action="store_true", help="write the report for a directory that was already encoded"
    )
    options = parser.parse_args()
    try:
        done = transcode(options.assets, options.output, options.workers, options.godot, options.finish)
    except (ValueError, RuntimeError) as error:
        raise SystemExit(str(error)) from None
    print(
        f"Converted {len(done['textures'])} textures ({done['megapixels']} megapixels) to ASTC {done['astcBlock']}; "
        f"lowest PSNR {done['lowestPsnr']} dB. {len(done['keptOriginal'])} kept in their original format for quality."
    )


if __name__ == "__main__":
    main()
