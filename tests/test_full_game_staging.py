import struct
import tempfile
import unittest
from pathlib import Path

from stage_full_game import gdscript_identifiers, stage


class FullGameStagingTests(unittest.TestCase):
    def test_unknown_pack_cannot_create_output(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        root = Path(directory.name)
        pack = root / "input" / "unknown.pck"
        pack.parent.mkdir()
        pack.write_bytes(b"unknown-version")
        output = root / "output"
        with self.assertRaisesRegex(ValueError, "Unsupported full game pack"):
            stage(pack, output)
        self.assertFalse(output.exists())

    def test_gdscript_identifiers_are_decoded_for_network_audit(self):
        identifiers = ["HTTPRequest", "request", "animation"]
        content = bytearray(struct.pack("<IIII", len(identifiers), 0, 0, 0))
        for identifier in identifiers:
            content += struct.pack("<I", len(identifier))
            content += bytes(byte ^ 0xB6 for byte in identifier.encode("utf-32-le"))
        data = b"GDSC" + struct.pack("<II", 101, 0) + content
        self.assertEqual(gdscript_identifiers(data), identifiers)
        with self.assertRaisesRegex(ValueError, "Truncated GDScript identifier"):
            gdscript_identifiers(data[:-1])

    def test_unsupported_gdscript_version_is_rejected(self):
        data = b"GDSC" + struct.pack("<II", 100, 0) + bytes(16)
        with self.assertRaisesRegex(ValueError, "Unsupported GDScript tokenizer"):
            gdscript_identifiers(data)


if __name__ == "__main__":
    unittest.main()
