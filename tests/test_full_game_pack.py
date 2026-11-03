import hashlib
import json
import tempfile
import unittest
from pathlib import Path

import build_full_game_pack as packer
import godot_pack
import profiles


class Fixture:
    def __init__(self, root: Path):
        self.assets = root / "assets"
        self.output = root / "out" / "game.pck"
        self.game = {
            "project.binary": b"ECFG settings",
            ".godot/uid_cache.bin": b"uids",
            ".godot/global_script_class_cache.cfg": b'list=[{"class": &"AudioManagerProxy"}]\n',
            ".godot/extension_list.cfg": b"res://addons/sentry/sentry.gdextension\n",
            "addons/fmod/fmod.gdextension": b"desktop only",
            "scenes/game.tscn": b"[gd_scene]",
            "steam/steam_appid.txt": b"0",
        }

    def write(self):
        staged = []
        for name, data in self.game.items():
            target = self.assets / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(data)
            staged.append({"path": name, "sha256": hashlib.sha256(data).hexdigest()})
        (self.assets / "asset-report.json").write_text(
            json.dumps({"profile": profiles.ASSET_PROFILE, "packSha256": "fixture", "staged": staged})
        )
        return self


class GamePackTests(unittest.TestCase):
    def build(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        return Fixture(Path(directory.name)).write()

    def test_game_pack_holds_only_the_games_files(self):
        fixture = self.build()
        report = packer.build_game_pack(fixture.assets, fixture.output)
        built = dict(godot_pack.read_pack(fixture.output))
        self.assertEqual(report["entries"], len(built))
        self.assertEqual(built["project.binary"], fixture.game["project.binary"])
        self.assertEqual(built[packer.UID_CACHE], fixture.game[packer.UID_CACHE])
        self.assertIn(packer.CLASS_CACHE, built)
        self.assertIn("scenes/game.tscn", built)
        self.assertFalse(any(name.endswith(".gdextension") or name.startswith("steam/") for name in built))
        with fixture.output.open("rb") as stream:
            self.assertEqual(report["outputSha256"], hashlib.file_digest(stream, "sha256").hexdigest())

    def test_game_pack_adds_the_files_made_from_the_game(self):
        fixture = self.build()
        fixture.output.parent.mkdir(parents=True, exist_ok=True)
        made = fixture.output.with_name("inventory.json")
        made.write_bytes(b"{}")
        packer.build_game_pack(fixture.assets, fixture.output, extra={"warmup/inventory.json": made})
        self.assertEqual(dict(godot_pack.read_pack(fixture.output))["warmup/inventory.json"], b"{}")

    def test_game_pack_refuses_an_added_file_that_shadows_a_game_file(self):
        fixture = self.build()
        fixture.output.parent.mkdir(parents=True, exist_ok=True)
        made = fixture.output.with_name("scene.tscn")
        made.write_bytes(b"other")
        with self.assertRaisesRegex(godot_pack.PackError, "shadows"):
            packer.build_game_pack(fixture.assets, fixture.output, extra={"scenes/game.tscn": made})
        self.assertFalse(fixture.output.exists())

    def test_game_pack_never_overwrites(self):
        fixture = self.build()
        packer.build_game_pack(fixture.assets, fixture.output)
        with self.assertRaisesRegex(godot_pack.PackError, "new pack destination"):
            packer.build_game_pack(fixture.assets, fixture.output)

    def test_changed_staged_resource_is_rejected_without_partial_output(self):
        fixture = self.build()
        (fixture.assets / "scenes/game.tscn").write_bytes(b"edited")
        with self.assertRaisesRegex(godot_pack.PackError, "Staged resource changed"):
            packer.build_game_pack(fixture.assets, fixture.output)
        self.assertFalse(fixture.output.exists())
        self.assertFalse(fixture.output.with_name("game.pck.partial").exists())


class ConvertedTextureTests(unittest.TestCase):
    """The pack ships converted textures only when they were made from exactly the staged originals."""

    IMPORT = b'[remap]\npath.bptc="res://.godot/imported/a.png-1.bptc.ctex"\n'
    NEW_IMPORT = b'[remap]\npath.astc="res://.godot/imported/a.png-1.astc.ctex"\n'

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.fixture = Fixture(Path(directory.name))
        self.fixture.game["images/a.png.import"] = self.IMPORT
        self.fixture.game[".godot/imported/a.png-1.bptc.ctex"] = b"bptc"
        self.fixture.write()
        self.overlay = Path(directory.name) / "textures"
        self.entry = {
            "import": "images/a.png.import",
            "sourceImportSha256": hashlib.sha256(self.IMPORT).hexdigest(),
            "source": ".godot/imported/a.png-1.bptc.ctex",
            "sourceSha256": hashlib.sha256(b"bptc").hexdigest(),
            "output": ".godot/imported/a.png-1.astc.ctex",
            "importSha256": hashlib.sha256(self.NEW_IMPORT).hexdigest(),
            "outputSha256": hashlib.sha256(b"astc").hexdigest(),
            "psnr": 52.0,
        }
        for name, data in (("images/a.png.import", self.NEW_IMPORT), (".godot/imported/a.png-1.astc.ctex", b"astc")):
            (self.overlay / name).parent.mkdir(parents=True, exist_ok=True)
            (self.overlay / name).write_bytes(data)
        self.write_report()

    def write_report(self, **changes):
        (self.overlay / "texture-report.json").write_text(
            json.dumps(
                {
                    "profile": profiles.TEXTURE_PROFILE,
                    "assetPackSha256": "fixture",
                    "lowestPsnr": 52.0,
                    "keptOriginal": [],
                    "textures": [self.entry],
                }
                | changes
            )
        )

    def build(self):
        return packer.build_game_pack(self.fixture.assets, self.fixture.output, self.overlay)

    def assert_rejected(self, pattern):
        with self.assertRaisesRegex(packer.PackError, pattern):
            self.build()
        self.assertFalse(self.fixture.output.exists())

    def test_converted_texture_and_its_remap_replace_the_original(self):
        self.build()
        built = dict(godot_pack.read_pack(self.fixture.output))
        self.assertEqual(built["images/a.png.import"], self.NEW_IMPORT)
        self.assertEqual(built[".godot/imported/a.png-1.astc.ctex"], b"astc")
        self.assertNotIn(".godot/imported/a.png-1.bptc.ctex", built)
        self.assertEqual(built["scenes/game.tscn"], b"[gd_scene]")

    def test_without_an_overlay_the_original_ships(self):
        packer.build_game_pack(self.fixture.assets, self.fixture.output)
        built = dict(godot_pack.read_pack(self.fixture.output))
        self.assertEqual(built[".godot/imported/a.png-1.bptc.ctex"], b"bptc")
        self.assertEqual(built["images/a.png.import"], self.IMPORT)

    def test_texture_pack_says_what_it_replaces_and_holds_the_converted_files(self):
        output = self.fixture.output.with_name("textures.pck")
        report = packer.build_texture_pack(self.overlay, self.fixture.assets, output)
        built = godot_pack.read_pack(output)
        self.assertEqual(report["entries"], 3)
        self.assertEqual(
            [name for name, _ in built],
            [packer.TEXTURE_MANIFEST, "images/a.png.import", ".godot/imported/a.png-1.astc.ctex"],
        )
        manifest = json.loads(built[0][1])
        self.assertEqual(manifest["assetPackSha256"], "fixture")
        self.assertEqual(manifest["textures"][0]["source"], ".godot/imported/a.png-1.bptc.ctex")
        self.assertEqual(built[1][1], self.NEW_IMPORT)

    def test_texture_pack_refuses_a_changed_converted_file(self):
        (self.overlay / ".godot/imported/a.png-1.astc.ctex").write_bytes(b"tampered")
        with self.assertRaisesRegex(packer.PackError, "Staged resource changed"):
            packer.build_texture_pack(self.overlay, self.fixture.assets, self.fixture.output)
        self.assertFalse(self.fixture.output.exists())

    def test_overlay_for_other_assets_is_rejected(self):
        self.write_report(assetPackSha256="another pack")
        self.assert_rejected("not made for this asset profile")

    def test_texture_made_from_a_different_original_is_rejected(self):
        self.entry["sourceSha256"] = hashlib.sha256(b"other").hexdigest()
        self.write_report()
        self.assert_rejected("not made from the staged original")

    def test_changed_converted_file_is_rejected(self):
        (self.overlay / ".godot/imported/a.png-1.astc.ctex").write_bytes(b"tampered")
        self.assert_rejected("Staged resource changed")

    def test_output_that_would_shadow_a_staged_file_is_rejected(self):
        self.entry["output"] = "scenes/game.tscn"
        self.write_report()
        self.assert_rejected("unusable path")


class AdjustedSceneTests(unittest.TestCase):
    """The pack ships phone-adjusted scenes only when they were made from exactly the staged originals."""

    ADJUSTED = b"[gd_scene adjusted]"

    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.fixture = Fixture(Path(directory.name)).write()
        self.overlay = Path(directory.name) / "scenes-overlay"
        (self.overlay / "scenes").mkdir(parents=True)
        (self.overlay / "scenes/game.tscn").write_bytes(self.ADJUSTED)
        self.scene = {
            "path": "scenes/game.tscn",
            "sourceSha256": hashlib.sha256(b"[gd_scene]").hexdigest(),
            "outputSha256": hashlib.sha256(self.ADJUSTED).hexdigest(),
            "emitters": [],
        }
        self.write_report()

    def write_report(self, **changes):
        (self.overlay / "scene-report.json").write_text(
            json.dumps(
                {
                    "profile": profiles.SCENE_PROFILE,
                    "assetPackSha256": "fixture",
                    "emittersAdjusted": 2,
                    "scenes": [self.scene],
                }
                | changes
            )
        )

    def assert_rejected(self, pattern):
        with self.assertRaisesRegex(packer.PackError, pattern):
            packer.build_game_pack(self.fixture.assets, self.fixture.output, scenes=self.overlay)
        self.assertFalse(self.fixture.output.exists())

    def test_adjusted_scene_replaces_the_original(self):
        packer.build_game_pack(self.fixture.assets, self.fixture.output, scenes=self.overlay)
        built = dict(godot_pack.read_pack(self.fixture.output))
        self.assertEqual(built["scenes/game.tscn"], self.ADJUSTED)

    def test_overlay_for_other_assets_is_rejected(self):
        self.write_report(assetPackSha256="another pack")
        self.assert_rejected("not made for this asset profile")

    def test_scene_made_from_a_different_original_is_rejected(self):
        self.scene["sourceSha256"] = hashlib.sha256(b"other").hexdigest()
        self.write_report()
        self.assert_rejected("not made from the staged original")

    def test_changed_adjusted_scene_is_rejected(self):
        (self.overlay / "scenes/game.tscn").write_bytes(b"tampered")
        self.assert_rejected("Staged resource changed")

    def test_a_file_that_is_not_a_staged_scene_is_rejected(self):
        self.scene["path"] = "steam/steam_appid.txt"
        self.write_report()
        self.assert_rejected("not made from the staged original")


if __name__ == "__main__":
    unittest.main()
