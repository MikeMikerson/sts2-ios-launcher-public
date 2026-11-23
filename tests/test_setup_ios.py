import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import setup_ios as setup


class SetupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()

    def game(self, folder):
        contents = folder / "SlayTheSpire2.app/Contents"
        managed = contents / "Resources/data_sts2_macos_arm64"
        managed.mkdir(parents=True)
        (managed / "sts2.dll").write_bytes(b"assembly fixture")
        (contents / "Resources/Slay the Spire 2.pck").write_bytes(b"pack fixture")
        return contents

    def test_accepts_game_folder_app_contents_and_managed_directory(self):
        contents = self.game(self.root / "Steam Library/game")
        for path in (contents.parent.parent, contents.parent, contents, contents / "Resources/data_sts2_macos_arm64"):
            self.assertEqual(setup.normalize_install(path), contents)

    def test_discovers_custom_steam_library_with_spaces(self):
        steam = self.root / "Library/Application Support/Steam/steamapps"
        steam.mkdir(parents=True)
        library = self.root / "Other Disk/My Games"
        contents = self.game(library / "steamapps/common/Slay the Spire 2")
        (steam / "libraryfolders.vdf").write_text('"libraryfolders" { "1" { "path" "' + str(library) + '" } }')
        self.assertEqual(setup.discover_game(None, self.root), contents)

    def test_requires_explicit_path_when_multiple_installations_exist(self):
        steam = self.root / "Library/Application Support/Steam/steamapps"
        self.game(steam / "common/Slay the Spire 2")
        library = self.root / "Other"
        self.game(library / "steamapps/common/Slay the Spire 2")
        (steam / "libraryfolders.vdf").write_text('"path" "' + str(library) + '"')
        with self.assertRaisesRegex(ValueError, "--game-path"):
            setup.discover_game(None, self.root)

    def test_rejects_changed_game_without_writing_it(self):
        contents = self.game(self.root / "Game")
        pack = contents / "Resources/Slay the Spire 2.pck"
        dll = contents / "Resources/data_sts2_macos_arm64/sts2.dll"
        spec = {"version": "test", "pack_sha256": setup.digest(pack), "assembly_sha256": setup.digest(dll)}
        setup.check_game(contents, spec)
        pack.write_bytes(b"changed")
        with self.assertRaisesRegex(ValueError, "Unsupported game input"):
            setup.check_game(contents, spec)
        self.assertEqual(pack.read_bytes(), b"changed")

    def test_rejects_unsafe_archive_paths(self):
        for name in ("../outside", "/absolute", "safe/../../outside", "safe\\..\\outside"):
            with self.assertRaises(ValueError):
                setup.safe_member(name)

    def test_cached_download_mismatch_is_not_accepted_or_replaced(self):
        cache = self.root / "cache"
        cache.mkdir()
        artifact = cache / "sample-payload.zip"
        artifact.write_bytes(b"corrupt")
        with patch.object(setup, "CACHE", cache), patch.object(setup, "ROOT", self.root):
            with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                setup.download("sample", {"url": "https://example.invalid/payload.zip", "sha256": "0" * 64})
        self.assertEqual(artifact.read_bytes(), b"corrupt")

    def test_stage_reuses_only_verified_outputs_and_rebuilds_changed_inputs(self):
        output = self.root / "analysis/assets"
        state = self.root / "state"
        state.mkdir()
        calls = []

        def action():
            calls.append(1)
            output.mkdir(parents=True)
            (output / "result").write_text("prepared")

        with patch.object(setup, "ROOT", self.root), patch.object(setup, "STATE", state):
            setup.stage("assets", {"input": "a"}, [output], action)
            setup.stage("assets", {"input": "a"}, [output], action)
            self.assertEqual(len(calls), 1)
            setup.stage("assets", {"input": "b"}, [output], action)
            self.assertEqual(len(calls), 2)
            (output / "result").write_text("corrupted")
            with self.assertRaisesRegex(ValueError, "files changed"):
                setup.stage("assets", {"input": "b"}, [output], action)

    def test_interrupted_stage_is_rebuilt(self):
        output = self.root / "analysis/assets"
        output.mkdir(parents=True)
        (output / "partial").write_text("unfinished")
        state = self.root / "state"
        state.mkdir()

        def action():
            self.assertFalse(output.exists())
            output.mkdir()
            (output / "complete").write_text("done")

        with patch.object(setup, "ROOT", self.root), patch.object(setup, "STATE", state):
            setup.stage("assets", {}, [output], action)
        self.assertTrue((output / "complete").is_file())
        self.assertFalse((output / "partial").exists())

    def test_stage_cannot_replace_source_tree(self):
        state = self.root / "state"
        state.mkdir()
        with patch.object(setup, "ROOT", self.root), patch.object(setup, "STATE", state):
            with self.assertRaisesRegex(ValueError, "source directory"):
                setup.stage("bad", {}, [self.root / "src"], lambda: None)

    def test_source_fingerprint_ignores_generated_inputs(self):
        source = self.root / "src/Host"
        source.mkdir(parents=True)
        (source / "Game.cs").write_text("class Game {}")
        with patch.object(setup, "ROOT", self.root):
            before = setup.source_fingerprint([source])
            (source / "staged").mkdir()
            (source / "staged/library.dll").write_bytes(b"compiled")
            (source / "icon.png").write_bytes(b"generated")
            self.assertEqual(before, setup.source_fingerprint([source]))


if __name__ == "__main__":
    unittest.main()
