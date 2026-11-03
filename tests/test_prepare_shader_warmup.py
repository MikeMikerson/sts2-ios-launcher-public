import json
import tempfile
import unittest
from pathlib import Path

from prepare_shader_warmup import MATERIALS_PER_SCENE, collect, prepare, render_scene, signature, sub_resources

SCENE = """[gd_scene load_steps=9 format=3]

[ext_resource type="Texture2D" path="res://images/a.png" id="1_a"]

[sub_resource type="Curve" id="Curve_a"]
_data = [Vector2(0, 1), 0.0, 0.0, 0, 0]

[sub_resource type="CurveTexture" id="CurveTexture_a"]
curve = SubResource("Curve_a")

[sub_resource type="ParticleProcessMaterial" id="Mat_plain"]
particle_flag_disable_z = true
gravity = Vector3(0, 98, 0)

[sub_resource type="ParticleProcessMaterial" id="Mat_plain_other_numbers"]
particle_flag_disable_z = true
gravity = Vector3(0, 0, 0)

[sub_resource type="ParticleProcessMaterial" id="Mat_curve"]
particle_flag_disable_z = true
gravity = Vector3(0, 0, 0)
scale_curve = SubResource("CurveTexture_a")

[sub_resource type="ParticleProcessMaterial" id="Mat_box"]
particle_flag_disable_z = true
emission_shape = 3
gravity = Vector3(0, 0, 0)

[sub_resource type="AtlasTexture" id="Atlas_a"]
atlas = ExtResource("1_a")

[sub_resource type="ParticleProcessMaterial" id="Mat_outside"]
emission_point_texture = SubResource("Atlas_a")

[node name="Vfx" type="Node2D"]

[node name="A" type="GPUParticles2D" parent="."]
process_material = SubResource("Mat_plain")
"""


class ShaderWarmupInventoryTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.assets = Path(self.directory.name) / "assets"
        (self.assets / "scenes/vfx").mkdir(parents=True)
        (self.assets / "shaders").mkdir()
        (self.assets / "scenes/vfx/a.tscn").write_text(SCENE)
        (self.assets / "scenes/vfx/b.tscn").write_text(SCENE.replace("Mat_", "Other_"))
        (self.assets / "shaders/glow.gdshader").write_text("shader_type canvas_item;")
        (self.assets / "shaders/shared.gdshaderinc").write_text("// include")

    def test_sub_resources_are_read_with_type_and_body(self):
        resources = sub_resources(SCENE)
        self.assertEqual(resources["CurveTexture_a"], ("CurveTexture", 'curve = SubResource("Curve_a")'))
        self.assertEqual(len(resources), 8)

    def test_numbers_do_not_distinguish_materials_but_switches_and_set_properties_do(self):
        resources = sub_resources(SCENE)
        plain, other, curve, box = (
            signature(resources[name][1]) for name in ("Mat_plain", "Mat_plain_other_numbers", "Mat_curve", "Mat_box")
        )
        self.assertEqual(plain, other)
        self.assertEqual(len({plain, curve, box}), 3)

    def test_distinct_materials_are_collected_once_most_used_first_and_outside_references_skipped(self):
        materials, counts = collect(self.assets)
        self.assertEqual(counts, {"particleMaterialsSeen": 10, "particleMaterialsSkipped": 2})
        self.assertEqual(len(materials), 3)
        self.assertIn("gravity", materials[0][-1][2])
        self.assertNotIn("scale_curve", materials[0][-1][2])  # the plain material is used four times, the others twice

    def test_curve_textures_become_one_shared_texture_and_no_game_texture_is_referenced(self):
        scene = render_scene(collect(self.assets)[0])
        self.assertIn('scale_curve = SubResource("shared_CurveTexture")', scene)
        self.assertNotIn("Curve_a", scene)
        self.assertNotIn("ExtResource", scene)
        self.assertEqual(scene.count('type="GPUParticles2D"'), 3)
        self.assertEqual(scene.count("emitting = false"), 3)

    def test_prepare_writes_small_scenes_an_inventory_and_refuses_an_existing_directory(self):
        output = Path(self.directory.name) / "warmup"
        inventory = prepare(self.assets, output)
        self.assertEqual(inventory["particleMaterials"], 3)
        self.assertEqual(inventory["particleScenes"], ["particles_000.tscn"])
        self.assertLessEqual(3, MATERIALS_PER_SCENE)
        self.assertEqual(inventory["canvasShaders"], ["res://shaders/glow.gdshader"])
        self.assertEqual(json.loads((output / "inventory.json").read_text()), inventory)
        self.assertEqual(SCENE, (self.assets / "scenes/vfx/a.tscn").read_text())
        with self.assertRaises(FileExistsError):
            prepare(self.assets, output)

    def test_an_asset_tree_without_particle_materials_is_refused(self):
        empty = Path(self.directory.name) / "empty"
        empty.mkdir()
        with self.assertRaises(ValueError):
            prepare(empty, Path(self.directory.name) / "none")


if __name__ == "__main__":
    unittest.main()
