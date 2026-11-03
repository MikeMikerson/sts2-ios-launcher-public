"""Exercise the native preload hook's file choice without starting Mono or the game."""
import pathlib
import shutil
import subprocess
import tempfile
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]


class GameAssemblyHookTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory()
        cls.folder = pathlib.Path(cls.temporary.name)
        header = cls.folder / "mono/metadata/assembly.h"
        header.parent.mkdir(parents=True)
        header.write_text("""
            typedef struct { const char *name; } MonoAssemblyName;
            typedef struct { int unused; } MonoAssembly;
            typedef MonoAssembly *(*Hook)(MonoAssemblyName *, char **, void *);
            static Hook installed;
            static void mono_install_assembly_preload_hook(Hook hook, void *data) { installed = hook; }
            static const char *mono_assembly_name_get_name(MonoAssemblyName *name) { return name->name; }
            static MonoAssembly *mono_assembly_open(const char *path, void *status) {
                static MonoAssembly result;
                puts(path);
                return &result;
            }
        """)
        source = cls.folder / "hook.c"
        source.write_text('''
            #include <stdlib.h>
            #include <string.h>
            static const char *test_home;
            static const char *test_mods;
            static char *test_getenv(const char *key) {
                return (char *)(strcmp(key, "HOME") == 0 ? test_home : test_mods);
            }
            #define getenv test_getenv
            #include "game_assembly_hook.c"
            int main(int argc, char **argv) {
                test_home = argv[1];
                test_mods = strcmp(argv[2], "unset") == 0 ? NULL : argv[2];
                MonoAssemblyName name = { argv[3] };
                return installed(&name, NULL, NULL) ? 0 : 2;
            }
        ''')
        cls.executable = cls.folder / "hook"
        compiler = shutil.which("clang")
        if not compiler:
            raise unittest.SkipTest("clang is needed for the native preload hook check")
        subprocess.run([compiler, "-I", str(cls.folder), "-I", str(ROOT / "tools/mono"),
                        str(source), "-o", str(cls.executable)], check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        cls.temporary.cleanup()

    def setUp(self):
        self.home = self.folder / self._testMethodName
        self.game = self.home / "Documents/game"
        self.game.mkdir(parents=True)
        (self.game / "sts2.dll").write_bytes(b"woven")

    def load(self, mode, name="sts2"):
        return subprocess.run([str(self.executable), str(self.home), mode, name],
                              capture_output=True, text=True)

    def test_off_and_default_map_plain_assembly(self):
        (self.game / "sts2.unmodded.dll").write_bytes(b"plain")
        for mode in ("unset", "0", "invalid"):
            with self.subTest(mode=mode):
                result = self.load(mode)
                self.assertEqual(result.returncode, 0)
                self.assertEqual(result.stdout.strip(), str(self.game / "sts2.unmodded.dll"))

    def test_on_maps_woven_assembly(self):
        result = self.load("1")
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout.strip(), str(self.game / "sts2.dll"))

    def test_missing_plain_never_falls_back_to_woven(self):
        result = self.load("0")
        self.assertEqual(result.returncode, 2)
        self.assertEqual(result.stdout, "")

    def test_other_game_dependencies_still_resolve(self):
        (self.game / "Dependency.dll").write_bytes(b"dependency")
        result = self.load("0", "Dependency")
        self.assertEqual(result.returncode, 0)
        self.assertEqual(result.stdout.strip(), str(self.game / "Dependency.dll"))
