import json
import plistlib
import struct
import tempfile
import unittest
import zlib
from pathlib import Path
from unittest import mock

import prepare_full_game_xcode as xcode
from xcode_local import PHASE_ID, capture, configure_project, local_settings

PROJECT = (
    """/* Begin PBXSourcesBuildPhase section */
isa = PBXNativeTarget;
buildPhases = (
);
"""
    + """PRODUCT_BUNDLE_IDENTIFIER = org.example.game;
DEVELOPMENT_TEAM = "";
CODE_SIGN_STYLE = Manual;
CODE_SIGN_IDENTITY = "Apple Distribution";
PRODUCT_NAME = FullGame;
"""
    * 2
)


# The shape of the project Godot's iOS export writes, cut down to what the preparation reads.
EXPORTED_PROJECT = """// !$*UTF8*$!
{
/* Begin PBXBuildFile section */
\t\t1FF8DBB11FBA9DE1009DE660 /* dummy.cpp in Sources */ = {isa = PBXBuildFile; fileRef = 1FF8DBB01FBA9DE1009DE660 /* dummy.cpp */; };
\t\tD07CD44E1C5D589C00B7FB28 /* Images.xcassets in Resources */ = {isa = PBXBuildFile; fileRef = D07CD44D1C5D589C00B7FB28 /* Images.xcassets */; };
\t\t90DD2D9E24B36E8000717FE1 /* Launch Screen.storyboard in Resources */ = {isa = PBXBuildFile; fileRef = 90DD2D9D24B36E8000717FE1 /* Launch Screen.storyboard */; };
/* End PBXBuildFile section */
/* Begin PBXFileReference section */
\t\t1FF8DBB01FBA9DE1009DE660 /* dummy.cpp */ = {isa = PBXFileReference; lastKnownFileType = sourcecode.cpp.cpp; path = dummy.cpp; sourceTree = "<group>"; };
/* End PBXFileReference section */
/* Begin PBXNativeTarget section */
\t\tD0BCFE3318AEBDA2004A7AAE /* FullGame */ = {
\t\t\tisa = PBXNativeTarget;
\t\t\tbuildPhases = (
\t\t\t\tD0BCFE3018AEBDA2004A7AAE /* Sources */,
\t\t\t\tD0BCFE3218AEBDA2004A7AAE /* Resources */,
\t\t\t);
\t\t};
/* End PBXNativeTarget section */
/* Begin PBXResourcesBuildPhase section */
\t\tD0BCFE3218AEBDA2004A7AAE /* Resources */ = {
\t\t\tisa = PBXResourcesBuildPhase;
\t\t\tfiles = (
\t\t\t\tD07CD44E1C5D589C00B7FB28 /* Images.xcassets in Resources */,
\t\t\t\t90DD2D9E24B36E8000717FE1 /* Launch Screen.storyboard in Resources */,
\t\t\t);
\t\t};
/* End PBXResourcesBuildPhase section */
/* Begin PBXSourcesBuildPhase section */
\t\tD0BCFE3018AEBDA2004A7AAE /* Sources */ = {
\t\t\tisa = PBXSourcesBuildPhase;
\t\t\tfiles = (
\t\t\t\t1FF8DBB11FBA9DE1009DE660 /* dummy.cpp in Sources */,
\t\t\t);
\t\t};
/* End PBXSourcesBuildPhase section */
""" + """\t\tD0BCFE7418AEBDA3004A7AAE /* Release */ = {
\t\t\tbuildSettings = {
\t\t\t\tASSETCATALOG_COMPILER_APPICON_NAME = AppIcon;
\t\t\t\tCODE_SIGN_IDENTITY = "Apple Distribution";
\t\t\t\tCODE_SIGN_STYLE = Manual;
\t\t\t\tDEVELOPMENT_TEAM = "";
\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = org.godotengine.fullgame;
\t\t\t\tPRODUCT_NAME = FullGame;
\t\t\t};
\t\t};
""" * 2 + "}\n"  # noqa: E501


def rgba_png(size: int) -> bytes:
    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    rows = b"".join(b"\x00" + b"\x10\x20\x30\x80" * size for _ in range(size))
    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(rows))
        + chunk(b"IEND", b"")
    )


class LocalSigningTests(unittest.TestCase):
    def test_local_choices_survive_regeneration_and_phase_is_idempotent(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "signing.json"
        settings = {"bundle": "org.example.changed", "team": "ABCDEFGHIJ"}
        once = configure_project(PROJECT, settings)
        capture(once, path)
        self.assertEqual(json.loads(path.read_text()), settings)
        self.assertEqual(configure_project(once, local_settings(path)), once)
        self.assertIn("CODE_SIGN_STYLE = Automatic", once)
        self.assertNotIn("Apple Distribution", once)
        self.assertIn("check-xcode-inputs.sh", once)

    def test_new_local_identity_is_stable(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "signing.json"
        self.assertEqual(local_settings(path), local_settings(path))
        self.assertEqual(local_settings(path)["team"], "")

    def test_conflicting_configurations_are_refused(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        with self.assertRaises(ValueError):
            capture(PROJECT.replace("org.example.game", "org.example.other", 1), Path(directory.name) / "settings.json")

    def test_setting_injection_is_refused(self):
        with self.assertRaises(ValueError):
            configure_project(PROJECT, {"bundle": "org.example.bad; OTHER=1", "team": ""})


class PrepareFlowTests(unittest.TestCase):
    """The whole Xcode preparation on an exported project, as scripts/export-host.sh runs it."""

    def prepare(self, root: Path) -> str:
        export, host = root / "export", root / "host"
        (export / "FullGame.xcodeproj").mkdir(parents=True)
        (export / "FullGame.xcodeproj/project.pbxproj").write_text(EXPORTED_PROJECT)
        icons = export / "FullGame/Images.xcassets/AppIcon.appiconset"
        icons.mkdir(parents=True)
        (icons / "Icon-1024.png").write_bytes(rgba_png(4))
        (export / "FullGame/dummy.cpp").write_text("// engine unit\n")
        (export / "FullGame/FullGame-Info.plist").write_bytes(
            plistlib.dumps({"CFBundleIdentifier": "$(PRODUCT_BUNDLE_IDENTIFIER)", "NSCameraUsageDescription": "x"})
        )
        (host / "native").mkdir(parents=True)
        for name in (*xcode.NATIVE_SOURCES, *xcode.NATIVE_UNITS):
            (host / "native" / name).write_text(f"// {name}\n")
        settings = {"bundle": "org.example.fork", "team": ""}
        with (
            mock.patch.object(xcode, "EXPORT", export),
            mock.patch.object(xcode, "HOST", host),
            mock.patch.object(xcode, "VOICE_MOD", root / "QuickMatch.dll"),
            mock.patch.object(xcode, "local_settings", return_value=settings),
            mock.patch("builtins.print"),
        ):
            xcode.prepare()
        return (export / "FullGame.xcodeproj/project.pbxproj").read_text()

    def test_the_verify_phase_and_every_host_unit_are_in_the_prepared_project(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        text = self.prepare(Path(directory.name))
        phases = text[text.index("isa = PBXNativeTarget;") :]
        phases = phases[: phases.index(");")]
        self.assertIn(f"{PHASE_ID} /* Verify prepared game */,", phases)
        self.assertIn(f"{PHASE_ID} /* Verify prepared game */ = {{", text)
        self.assertIn("check-xcode-inputs.sh", text)
        sources = text[text.index("isa = PBXSourcesBuildPhase;") :]
        for name, (file_id, build_id, _) in xcode.NATIVE_UNITS.items():
            self.assertIn(f"{build_id} /* {name} in Sources */,", sources)
            self.assertIn(f"{file_id} /* {name} */ = {{isa = PBXFileReference;", text)
        self.assertIn("voice_audio.mm in Sources */,", sources)
        self.assertEqual(text.count(f"{PHASE_ID} "), 2)

    def test_the_phase_id_is_used_by_nothing_else(self):
        ids = {identifier for pair in xcode.NATIVE_UNITS.values() for identifier in pair[:2]}
        self.assertNotIn(PHASE_ID, ids)
        self.assertNotIn(PHASE_ID, EXPORTED_PROJECT)

    def test_a_project_already_using_the_id_is_refused(self):
        with self.assertRaises(ValueError):
            configure_project(PROJECT + f"{PHASE_ID} /* something else */", {"bundle": "org.example.a", "team": ""})


if __name__ == "__main__":
    unittest.main()
