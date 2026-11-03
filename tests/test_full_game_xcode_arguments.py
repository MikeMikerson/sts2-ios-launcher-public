import struct
import unittest
import zlib
from pathlib import Path

from prepare_full_game_xcode import (
    MICROPHONE_PURPOSE,
    NATIVE_UNITS,
    SCENE_DELEGATE_CLASS,
    add_build_file,
    adopt_scene_lifecycle,
    fill_purpose_strings,
    keep_exported_symbols,
    opaque_png,
    startup_arguments,
    use_app_store_launch_packaging,
)

PROJECT = """/* Begin PBXBuildFile section */
\t\tAAAA /* dummy.cpp in Sources */ = {isa = PBXBuildFile; fileRef = BBBB /* dummy.cpp */; };
/* End PBXBuildFile section */
/* Begin PBXFileReference section */
\t\tBBBB /* dummy.cpp */ = {isa = PBXFileReference; lastKnownFileType = sourcecode.cpp.cpp; path = dummy.cpp; sourceTree = "<group>"; };
/* End PBXFileReference section */
/* Begin PBXSourcesBuildPhase section */
\t\tCCCC /* Sources */ = {
\t\t\tisa = PBXSourcesBuildPhase;
\t\t\tbuildActionMask = 2147483647;
\t\t\tfiles = (
\t\t\t\tAAAA /* dummy.cpp in Sources */,
\t\t\t);
\t\t};
/* End PBXSourcesBuildPhase section */
"""  # noqa: E501


class StartupArgumentTests(unittest.TestCase):
    def test_ordinary_install_keeps_the_log_file_without_verbose_logging(self):
        self.assertEqual(startup_arguments({}), ["--log-file", "user://full-game-startup.log"])
        self.assertEqual(
            startup_arguments({"STS2_FULL_GAME_VERBOSE": "0"}), ["--log-file", "user://full-game-startup.log"]
        )

    def test_verbose_logging_is_an_explicit_choice(self):
        self.assertEqual(startup_arguments({"STS2_FULL_GAME_VERBOSE": "1"})[0], "--verbose")

    def test_an_unclear_choice_is_rejected(self):
        with self.assertRaises(ValueError):
            startup_arguments({"STS2_FULL_GAME_VERBOSE": "yes"})


class NativeUnitTests(unittest.TestCase):
    def test_the_orientation_guard_is_compiled_as_objective_cpp_in_the_app_target(self):
        text = add_build_file(PROJECT, "orientation_guard.mm", "Sources")
        file_id, build_id, _ = NATIVE_UNITS["orientation_guard.mm"]
        self.assertIn(
            f"{file_id} /* orientation_guard.mm */ = {{isa = PBXFileReference; "
            "lastKnownFileType = sourcecode.cpp.objcpp; "
            "path = FullGame/orientation_guard.mm; sourceTree = SOURCE_ROOT; }",
            text,
        )
        self.assertIn(
            f"{build_id} /* orientation_guard.mm in Sources */ = {{isa = PBXBuildFile; fileRef = {file_id}", text
        )
        sources = text[text.index("isa = PBXSourcesBuildPhase") :]
        self.assertIn(f"{build_id} /* orientation_guard.mm in Sources */,", sources)
        self.assertIn("AAAA /* dummy.cpp in Sources */,", sources)

    def test_every_native_unit_gets_its_own_ids_and_compiles_in_the_app_target(self):
        text = PROJECT
        for name in NATIVE_UNITS:
            text = add_build_file(text, name, "Sources")
        ids = [identifier for pair in NATIVE_UNITS.values() for identifier in pair[:2]]
        self.assertEqual(len(ids), len(set(ids)))
        sources = text[text.index("isa = PBXSourcesBuildPhase") :]
        self.assertIn("power_state.mm", NATIVE_UNITS)
        self.assertIn("game_files.mm", NATIVE_UNITS)
        self.assertIn("setup_overlay.mm", NATIVE_UNITS)
        self.assertIn("voice_audio.mm", NATIVE_UNITS)
        for name, (_, build_id, _) in NATIVE_UNITS.items():
            self.assertIn(f"{build_id} /* {name} in Sources */,", sources)

    def test_a_second_run_changes_nothing(self):
        once = add_build_file(PROJECT, "orientation_guard.mm", "Sources")
        self.assertEqual(add_build_file(once, "orientation_guard.mm", "Sources"), once)

    def test_a_unit_already_referenced_by_a_project_saved_in_xcode_is_not_added_again(self):
        saved = add_build_file(PROJECT, "orientation_guard.mm", "Sources").replace(
            "/* orientation_guard.mm in Sources */", "/* FullGame/orientation_guard.mm in Sources */"
        )
        self.assertEqual(add_build_file(saved, "orientation_guard.mm", "Sources"), saved)

    def test_an_unknown_project_shape_is_refused(self):
        with self.assertRaises(ValueError):
            add_build_file(PROJECT.replace("PBXSourcesBuildPhase;", "PBXOtherPhase;"), "orientation_guard.mm", "Sources")

    def test_every_unit_exists_in_the_host(self):
        native = Path(__file__).resolve().parents[1] / "src/STS2Mobile.FullGame/native"
        for name in NATIVE_UNITS:
            self.assertTrue((native / name).is_file(), name)


def png(width: int, rows: list[bytes], color: int) -> bytes:
    """Encode rows of 8-bit pixels, filtering row n with PNG filter type n % 5 so every filter is exercised."""
    step = 4 if color == 6 else 3
    encoded, previous = bytearray(), bytes(width * step)
    for number, row in enumerate(rows):
        kind = number % 5
        filtered = bytearray()
        for i, value in enumerate(row):
            left = row[i - step] if i >= step else 0
            above, corner = previous[i], previous[i - step] if i >= step else 0
            estimate = left + above - corner
            paeth = min(
                (abs(estimate - left), 0, left), (abs(estimate - above), 1, above), (abs(estimate - corner), 2, corner)
            )[2]
            predicted = (0, left, above, (left + above) >> 1, paeth)[kind]
            filtered.append((value - predicted) & 0xFF)
        encoded += bytes([kind]) + filtered
        previous = row

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body))

    return (
        b"\x89PNG\r\n\x1a\n"
        + chunk(b"IHDR", struct.pack(">IIBBBBB", width, len(rows), 8, color, 0, 0, 0))
        + chunk(b"IDAT", zlib.compress(bytes(encoded)))
        + chunk(b"IEND", b"")
    )


def decoded_rgb(data: bytes) -> tuple[int, list[bytes]]:
    width, height, depth, color, _, _, _ = struct.unpack(">IIBBBBB", data[16:29])
    assert (depth, color) == (8, 2)
    raw = zlib.decompress(data[41:-12])
    stride = width * 3
    assert all(raw[row * (stride + 1)] == 0 for row in range(height))
    return width, [raw[row * (stride + 1) + 1 : (row + 1) * (stride + 1)] for row in range(height)]


class AppIconTests(unittest.TestCase):
    def test_transparency_is_composed_over_black_through_every_filter_type(self):
        rows = [
            bytes([10 * r, 200 - r, 7 * r + 3, 255, 250, 40, 90, 0, 120, 60, 30, 128, 17, 230, 99, 51])
            for r in range(7)
        ]
        width, result = decoded_rgb(opaque_png(png(4, rows, 6)))
        self.assertEqual(width, 4)
        for row, source in zip(result, rows, strict=True):
            expected = bytearray()
            for i in range(0, 16, 4):
                expected += bytes((source[i + c] * source[i + 3] + 127) // 255 for c in range(3))
            self.assertEqual(row, bytes(expected))

    def test_an_opaque_icon_is_kept_as_it_is(self):
        icon = png(2, [bytes(range(6)), bytes(range(6, 12))], 2)
        self.assertEqual(opaque_png(icon), icon)

    def test_other_png_shapes_are_refused(self):
        icon = bytearray(png(1, [bytes(4)], 6))
        icon[24] = 16
        with self.assertRaises(ValueError):
            opaque_png(bytes(icon))
        with self.assertRaises(ValueError):
            opaque_png(b"not a png")

    def test_a_project_without_the_icon_catalog_is_refused(self):
        with self.assertRaises(ValueError):
            use_app_store_launch_packaging(PROJECT, {})


class InfoPropertyTests(unittest.TestCase):
    def test_unused_permissions_are_removed(self):
        properties = {
            "NSCameraUsageDescription": "",
            "NSPhotoLibraryUsageDescription": "Unused",
            "NSMicrophoneUsageDescription": "Unused",
            "UIRequiresFullScreen": True,
        }
        fill_purpose_strings(properties, with_voice=False)
        self.assertEqual(properties, {"UIRequiresFullScreen": True})

    def test_voice_build_says_what_the_microphone_is_for(self):
        properties = {"NSMicrophoneUsageDescription": "", "NSCameraUsageDescription": ""}
        fill_purpose_strings(properties, with_voice=True)
        self.assertEqual(properties, {"NSMicrophoneUsageDescription": MICROPHONE_PURPOSE})


class SceneLifecycleTests(unittest.TestCase):
    def test_one_application_scene_is_declared_with_the_host_delegate(self):
        properties = {"UIRequiresFullScreen": True}
        adopt_scene_lifecycle(properties)
        manifest = properties["UIApplicationSceneManifest"]
        self.assertIs(manifest["UIApplicationSupportsMultipleScenes"], False)
        [configuration] = manifest["UISceneConfigurations"]["UIWindowSceneSessionRoleApplication"]
        self.assertEqual(
            configuration,
            {"UISceneConfigurationName": "Default Configuration", "UISceneDelegateClassName": SCENE_DELEGATE_CLASS},
        )
        self.assertTrue(properties["UIRequiresFullScreen"])

    def test_a_second_run_changes_nothing(self):
        once = {}
        adopt_scene_lifecycle(once)
        twice = {key: value for key, value in once.items()}
        adopt_scene_lifecycle(twice)
        self.assertEqual(twice, once)

    def test_a_different_existing_manifest_is_refused(self):
        with self.assertRaises(ValueError):
            adopt_scene_lifecycle({"UIApplicationSceneManifest": {"UIApplicationSupportsMultipleScenes": True}})

    def test_the_declared_class_is_the_one_the_native_unit_implements(self):
        source = (Path(__file__).resolve().parents[1] / "src/STS2Mobile.FullGame/native/scene_lifecycle.mm").read_text()
        self.assertIn("scene_lifecycle.mm", NATIVE_UNITS)
        self.assertIn(f"@implementation {SCENE_DELEGATE_CLASS}\n", source)
        self.assertIn(f"@interface {SCENE_DELEGATE_CLASS} : UIResponder <UIWindowSceneDelegate>", source)


class ModImportPickerTests(unittest.TestCase):
    def test_the_managed_host_looks_up_functions_the_native_unit_exports(self):
        root = Path(__file__).resolve().parents[1]
        native = (root / "src/STS2Mobile.FullGame/native/mod_import.mm").read_text()
        managed = (root / "src/STS2Mobile.Platform/FilesPicker.cs").read_text()
        self.assertIn("mod_import.mm", NATIVE_UNITS)
        for name in ("sts2_mod_import_pick", "sts2_mod_import_poll"):
            self.assertIn(f'extern "C" __attribute__((visibility("default"))) int {name}(', native)
            self.assertIn(f'NativeExports.Find("{name}")', managed)
            self.assertIn(f"(void *)&{name}", native)


class StripTests(unittest.TestCase):
    CONFIGURATIONS = (
        "\t\t\t\tMARKETING_VERSION = 1.0.0;\n"
        "\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = io.example.game;\n"
        "\t\t\t\tPRODUCT_NAME = FullGame;\n"
    ) * 2

    def test_both_target_configurations_keep_global_symbols_once(self):
        text = keep_exported_symbols(self.CONFIGURATIONS)
        self.assertEqual(
            text.count('\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = io.example.game;\n\t\t\t\tSTRIP_STYLE = "non-global";\n'), 2
        )
        self.assertEqual(keep_exported_symbols(text), text)

    def test_a_project_with_its_own_strip_style_is_refused(self):
        with self.assertRaises(ValueError):
            keep_exported_symbols(self.CONFIGURATIONS + "\t\t\t\tSTRIP_STYLE = all;\n")


if __name__ == "__main__":
    unittest.main()
