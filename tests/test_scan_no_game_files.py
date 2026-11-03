import hashlib
import tempfile
import unittest
import zipfile
from pathlib import Path

import godot_pack
import scan_no_game_files as scan

HOST_FILES = {
    "project.binary": b"settings",
    ".godot/extension_list.cfg": b"list",
    ".godot/uid_cache.bin": b"uids",
    ".godot/exported/133200997/export-abc-Launcher.scn": b"scene",
    "Launcher.tscn.remap": b"remap",
    "Launcher.cs": b"\n",
    "addons/fmod/fmod.gdextension": b"fmod",
    "icon.png": b"placeholder icon",
}
MACHO = b"\xcf\xfa\xed\xfe" + b"\0" * 64


class ScanTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def pack(self, files: dict[str, bytes], name: str = "pack.pck") -> Path:
        path = self.root / name
        with path.open("wb") as stream:
            godot_pack.write_pack(stream, files.items())
        return path

    def ipa(self, pack_files=None, others=None) -> Path:
        pack = self.pack(HOST_FILES if pack_files is None else pack_files)
        path = self.root / "app.ipa"
        with zipfile.ZipFile(path, "w") as archive:
            archive.write(pack, "Payload/FullGame.app/FullGame.pck")
            archive.writestr("Payload/FullGame.app/FullGame", MACHO)
            for name, data in (others or {}).items():
                archive.writestr(name, data)
        return path

    def test_a_host_only_app_passes(self):
        self.assertEqual(scan.scan(self.ipa()), [])

    def test_a_game_assembly_anywhere_fails(self):
        problems = scan.scan(self.ipa(others={"Payload/FullGame.app/Frameworks/x/sts2.dll": b"code"}))
        self.assertEqual(len(problems), 1)
        self.assertIn("sts2.dll", problems[0])

    def test_any_loose_assembly_or_second_pack_fails(self):
        problems = scan.scan(self.ipa(others={"Payload/FullGame.app/Other.dll": b"code", "Payload/FullGame.app/game.pck": b"x"}))
        self.assertEqual(len(problems), 2)

    def test_release_info_fails(self):
        self.assertTrue(scan.scan(self.ipa(others={"Payload/FullGame.app/release_info.json": b"{}"})))

    def test_a_game_resource_in_the_main_pack_fails(self):
        problems = scan.scan(self.ipa(pack_files=HOST_FILES | {"scenes/game.tscn": b"scene"}))
        self.assertEqual(problems, ["the main pack has scenes/game.tscn"])

    def test_a_warmup_scene_in_the_main_pack_fails(self):
        self.assertTrue(scan.scan(self.ipa(pack_files=HOST_FILES | {"warmup/inventory.json": b"{}"})))

    def test_script_text_in_the_main_pack_fails(self):
        problems = scan.scan(self.ipa(pack_files=HOST_FILES | {"Launcher.cs": b"public class Launcher {}"}))
        self.assertEqual(problems, ["the main pack has script text in Launcher.cs"])

    def test_a_binary_embedding_a_game_assembly_fails(self):
        problems = scan.scan(self.ipa(others={"Payload/FullGame.app/Frameworks/Lib": MACHO + b"sts2.dll\0"}))
        self.assertEqual(problems, ["Lib embeds an assembly named sts2.dll"])

    def test_a_binary_with_a_name_from_game_code_fails(self):
        problems = scan.scan(self.ipa(others={"Payload/FullGame.app/Frameworks/Lib": MACHO + b"NCursorManager"}))
        self.assertEqual(problems, ["Lib contains the game's name NCursorManager"])

    def test_an_app_reference_to_the_game_by_name_is_not_a_game_file(self):
        self.assertEqual(scan.scan(self.ipa(others={"Payload/FullGame.app/Frameworks/Lib": MACHO + b"sts2\0"})), [])

    def game_files(self, entries: dict[str, bytes]) -> Path:
        folder = self.root / "game"
        folder.mkdir()
        (folder / "original").mkdir()
        for name in scan.ORIGINAL_ASSEMBLIES:
            (folder / "original" / name).write_bytes(hashlib.sha512(name.encode()).digest() * 512)
        for name in scan.UNCHANGED_ASSEMBLIES:
            (folder / name).write_bytes(hashlib.sha512(name.encode()).digest() * 512)
        self.pack(entries, "../root.pck").rename(folder / "original" / scan.GAME_PACKS[0])
        return folder

    def test_pieces_of_a_prepared_assembly_fail(self):
        folder = self.game_files({"scenes/game.tscn": b"game scene"})
        piece = (folder / "original/sts2.dll").read_bytes()[:scan.SAMPLE_SIZE]
        problems = scan.scan(self.ipa(others={"Payload/FullGame.app/data.bin": piece}), folder)
        self.assertEqual(len(problems), 1)
        self.assertIn("pieces of sts2.dll are in data.bin", problems[0])

    def test_a_vendored_file_the_game_also_ships_passes_but_only_with_the_vendored_bytes(self):
        vendored = scan.VENDORED_FILES[0].read_bytes()
        name = "addons/spine/spine_godot_extension.gdextension"
        folder = self.game_files({name: vendored})
        self.assertEqual(scan.scan(self.ipa(HOST_FILES | {name: vendored}), folder), [])
        other = self.root / "other"
        other.mkdir()
        (self.root / "game").rename(other / "game")
        folder = self.game_files({name: vendored + b"changed"})
        problems = scan.scan(self.ipa(HOST_FILES | {name: vendored + b"changed"}), folder)
        self.assertEqual(len(problems), 1)
        self.assertIn("is the game's", problems[0])

    def test_a_main_pack_entry_that_is_a_game_file_fails(self):
        folder = self.game_files({"images/icon_1024.png": HOST_FILES["icon.png"]})
        problems = scan.scan(self.ipa(), folder)
        self.assertEqual(problems, ["the main pack's icon.png is the game's images/icon_1024.png"])

    def test_clean_app_passes_against_the_prepared_files(self):
        self.assertEqual(scan.scan(self.ipa(), self.game_files({"scenes/game.tscn": b"game scene"})), [])

    def test_an_ipa_without_one_app_fails(self):
        path = self.root / "empty.ipa"
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr("Payload/readme.txt", b"")
        self.assertEqual(scan.scan(path), ["the IPA holds 0 apps"])


if __name__ == "__main__":
    unittest.main()
