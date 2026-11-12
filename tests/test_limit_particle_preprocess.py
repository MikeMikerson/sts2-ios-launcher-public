import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from limit_particle_preprocess import (
    SCENE_STEP_BUDGET,
    adjust_scene,
    limited_rate,
    prepare,
    scene_limit,
    scene_steps,
)


def emitter(name, *lines):
    return f'[node name="{name}" type="GPUParticles2D" parent="."]\n' + "\n".join(lines) + "\n\n"


SCENE = (
    '[gd_scene format=3]\n\n[node name="Bg" type="Node2D"]\n\n'
    + emitter("clouds", "amount = 40", "lifetime = 400.0", "preprocess = 400.0", "fixed_fps = 0")
    + emitter("fog", "preprocess = 100.0")
    + emitter("embers", "preprocess = 6.0", "fixed_fps = 60")
    + emitter("sparkle", "preprocess = 5.0")
    + emitter("rigid", "preprocess = 100.0", "interpolate = false")
    + '[node name="Label" type="Label" parent="."]\ntext = "preprocess = 999.0"\n'
)


class ParticlePreprocessLimitTests(unittest.TestCase):
    def test_rate_is_lowered_only_when_the_start_would_exceed_the_limit(self):
        self.assertIsNone(limited_rate(5.0, 30))
        self.assertIsNone(limited_rate(10.0, 0))  # 0 means the default rate of 30: exactly 300 steps
        self.assertEqual(limited_rate(400.0, 0), 1)
        self.assertEqual(limited_rate(100.0, 30), 3)
        self.assertEqual(limited_rate(6.0, 60), 50)
        self.assertEqual(limited_rate(6.0, 60, step_limit=40), 6)

    def test_scene_keeps_presimulated_time_and_changes_only_the_rate_of_long_starts(self):
        adjusted, changes = adjust_scene(SCENE)
        self.assertEqual([change["node"] for change in changes], ["clouds", "fog", "embers"])
        self.assertEqual(
            [(change["stepsBefore"], change["stepsAfter"]) for change in changes],
            [(12000, 400), (3000, 300), (360, 300)],
        )
        self.assertIn("preprocess = 400.0\nfixed_fps = 1", adjusted)
        self.assertIn('[node name="fog" type="GPUParticles2D" parent="."]\nfixed_fps = 3\npreprocess = 100.0', adjusted)
        self.assertIn("preprocess = 6.0\nfixed_fps = 50", adjusted)
        self.assertEqual(adjusted.count("preprocess = "), SCENE.count("preprocess = "))

    def test_short_starts_uninterpolated_emitters_and_other_nodes_are_untouched(self):
        adjusted, _ = adjust_scene(SCENE)
        self.assertIn(emitter("sparkle", "preprocess = 5.0"), adjusted)
        self.assertIn(emitter("rigid", "preprocess = 100.0", "interpolate = false"), adjusted)
        self.assertIn('text = "preprocess = 999.0"', adjusted)

    def test_a_scene_with_many_emitters_gets_a_lower_limit_to_stay_in_budget(self):
        crowded = "[gd_scene format=3]\n\n" + "".join(
            emitter(f"spark{index}", "preprocess = 6.0", "fixed_fps = 60") for index in range(75)
        )
        self.assertEqual(scene_steps(crowded, 300), 75 * 300)
        limit = scene_limit(crowded)
        self.assertEqual(limit, 40)
        adjusted, changes = adjust_scene(crowded, limit)
        self.assertEqual(len(changes), 75)
        self.assertLessEqual(scene_steps(adjusted, limit), SCENE_STEP_BUDGET)
        self.assertIn("fixed_fps = 6", adjusted)

    def test_prepare_writes_only_changed_scenes_with_a_report_and_refuses_changed_input(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        assets = Path(directory.name) / "assets"
        (assets / "scenes").mkdir(parents=True)
        files = {"scenes/bg.tscn": SCENE.encode(), "scenes/plain.tscn": b"[gd_scene format=3]\n"}
        for name, data in files.items():
            (assets / name).write_bytes(data)
        (assets / "asset-report.json").write_text(
            json.dumps(
                {
                    "profile": "v0.113.0-full-assets-v1",
                    "packSha256": "fixture",
                    "staged": [
                        {"path": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in files.items()
                    ],
                }
            )
        )
        report = prepare(assets, Path(directory.name) / "out")
        self.assertEqual([scene["path"] for scene in report["scenes"]], ["scenes/bg.tscn"])
        self.assertEqual((report["emittersAdjusted"], report["stepsBefore"], report["stepsAfter"]), (3, 15360, 1000))
        written = (Path(directory.name) / "out/scenes/bg.tscn").read_bytes()
        self.assertEqual(hashlib.sha256(written).hexdigest(), report["scenes"][0]["outputSha256"])
        self.assertFalse((Path(directory.name) / "out/scenes/plain.tscn").exists())
        self.assertEqual(SCENE.encode(), (assets / "scenes/bg.tscn").read_bytes())
        (assets / "scenes/bg.tscn").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            prepare(assets, Path(directory.name) / "second")


if __name__ == "__main__":
    unittest.main()
