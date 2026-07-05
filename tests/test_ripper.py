import os
import tempfile
import unittest
import zipfile

import ripper


class ResolveRomPathTests(unittest.TestCase):
    def test_extracts_single_game_boy_rom_from_zip(self):
        rom_bytes = bytes([0]) * 0x150
        rom_bytes = rom_bytes[:0x134] + b"TESTROM".ljust(16, b"\0") + rom_bytes[0x144:]

        with tempfile.TemporaryDirectory() as td:
            zip_path = os.path.join(td, "game.zip")
            with zipfile.ZipFile(zip_path, "w") as zf:
                zf.writestr("readme.txt", "ignored")
                zf.writestr("Game.gb", rom_bytes)

            with ripper.resolve_rom_path(zip_path) as resolved:
                self.assertNotEqual(resolved, zip_path)
                self.assertTrue(resolved.endswith(".gb"))
                with open(resolved, "rb") as f:
                    self.assertEqual(f.read(), rom_bytes)

            self.assertFalse(os.path.exists(resolved))

    def test_rejects_zip_without_supported_rom(self):
        with tempfile.TemporaryDirectory() as td:
            zip_path = os.path.join(td, "empty.zip")
            with zipfile.ZipFile(zip_path, "w") as zf:
                zf.writestr("readme.txt", "ignored")

            with self.assertRaises(ValueError):
                with ripper.resolve_rom_path(zip_path):
                    pass


if __name__ == "__main__":
    unittest.main()
