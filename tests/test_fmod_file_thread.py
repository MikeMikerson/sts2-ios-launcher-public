import hashlib
import unittest

import patch_fmod_file_thread as patch


def library() -> bytes:
    data = bytearray(0x4C000)
    for offset, original, _ in patch.PATCHES:
        data[offset : offset + 4] = original.to_bytes(4, "little")
    return bytes(data)


class FmodFileThreadPatchTests(unittest.TestCase):
    def test_replaces_both_instructions_and_nothing_else(self):
        data = library()
        patched = patch.patch_bytes(data, hashlib.sha256(data).hexdigest())
        self.assertTrue(patch.is_patched(patched))
        self.assertFalse(patch.is_patched(data))
        changed = [i for i in range(len(data)) if data[i] != patched[i]]
        self.assertTrue(all(any(o <= i < o + 4 for o, _, _ in patch.PATCHES) for i in changed))

    def test_the_branch_goes_back_to_the_loop(self):
        offset, _, branch = patch.PATCHES[1]
        self.assertEqual(branch >> 26, 0b000101)
        delta = (branch & 0x3FFFFFF) - (1 << 26)
        self.assertEqual(offset + delta * 4, 0x4AEFC)

    def test_refuses_another_library(self):
        with self.assertRaises(ValueError):
            patch.patch_bytes(library())

    def test_refuses_unexpected_instructions(self):
        data = bytearray(library())
        data[patch.PATCHES[0][0]] ^= 1
        with self.assertRaises(ValueError):
            patch.patch_bytes(bytes(data), hashlib.sha256(bytes(data)).hexdigest())


if __name__ == "__main__":
    unittest.main()
