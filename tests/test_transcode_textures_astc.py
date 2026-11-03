import hashlib
import tempfile
import unittest
from pathlib import Path

from transcode_textures_astc import plan, rewrite_import

IMPORT = """[remap]

importer="texture"
type="CompressedTexture2D"
uid="uid://dfsy7a13qlfcv"
path.bptc="res://.godot/imported/era_atlas.png-907b.bptc.ctex"
metadata={
"imported_formats": ["s3tc_bptc", "etc2_astc"],
"vram_texture": true
}
"""


class TextureTranscodePlanTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.assets = Path(self.directory.name)
        self.files = {
            "images/era_atlas.png.import": IMPORT.encode(),
            ".godot/imported/era_atlas.png-907b.bptc.ctex": b"bptc data",
            "images/plain.png.import": b'[remap]\npath="res://.godot/imported/plain.png-1.ctex"\n',
            ".godot/imported/plain.png-1.ctex": b"webp data",
        }
        for name, data in self.files.items():
            (self.assets / name).parent.mkdir(parents=True, exist_ok=True)
            (self.assets / name).write_bytes(data)

    def report(self):
        return {
            "staged": [{"path": name, "sha256": hashlib.sha256(data).hexdigest()} for name, data in self.files.items()]
        }

    def test_remap_is_pointed_at_the_astc_file_and_nothing_else_changes(self):
        text, source, output = rewrite_import(IMPORT)
        self.assertEqual(source, ".godot/imported/era_atlas.png-907b.bptc.ctex")
        self.assertEqual(output, ".godot/imported/era_atlas.png-907b.astc.ctex")
        self.assertEqual(
            text,
            IMPORT.replace(
                'path.bptc="res://.godot/imported/era_atlas.png-907b.bptc.ctex"',
                'path.astc="res://.godot/imported/era_atlas.png-907b.astc.ctex"',
            ),
        )

    def test_s3tc_remaps_are_handled_and_other_imports_are_left_alone(self):
        self.assertEqual(
            rewrite_import(IMPORT.replace("bptc", "s3tc"))[1], ".godot/imported/era_atlas.png-907b.s3tc.ctex"
        )
        self.assertIsNone(rewrite_import('[remap]\npath="res://.godot/imported/plain.png-1.ctex"\n'))

    def test_an_import_with_two_block_compressed_remaps_is_refused(self):
        with self.assertRaises(ValueError):
            rewrite_import(IMPORT + 'path.s3tc="res://.godot/imported/era_atlas.png-907b.s3tc.ctex"\n')

    def test_plan_lists_only_block_compressed_textures_with_their_staged_hashes(self):
        textures = plan(self.assets, self.report())
        self.assertEqual(len(textures), 1)
        self.assertEqual(textures[0]["import"], "images/era_atlas.png.import")
        self.assertEqual(textures[0]["sourceSha256"], hashlib.sha256(b"bptc data").hexdigest())
        self.assertEqual(textures[0]["output"], ".godot/imported/era_atlas.png-907b.astc.ctex")

    def test_plan_refuses_inputs_that_differ_from_the_hash_report(self):
        report = self.report()
        (self.assets / ".godot/imported/era_atlas.png-907b.bptc.ctex").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            plan(self.assets, report)

    def test_plan_refuses_a_texture_the_report_does_not_list(self):
        report = self.report()
        report["staged"] = [entry for entry in report["staged"] if not entry["path"].endswith(".bptc.ctex")]
        with self.assertRaises(ValueError):
            plan(self.assets, report)


if __name__ == "__main__":
    unittest.main()
