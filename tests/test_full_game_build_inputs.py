import tempfile
import unittest
from pathlib import Path

import full_game_build_inputs as inputs


class FullGameBuildInputTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def test_snapshot_ignores_build_output_and_detects_source_edits(self):
        project = self.root / "src/STS2Mobile.Platform"
        (project / "obj").mkdir(parents=True)
        (project / "obj/Generated.cs").write_text("ignored")
        (project / "JsonNodes.cs").write_text("first")
        (project / "notes.txt").write_text("ignored")
        before = inputs.snapshot(self.root)
        self.assertEqual(list(before), ["src/STS2Mobile.Platform/JsonNodes.cs"])
        (project / "JsonNodes.cs").write_text("second")
        (project / "Added.cs").write_text("new")
        self.assertEqual(
            inputs.changed_inputs(before, inputs.snapshot(self.root)),
            ["src/STS2Mobile.Platform/Added.cs", "src/STS2Mobile.Platform/JsonNodes.cs"],
        )

    def test_snapshot_leaves_out_the_mono_tools_but_not_other_tools(self):
        (self.root / "tools/mono").mkdir(parents=True)
        (self.root / "tools/mono/build_mono.py").write_text("mono only")
        (self.root / "tools/setup_ios.py").write_text("shared")
        self.assertEqual(list(inputs.snapshot(self.root)), ["tools/setup_ios.py"])


if __name__ == "__main__":
    unittest.main()
