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

    def built_in_spec(self, url="<release>/Mod.dll"):
        import hashlib
        return {"builtin_mods": {"Mod": {"version": "1", "local_stand_in": "stand-in", "files": {
            "Mod.dll": {"url": url, "sha256": hashlib.sha256(b"mod").hexdigest()}}}}}

    def test_built_in_mod_comes_from_its_local_stand_in_until_released(self):
        (self.root / "stand-in").mkdir()
        (self.root / "stand-in/Mod.dll").write_bytes(b"mod")
        with patch.object(setup, "ROOT", self.root), patch.object(setup, "BUILTIN_MODS", self.root / "out"):
            setup.provision_builtin_mods(self.built_in_spec())
        self.assertEqual((self.root / "out/Mod.dll").read_bytes(), b"mod")

    def relay(self, environment, local=None):
        (self.root / "out").mkdir(exist_ok=True)
        (self.root / "out/QuickMatch.dll").write_bytes(b"mod")
        (self.root / "out/QuickMatchRelay.cfg").unlink(missing_ok=True)
        if local is not None:
            (self.root / "QuickMatchRelay.cfg").write_text(local)
        with patch.object(setup, "ROOT", self.root), patch.object(setup, "BUILTIN_MODS", self.root / "out"), \
                patch.object(setup, "RELAY_FILE", self.root / "QuickMatchRelay.cfg"), patch.dict(setup.os.environ, environment, clear=True):
            setup.provision_quickmatch_relay()
        relay = self.root / "out/QuickMatchRelay.cfg"
        return relay.read_text() if relay.is_file() else None

    def test_quickmatch_relay_is_chosen_at_build_time(self):
        self.assertIsNone(self.relay({}))
        self.assertEqual(self.relay({}, local="relay = https://own.example.test\n"), "relay = https://own.example.test\n")
        self.assertEqual(self.relay({"QUICKMATCH_RELAY": "wss://env.example.test/"}), "relay = wss://env.example.test\n")
        for bad in ("http://plain.example.test", "wss://example.test:8443", "wss://a@example.com", "wss://example.test/path",
                    "wss://10.0.0.1", "wss://example.test:notaport"):
            with self.subTest(bad=bad), self.assertRaisesRegex(ValueError, "QUICKMATCH_RELAY"):
                self.relay({"QUICKMATCH_RELAY": bad})

    def test_relay_file_is_checked_as_the_app_reads_it(self):
        good = "# QuickMatch relay\nrelay = https://Own.Example.test:443/\nenabled = true\n"
        self.assertEqual(self.relay({}, local=good), "relay = https://own.example.test\n")
        self.assertIsNone(self.relay({}, local="relay = wss://own.example.test\nenabled = off\n"))
        self.assertIsNone(self.relay({}, local="# no relay yet\nenabled = true\n"))
        for bad in (
            "relay = http://plain.example.test\n",
            "relay = wss://own.example.test:8443\n",
            "relay = wss://own.example.test/v1/relay\n",
            "relay = wss://user@own.example.test\n",
            "relay = wss://192.168.1.10\n",
            "relay = wss://localhost\n",
            "relay = https://a.example.test\nrelay = https://b.example.test\n",
            "relay https://own.example.test\n",
            "relay = https://own.example.test\nenabled = maybe\n",
            "relay = https://own.example.test\nport = 443\n",
        ):
            with self.subTest(bad=bad), self.assertRaisesRegex(ValueError, "QuickMatchRelay.cfg line"):
                self.relay({}, local=bad)

    def test_the_environment_wins_over_the_file(self):
        self.assertEqual(
            self.relay({"QUICKMATCH_RELAY": "wss://env.example.test"}, local="relay = https://own.example.test\n"),
            "relay = wss://env.example.test\n",
        )

    def test_built_in_mod_with_no_release_and_no_stand_in_is_left_out(self):
        (self.root / "out").mkdir()
        (self.root / "out/Mod.dll").write_bytes(b"from an earlier setup")
        with patch.object(setup, "ROOT", self.root), patch.object(setup, "BUILTIN_MODS", self.root / "out"):
            setup.provision_builtin_mods(self.built_in_spec())
        self.assertFalse((self.root / "out").exists())

    def test_built_in_mod_that_differs_from_its_pin_stops(self):
        (self.root / "stand-in").mkdir()
        (self.root / "stand-in/Mod.dll").write_bytes(b"other")
        with patch.object(setup, "ROOT", self.root), patch.object(setup, "BUILTIN_MODS", self.root / "out"):
            with self.assertRaisesRegex(ValueError, "does not match its pin"):
                setup.provision_builtin_mods(self.built_in_spec())

    def test_released_built_in_mod_is_downloaded(self):
        (self.root / "cache").mkdir()
        (self.root / "cache/Mod.dll").write_bytes(b"mod")
        with patch.object(setup, "ROOT", self.root), patch.object(setup, "BUILTIN_MODS", self.root / "out"), \
                patch.object(setup, "download", return_value=self.root / "cache/Mod.dll") as download:
            setup.provision_builtin_mods(self.built_in_spec("https://example.invalid/Mod.dll"))
        download.assert_called_once()
        self.assertEqual((self.root / "out/Mod.dll").read_bytes(), b"mod")

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
