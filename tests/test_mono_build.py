import hashlib
import json
import struct
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools/mono"))

import build_mono  # noqa: E402
import check_mono_build  # noqa: E402
import godot_pack  # noqa: E402
import mono_library as library  # noqa: E402
import pinvoke_table  # noqa: E402
import setup_ios as setup  # noqa: E402

CODE_SIZE = 0x4000
BL_UNRELATED = 0x94000000


def bl(address, target):
    return library.encode_bl(address, target)


def code_fixture():
    """A code image with the patch sites, and its nm output."""
    addresses = {
        "_key_from_id": 0x100,
        library.CLASS_INIT: 0x200,
        library.CLASS_INIT_SHIM: 0x300,
        "_mono_jit_runtime_invoke": 0x1000,
        "_mono_interp_transform_method": 0x2000,
        "_after": 0x3000,
    }
    data = bytearray(CODE_SIZE)
    struct.pack_into("<I", data, 0x100 + library.KEY_FROM_ID_SITE, library.KEY_FROM_ID_ORIGINAL)
    for function, offsets in library.CLASS_INIT_SITES.items():
        for offset in offsets:
            site = addresses[function] + offset
            struct.pack_into("<I", data, site, bl(site, addresses[library.CLASS_INIT]))
    nm = "\n".join(f"{address:016x} t {name}" for name, address in addresses.items())
    return data, library.Symbols(nm), addresses


class RemovedAssemblyTests(unittest.TestCase):
    def test_what_is_only_named_in_strings_or_needed_stays(self):
        # protobuf-net.Core asks for protobuf-net by name. The patcher needs Mono.Cecil, and not its symbol readers.
        removed = check_mono_build.REMOVED_ASSEMBLIES
        self.assertNotIn("protobuf-net", removed)
        self.assertNotIn("Mono.Cecil", removed)
        self.assertIn("Mono.Cecil.Mdb", removed)
        self.assertIn("Mono.Cecil.Pdb", removed)

    def test_the_publish_gets_the_list_as_one_property(self):
        # A plain semicolon would end the -p: value on the command line.
        value = next(item for item in build_mono.PUBLISH_PROPERTIES if item.startswith("MonoRemovedAssemblies="))
        self.assertEqual(value.split("=", 1)[1].split("%3B"), list(check_mono_build.REMOVED_ASSEMBLIES))
        self.assertNotIn(";", value)


class BinaryPatchTests(unittest.TestCase):
    def test_key_from_id_rewrites_only_the_null_select(self):
        data, symbols, addresses = code_fixture()
        original = bytes(data)
        site = library.patch_key_from_id(data, symbols)
        self.assertEqual(site, addresses["_key_from_id"] + library.KEY_FROM_ID_SITE)
        self.assertEqual(struct.unpack_from("<I", data, site)[0], library.KEY_FROM_ID_PATCHED)
        self.assertEqual(data[:site] + data[site + 4 :], original[:site] + original[site + 4 :])

    def test_key_from_id_refuses_another_instruction(self):
        data, symbols, addresses = code_fixture()
        struct.pack_into("<I", data, addresses["_key_from_id"] + library.KEY_FROM_ID_SITE, 0xD503201F)
        with self.assertRaisesRegex(library.MonoBuildError, "Expected"):
            library.patch_key_from_id(data, symbols)

    def test_class_init_calls_move_to_the_shim(self):
        data, symbols, _ = code_fixture()
        self.assertEqual(len(library.patch_class_init(data, symbols)), 3)
        for function, offsets in library.CLASS_INIT_SITES.items():
            called = library.functions_called(data, symbols, function)
            self.assertEqual(called.count(library.CLASS_INIT_SHIM), len(offsets))
            self.assertNotIn(library.CLASS_INIT, called)

    def test_class_init_refuses_a_different_shape(self):
        data, symbols, addresses = code_fixture()
        extra = addresses["_mono_interp_transform_method"] + 0x10
        struct.pack_into("<I", data, extra, bl(extra, addresses[library.CLASS_INIT]))
        with self.assertRaisesRegex(library.MonoBuildError, "class-init calls"):
            library.patch_class_init(data, symbols)

    def test_branch_decoding_handles_backward_calls(self):
        data = bytearray(16)
        struct.pack_into("<I", data, 8, bl(8, 0))
        self.assertEqual(library.branch_calls(data, 0, 16), [(8, 0)])

    def test_bl_range_is_enforced(self):
        with self.assertRaisesRegex(library.MonoBuildError, "out of bl range"):
            bl(0, 0x10000000)

    def test_island_names_are_the_called_function(self):
        symbols = library.Symbols("0000000000000010 t _f.island\n0000000000000020 t _g")
        self.assertEqual(symbols.target_name(0x10), "_f")

    def test_data_and_code_symbols_are_kept_apart(self):
        symbols = library.Symbols("0000000000000010 t _same\n0000000000000020 b _same\n                 U _undefined")
        self.assertEqual(symbols.code, {"_same": 0x10})
        self.assertEqual(symbols.data, {"_same": 0x20})


class MachOTests(unittest.TestCase):
    def image(self, vmaddr=0, pointers=(0x1234,)):
        section = b"__mod_init_func".ljust(16, b"\0") + b"__DATA".ljust(16, b"\0")
        section += struct.pack("<2Q", 0x8000, len(pointers) * 8) + struct.pack("<I", 0x400) + bytes(28)
        text = struct.pack("<2I", library.LC_SEGMENT_64, 72) + b"__TEXT".ljust(16, b"\0")
        text += struct.pack("<4Q", vmaddr, 0x1000, 0, 0x1000) + bytes(16)
        extra = struct.pack("<2I", library.LC_SEGMENT_64, 72 + 80) + b"__DATA".ljust(16, b"\0")
        extra += struct.pack("<4Q", 0x8000, 0x1000, 0x400, 0x1000) + bytes(8) + struct.pack("<2I", 1, 0) + section
        header = struct.pack("<8I", library.MH_MAGIC_64, 0, 0, 0, 2, len(text) + len(extra), 0, 0)
        data = bytearray((header + text + extra).ljust(0x400, b"\0"))
        for pointer in pointers:
            data += struct.pack("<Q", pointer)
        return data

    def test_reads_the_functions_run_at_load(self):
        self.assertEqual(library.init_functions(self.image()), {0x1234})

    def test_a_fixup_in_the_high_bits_is_ignored(self):
        self.assertEqual(library.init_functions(self.image(pointers=((1 << 51) | 0x1234,))), {0x1234})

    def test_requires_code_at_file_offsets(self):
        library.check_code_at_file_offsets(self.image())
        with self.assertRaisesRegex(library.MonoBuildError, "__TEXT"):
            library.check_code_at_file_offsets(self.image(vmaddr=0x1000))

    def test_refuses_a_fat_or_foreign_file(self):
        with self.assertRaisesRegex(library.MonoBuildError, "Mach-O"):
            library.segments(bytes(64))


class SourceEditTests(unittest.TestCase):
    def test_startup_mode_edit_is_exact_and_repeatable(self):
        edited = library.edit_autoinit(f"a\n    {library.AOT_ONLY_CALL}\nb\n    {library.RUNTIME_READY}\n")
        self.assertIn(library.INTERP_MODE_CALL, edited)
        self.assertNotIn(library.AOT_ONLY_CALL, edited)
        self.assertLess(edited.index(library.FINALIZER_THREAD_CALL), edited.index(library.RUNTIME_READY))
        self.assertEqual(library.edit_autoinit(edited), edited)

    def test_finalizer_thread_is_added_to_an_already_edited_file(self):
        edited = library.edit_autoinit(f"a\n    {library.INTERP_MODE_CALL}\n    {library.RUNTIME_READY}\n")
        self.assertEqual(edited.count(library.FINALIZER_THREAD_CALL), 1)

    def test_startup_edit_refuses_a_missing_runtime_ready_line(self):
        with self.assertRaisesRegex(library.MonoBuildError, library.RUNTIME_READY.split(" ")[0]):
            library.edit_autoinit(f"a\n    {library.AOT_ONLY_CALL}\n")

    def test_startup_mode_edit_refuses_a_missing_call(self):
        with self.assertRaisesRegex(library.MonoBuildError, "exactly one"):
            library.edit_autoinit("nothing here")

    def test_alignment_edit_is_exact_and_repeatable(self):
        source = "#include <stdint.h>\nuint8_t bundled_resource_Ab_9_data[] = {\n1,2};\n"
        edited = library.align_bundled_resource(source)
        self.assertIn("uint8_t bundled_resource_Ab_9_data[] __attribute__((aligned(16))) = {", edited)
        self.assertEqual(library.align_bundled_resource(edited), edited)

    def test_mount_settings_put_the_mount_before_every_other_autoload(self):
        host = godot_pack.write_settings([("application/run/main_scene", b"a"), ("_custom_features", b"b"), ("autoload/Other", b"c")])
        settings = godot_pack.read_settings(library.game_mount_settings(host))
        keys = [key for key, _ in settings]
        self.assertEqual(keys[0], "_custom_features")
        self.assertLess(keys.index("autoload/GameMount"), keys.index("autoload/Other"))
        self.assertIn(("application/run/main_scene", b"a"), settings)
        self.assertIn(("autoload/GameMount", library.variant_string("res://GameMount.cs")), settings)

    def test_mount_settings_refuse_a_host_that_already_names_them(self):
        host = godot_pack.write_settings([("autoload/GameMount", b"a")])
        with self.assertRaisesRegex(library.MonoBuildError, "already"):
            library.game_mount_settings(host)

    def test_alignment_edit_refuses_a_file_without_an_array(self):
        with self.assertRaisesRegex(library.MonoBuildError, "my.c"):
            library.align_bundled_resource("int x;\n", "my.c")


class LinkCommandTests(unittest.TestCase):
    LOG = """
      other output
      _BuildNativeLibrary:
        Running: xcrun clang -dynamiclib -o lib.dylib -Wl,-headerpad_max_install_names /a/libmonosgen-2.0.a "/b c/libSystem.Native.a" x.o
        Using working directory: /work/Bundle/
        Exit code: 0
        Running: xcrun install_name_tool -id @rpath/lib.dylib lib.dylib
    """

    def test_finds_the_commands(self):
        link, directory, name_id = library.parse_link_commands(self.LOG)
        self.assertTrue(link.startswith("xcrun clang -dynamiclib"))
        self.assertEqual(directory, Path("/work/Bundle"))
        self.assertEqual(name_id, "xcrun install_name_tool -id @rpath/lib.dylib lib.dylib")

    def test_refuses_a_log_without_the_step(self):
        with self.assertRaisesRegex(library.MonoBuildError, "_BuildNativeLibrary"):
            library.parse_link_commands("Build succeeded.")

    def test_lists_archives_with_spaces(self):
        link, _, _ = library.parse_link_commands(self.LOG)
        self.assertEqual(library.linked_archives(link), [Path("/a/libmonosgen-2.0.a"), Path("/b c/libSystem.Native.a")])

    def test_extra_sources_go_before_the_archives(self):
        link, _, _ = library.parse_link_commands(self.LOG)
        edited = library.add_link_sources(link, [Path("/x y/shim.c")])
        self.assertLess(edited.index("'/x y/shim.c'"), edited.index("/a/libmonosgen"))

    def test_extra_sources_need_the_anchor(self):
        with self.assertRaises(library.MonoBuildError):
            library.add_link_sources("xcrun clang x.o", [Path("a.c")])

    LINK = (
        "xcrun clang -dynamiclib -o lib.dylib -D HOST=1 -DX -mios-version-min=12.2 -isysroot /sdk -arch arm64 -I /inc/ "
        "-framework UIKit -lz -L/usr/lib/swift -exported_symbols_list syms.txt -Wl,-headerpad_max_install_names "
        "'/x y/shim.c' /a/Core.dll.s modules.m -force_load /a/libmono.a bundled_resource_a.c"
    )

    def test_split_keeps_each_source_in_its_place(self):
        split = library.split_link(self.LINK)
        self.assertEqual(
            split.compile_flags, ["-D", "HOST=1", "-DX", "-mios-version-min=12.2", "-isysroot", "/sdk", "-arch", "arm64", "-I", "/inc/"]
        )
        self.assertEqual([split.arguments[i] for i in split.sources], ["/x y/shim.c", "/a/Core.dll.s", "modules.m", "bundled_resource_a.c"])
        objects = {index: Path(f"/o/{index}.o") for index in split.sources}
        linked = split.object_link(objects)
        self.assertEqual(linked[:2], ["xcrun", "clang"])
        self.assertEqual(len(linked), len(split.arguments) + 2)
        self.assertLess(linked.index(str(objects[split.sources[0]])), linked.index("/a/libmono.a"))
        self.assertNotIn("modules.m", linked)
        self.assertEqual(linked[-1], str(objects[split.sources[-1]]))

    def test_split_compiles_with_the_compile_flags_only(self):
        split = library.split_link(self.LINK)
        command = split.compile_command(Path("modules.m"), Path("o.o"))
        self.assertEqual(command[:3], ["xcrun", "clang", "-c"])
        self.assertNotIn("-dynamiclib", command)
        self.assertNotIn("-framework", command)
        self.assertEqual(command[-3:], ["modules.m", "-o", "o.o"])

    def test_split_refuses_an_unknown_flag(self):
        with self.assertRaisesRegex(library.MonoBuildError, "-fobjc-arc"):
            library.split_link(self.LINK + " -fobjc-arc")

    def test_split_needs_every_object(self):
        split = library.split_link(self.LINK)
        with self.assertRaises(library.MonoBuildError):
            split.object_link({split.sources[0]: Path("a.o")})


class PInvokeTableTests(unittest.TestCase):
    DECLARED = [
        "libSystem.Native\tSystemNative_Write",
        "libSystem.Native\tSystemNative_GetTimestamp",
        "libSystem.Native\tSystemNative_Missing",
        "libOther\tOther_Call",
    ]

    def test_keeps_only_entries_the_archives_define_in_strcmp_order(self):
        entries = pinvoke_table.table_entries(
            self.DECLARED, {"libSystem.Native"}, {"SystemNative_Write", "SystemNative_GetTimestamp", "Other_Call"}
        )
        self.assertEqual(
            entries, [("libSystem.Native", "SystemNative_GetTimestamp"), ("libSystem.Native", "SystemNative_Write")]
        )

    def test_generated_source_compiles_and_finds_every_entry(self):
        entries = [("libA", "fn_b"), ("libA", "fn_c"), ("libZ", "fn_a")]
        harness = """
#include <stdio.h>
void fn_a (void) {} void fn_b (void) {} void fn_c (void) {}
static void *(*installed) (const char *, const char *);
void mono_loader_install_pinvoke_override (void *(*f) (const char *, const char *)) { installed = f; }
int main (void) {
    if (!installed) return 1;
    if (installed ("libA", "fn_c") != (void *)fn_c || installed ("libZ", "fn_a") != (void *)fn_a) return 2;
    if (installed ("libA", "fn_a") || installed ("libQ", "fn_b") || installed (NULL, "fn_b")) return 3;
    return 0;
}
"""
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder)
            (path / "t.c").write_text(pinvoke_table.render(entries) + harness)
            subprocess.run(["clang", "-w", "-o", str(path / "t"), str(path / "t.c")], check=True)
            self.assertEqual(subprocess.run([str(path / "t")]).returncode, 0)


class LibraryBuilderPackTests(unittest.TestCase):
    ORIGINAL = "<_AOTMode Condition=\"'$(UseMonoJustInterp)' != 'true'\">Full</_AOTMode>\n"
    EDITED = ORIGINAL.replace(">Full<", ">FullInterp<")

    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.pins = {
            "input_sha256": hashlib.sha256(self.ORIGINAL.encode()).hexdigest(),
            "output_sha256": hashlib.sha256(self.EDITED.encode()).hexdigest(),
        }

    def spec(self):
        return {
            "mono": {"runtime_version": "1.2.3", "apple_build_targets": self.pins},
            "downloads": {"library-builder": {"url": "https://example.invalid/a.nupkg", "sha512": "x"}},
        }

    def archive(self, members=None):
        path = self.root / "pack.nupkg"
        with zipfile.ZipFile(path, "w") as bundle:
            for name, text in (members or {"Sdk/AppleBuild.targets": self.ORIGINAL, "Sdk/Sdk.props": "<Project />"}).items():
                bundle.writestr(name, text)
        return path

    def test_installs_and_edits_the_pack(self):
        with patch.object(setup, "download", return_value=self.archive()):
            pack = setup.provision_library_builder(self.spec(), self.root / "packs")
        self.assertEqual(pack, self.root / "packs" / setup.LIBRARY_BUILDER / "1.2.3")
        self.assertEqual((pack / "Sdk/AppleBuild.targets").read_text(), self.EDITED)
        self.assertTrue((pack / "Sdk/Sdk.props").is_file())

    def test_an_installed_pack_is_not_downloaded_again(self):
        with patch.object(setup, "download", return_value=self.archive()):
            setup.provision_library_builder(self.spec(), self.root / "packs")
        with patch.object(setup, "download", side_effect=AssertionError("downloaded")):
            setup.provision_library_builder(self.spec(), self.root / "packs")

    def test_a_changed_installed_pack_stops_setup(self):
        with patch.object(setup, "download", return_value=self.archive()):
            pack = setup.provision_library_builder(self.spec(), self.root / "packs")
        (pack / "Sdk/AppleBuild.targets").write_text("changed")
        with self.assertRaisesRegex(ValueError, "changed"):
            setup.provision_library_builder(self.spec(), self.root / "packs")

    def test_a_different_targets_file_is_refused_and_nothing_is_installed(self):
        archive = self.archive({"Sdk/AppleBuild.targets": "another version"})
        with patch.object(setup, "download", return_value=archive), self.assertRaisesRegex(ValueError, "Unexpected"):
            setup.provision_library_builder(self.spec(), self.root / "packs")
        self.assertFalse((self.root / "packs" / setup.LIBRARY_BUILDER / "1.2.3").exists())

    def test_an_edit_that_gives_another_result_is_refused(self):
        self.pins["output_sha256"] = "0" * 64
        with patch.object(setup, "download", return_value=self.archive()), self.assertRaisesRegex(ValueError, "unexpected"):
            setup.provision_library_builder(self.spec(), self.root / "packs")

    def test_unsafe_archive_member_is_refused(self):
        archive = self.archive({"../escape": "x"})
        with patch.object(setup, "download", return_value=archive), self.assertRaisesRegex(ValueError, "Unsafe"):
            setup.provision_library_builder(self.spec(), self.root / "packs")

    def test_the_manifest_pins_the_pack(self):
        spec = json.loads(setup.MANIFEST.read_text())
        self.assertIn(spec["mono"]["runtime_version"], spec["downloads"]["library-builder"]["url"])
        self.assertIn("sha512", spec["downloads"]["library-builder"])


if __name__ == "__main__":
    unittest.main()
