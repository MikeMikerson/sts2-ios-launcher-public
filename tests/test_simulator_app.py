import struct
import sys
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
sys.path.insert(0, str(ROOT / "tools/mono"))

import simulator_app  # noqa: E402

TEXT_OFFSET = 4096


def section(name: bytes, flags: int, offset: int) -> bytes:
    return (
        name.ljust(16, b"\0")
        + b"__DATA".ljust(16, b"\0")
        + struct.pack("<2Q", 0x1000, 0x10)
        + struct.pack("<2I", offset, 3)
        + struct.pack("<2I", 0, 0)
        + struct.pack("<3I", flags, 0, 0)
        + bytes(4)
    )


def binary(*sections: bytes, size: int = TEXT_OFFSET + 16) -> bytearray:
    body = b"".join(sections)
    segment = struct.pack("<2I", simulator_app.LC_SEGMENT_64, 72 + len(body)) + b"__DATA".ljust(16, b"\0")
    segment += struct.pack("<4Q", 0, 0, 0, 0) + struct.pack("<4I", 0, 0, len(sections), 0) + body
    header = struct.pack("<8I", simulator_app.MH_MAGIC_64, 0x100000C, 0, 2, 1, len(segment), 0, 0)
    return bytearray((header + segment).ljust(size, b"\0"))


class ZeroFillOffsets(unittest.TestCase):
    def test_clears_only_zero_fill_sections(self):
        data = binary(section(b"__thread_bss", 0x12, 4000), section(b"__bss", 0x01, 0), section(b"__data", 0x00, 4096))
        self.assertEqual(simulator_app.clear_zero_fill_offsets(data), 1)
        offsets = [struct.unpack_from("<I", data, 32 + 72 + index * 80 + 48)[0] for index in range(3)]
        self.assertEqual(offsets, [0, 0, 4096])

    def test_refuses_other_files(self):
        with self.assertRaises(simulator_app.SimulatorError):
            simulator_app.clear_zero_fill_offsets(bytearray(64))


class LoadCommand(unittest.TestCase):
    def test_adds_a_padded_command_after_the_others(self):
        data = binary(section(b"__text", 0, TEXT_OFFSET))
        _, _, _, _, count, size, _, _ = struct.unpack_from("<8I", data, 0)
        simulator_app.add_load_dylib(data, "@rpath/shim.dylib")
        _, _, _, _, new_count, new_size, _, _ = struct.unpack_from("<8I", data, 0)
        self.assertEqual(new_count, count + 1)
        command, command_size, name_offset = struct.unpack_from("<3I", data, 32 + size)
        self.assertEqual((command, name_offset), (simulator_app.LC_LOAD_DYLIB, 24))
        self.assertEqual(command_size % 8, 0)
        self.assertEqual(new_size, size + command_size)
        name = bytes(data[32 + size + 24 : 32 + size + command_size]).rstrip(b"\0")
        self.assertEqual(name, b"@rpath/shim.dylib")

    def test_stops_when_the_header_has_no_room(self):
        data = binary(section(b"__text", 0, 32 + 72 + 80 + 8))
        with self.assertRaises(simulator_app.SimulatorError):
            simulator_app.add_load_dylib(data, "@rpath/shim.dylib")


def text_binary() -> bytearray:
    data = binary(section(b"__text", 0, TEXT_OFFSET))
    data[32 + 8 : 32 + 24] = b"__TEXT".ljust(16, b"\0")
    struct.pack_into("<Q", data, 32 + 24, 0x100000000)
    return data


class Entitlements(unittest.TestCase):
    def test_adds_the_first_section_with_its_bytes_before_the_code(self):
        data = text_binary()
        plist = simulator_app.simulator_entitlements("org.example.app")
        simulator_app.add_entitlements_section(data, plist)
        _, _, _, _, count, size, _, _ = struct.unpack_from("<8I", data, 0)
        self.assertEqual((count, size), (1, 72 + 2 * 80))
        self.assertEqual(struct.unpack_from("<I", data, 32 + 64)[0], 2)
        first, second = 32 + 72, 32 + 72 + 80
        self.assertEqual(bytes(data[first : first + 16]).rstrip(b"\0"), b"__entitlements")
        self.assertEqual(bytes(data[second : second + 16]).rstrip(b"\0"), b"__text")
        address, length = struct.unpack_from("<2Q", data, first + 32)
        (position,) = struct.unpack_from("<I", data, first + 48)
        self.assertEqual(address, 0x100000000 + position)
        self.assertLessEqual(position + length, TEXT_OFFSET)
        self.assertEqual(bytes(data[position : position + length]), plist)

    def test_stops_when_there_is_no_room(self):
        with self.assertRaises(simulator_app.SimulatorError):
            simulator_app.add_entitlements_section(text_binary(), bytes(TEXT_OFFSET))


if __name__ == "__main__":
    unittest.main()
