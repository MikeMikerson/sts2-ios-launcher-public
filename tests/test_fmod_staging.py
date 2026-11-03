import hashlib
import json
import tempfile
import unittest
import zipfile
from pathlib import Path

from stage_fmod_dependencies import DEVICE_LIBRARIES, MACOS_LIBRARIES, MACOS_WRAPPER, stage_binaries


class FmodStagingTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.archive = self.root / "addon.zip"
        self.license = self.root / "LICENSE"
        self.license.write_text("Synthetic test license")
        self.output = self.root / "staged"

    def fixture(self, omitted=None, extra=None):
        with zipfile.ZipFile(self.archive, "w") as archive:
            for library in DEVICE_LIBRARIES:
                if library != omitted:
                    archive.writestr("fmod/libs/iOS/" + library, b"test static library")
            archive.writestr("fmod/libs/iOS/libGodotFmod.ios.template_release.xcframework/Info.plist", b"test metadata")
            archive.writestr("fmod/libs/iOS/libfmod_iphonesimulator.a", b"not selected")
            for library in MACOS_LIBRARIES:
                if library != omitted:
                    archive.writestr("fmod/libs/macos/" + library, b"test dynamic library")
            archive.writestr(f"fmod/libs/macos/{MACOS_WRAPPER}/wrapper", b"test wrapper")
            archive.writestr("fmod/libs/macos/libfmodL.dylib", b"not selected")
            if extra:
                archive.writestr(extra, b"unsafe")

    def test_stages_only_device_inputs_with_checksums_and_license(self):
        self.fixture()
        report = stage_binaries(self.archive, self.output, self.license)
        self.assertEqual(report["version"], "2.03.06")
        self.assertEqual(report["archive_sha256"], hashlib.sha256(self.archive.read_bytes()).hexdigest())
        self.assertFalse(any("simulator" in file["path"] or "fmodL.dylib" in file["path"] for file in report["files"]))
        self.assertTrue((self.output / "gdextension/macos" / MACOS_WRAPPER / "wrapper").is_file())
        for file in report["files"]:
            self.assertEqual(file["sha256"], hashlib.sha256((self.output / file["path"]).read_bytes()).hexdigest())
        self.assertEqual((self.output / "GODOT_FMOD_LICENSE.txt").read_text(), self.license.read_text())
        self.assertEqual(json.loads((self.output / "manifest.json").read_text()), report)

    def test_missing_library_fails_without_partial_output(self):
        self.fixture(omitted=DEVICE_LIBRARIES[0])
        with self.assertRaisesRegex(ValueError, "Missing device library"):
            stage_binaries(self.archive, self.output, self.license)
        self.assertFalse(self.output.exists())

    def test_missing_macos_library_fails_without_partial_output(self):
        self.fixture(omitted=MACOS_LIBRARIES[0])
        with self.assertRaisesRegex(ValueError, "Missing macOS library"):
            stage_binaries(self.archive, self.output, self.license)
        self.assertFalse(self.output.exists())

    def test_archive_traversal_is_rejected(self):
        self.fixture(extra="fmod/libs/iOS/libGodotFmod.ios.bad/../../../../outside")
        with self.assertRaisesRegex(ValueError, "Unsafe archive member"):
            stage_binaries(self.archive, self.output, self.license)
        self.assertFalse(self.output.exists())
        self.assertFalse((self.root / "outside").exists())

    def test_existing_output_is_not_overwritten(self):
        self.fixture()
        self.output.mkdir()
        (self.output / "keep").write_text("unchanged")
        with self.assertRaisesRegex(ValueError, "new directory"):
            stage_binaries(self.archive, self.output, self.license)
        self.assertEqual((self.output / "keep").read_text(), "unchanged")


if __name__ == "__main__":
    unittest.main()
