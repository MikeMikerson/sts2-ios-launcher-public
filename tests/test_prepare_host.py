import hashlib
import json
import struct
import tempfile
import unittest
import zlib
from pathlib import Path

import prepare_full_game_project as host
import prepare_game_files as game_files
from prepare_full_game_xcode import opaque_png

ROOT = Path(__file__).resolve().parents[1]


class PlaceholderIconTests(unittest.TestCase):
    def test_is_an_opaque_rgb_png_of_the_size_xcode_wants(self):
        data = host.placeholder_icon(16)
        self.assertEqual(data[:8], b"\x89PNG\r\n\x1a\n")
        width, height, depth, color = struct.unpack(">IIBB", data[16:26])
        self.assertEqual((width, height, depth, color), (16, 16, 8, 2))
        # The Xcode step takes it as it is, because it has no alpha channel.
        self.assertEqual(opaque_png(data), data)

    def test_rows_have_the_stated_length(self):
        data = host.placeholder_icon(16)
        length = struct.unpack(">I", data[33:37])[0]
        self.assertEqual(len(zlib.decompress(data[41 : 41 + length])), 16 * (16 * 3 + 1))


class BuiltInModTests(unittest.TestCase):
    def catalog(self, root: Path, data: bytes, pinned: bytes) -> Path:
        (root / "Good.dll").write_bytes(data)
        (root / "Good.json").write_bytes(b"{}")
        entry = {
            "id": "Good",
            "dll": str(root / "Good.dll"),
            "manifest": str(root / "Good.json"),
            "dllSha256": hashlib.sha256(pinned).hexdigest(),
            "manifestSha256": hashlib.sha256(b"{}").hexdigest(),
        }
        (root / "builtin.json").write_text(json.dumps({"mods": [entry]}))
        return root / "builtin.json"

    def test_lists_each_checked_file_with_its_place_in_the_original_folder(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog = self.catalog(Path(directory), b"mod", b"mod")
            self.assertEqual([name for _, name in game_files.built_in_mods(catalog)], ["mods/Good.dll", "mods/Good.json"])

    def test_an_optional_mod_setup_did_not_fetch_is_left_out(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog = self.catalog(Path(directory), b"mod", b"mod")
            data = json.loads(catalog.read_text())
            data["mods"][0]["optional"] = True
            (Path(directory) / "Good.dll").unlink()
            catalog.write_text(json.dumps(data))
            self.assertEqual(game_files.built_in_mods(catalog), [])

    def test_the_catalog_pins_match_the_dependency_pins(self):
        catalog = json.loads((ROOT / "mods/builtin.json").read_text())
        pins = json.loads((ROOT / "tools/dependencies.json").read_text())["builtin_mods"]
        for entry in catalog["mods"]:
            files = pins[entry["id"]]["files"]
            self.assertEqual(files[f"{entry['id']}.dll"]["sha256"], entry["dllSha256"])
            self.assertEqual(files[f"{entry['id']}.json"]["sha256"], entry["manifestSha256"])
            self.assertEqual(entry["dll"], f".tools/builtin-mods/{entry['id']}.dll")

    def test_a_mod_that_differs_from_its_catalog_stops(self):
        with tempfile.TemporaryDirectory() as directory:
            catalog = self.catalog(Path(directory), b"mod", b"other")
            with self.assertRaisesRegex(ValueError, "does not match its catalog"):
                game_files.built_in_mods(catalog)


if __name__ == "__main__":
    unittest.main()
