import json
import os
import unittest


ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def read_num(value):
    return int(value, 16) if isinstance(value, str) else value


class ProfileTests(unittest.TestCase):
    def test_sml2_profile_targets_engine_sfx_queues(self):
        with open(os.path.join(ROOT, "profiles", "sml2.json")) as f:
            profile = json.load(f)

        self.assertEqual(profile["system"], "Game Boy")
        queues = {read_num(sfx["queue"]) for sfx in profile["sfx"]}
        self.assertEqual(queues, {0xA460, 0xA470, 0xA478})

        ids = {sfx["id"] for sfx in profile["sfx"]}
        self.assertIn("Square1_13", ids)
        self.assertIn("Wave_15", ids)
        self.assertIn("Noise_10", ids)
        self.assertNotIn("Wave_28", ids)
        self.assertNotIn("Wave_31", ids)


if __name__ == "__main__":
    unittest.main()
