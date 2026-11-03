import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import install_app

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools/mono"))
import build_mono  # noqa: E402

FREE = {"id": "AAAAAAAAAA", "name": "A Person (Personal Team)", "free": True}
PAID = {"id": "BBBBBBBBBB", "name": "A Person", "free": False}
PHONE = {"udid": "00000000-0000000000000000", "name": "iPhone", "model": "iPhone 11 Pro", "paired": True, "connected": True, "developer_mode": "enabled"}


class InstallAppTests(unittest.TestCase):
    def configure(self, teams, phones, saved=None, **options):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "device.json"
            if saved is not None:
                path.write_text(json.dumps(saved))
            with (
                mock.patch.object(install_app, "xcode_teams", return_value=teams),
                mock.patch.object(install_app, "iphones", return_value=phones),
                mock.patch("xcode_local.local_settings", return_value={"bundle": "org.example.sts2ios.abc", "team": ""}),
                mock.patch.object(install_app, "ROOT", Path(folder)),
            ):
                values = install_app.configure(path=path, **options)
            self.assertEqual(json.loads(path.read_text()), values)
            return values

    def test_one_team_and_one_phone_need_no_options(self):
        values = self.configure([FREE], [PHONE])
        self.assertEqual(values, {"team": FREE["id"], "device_udid": PHONE["udid"], "dev_bundle_id": "org.example.sts2ios.abc"})

    def test_two_teams_need_a_choice(self):
        with self.assertRaisesRegex(ValueError, "--team"):
            self.configure([PAID, FREE], [PHONE])
        self.assertEqual(self.configure([PAID, FREE], [PHONE], team=FREE["id"])["team"], FREE["id"])

    def test_saved_values_and_other_keys_are_kept(self):
        saved = {"team": PAID["id"], "dev_bundle_id": "org.example.kept", "device_udid": PHONE["udid"], "note": "x"}
        self.assertEqual(self.configure([PAID, FREE], [PHONE], saved=saved), saved)

    def test_a_team_not_in_xcode_is_refused(self):
        with self.assertRaisesRegex(ValueError, "not signed in to Xcode"):
            self.configure([FREE], [PHONE], team="CCCCCCCCCC")

    def test_no_apple_id_and_no_phone_say_what_to_do(self):
        with self.assertRaisesRegex(ValueError, "Apple Accounts"):
            self.configure([], [PHONE])
        with self.assertRaisesRegex(ValueError, "No iPhone is connected"):
            self.configure([FREE], [])

    def test_phone_not_ready_is_refused_before_the_build(self):
        with self.assertRaisesRegex(ValueError, "Developer Mode is off"):
            self.configure([FREE], [{**PHONE, "developer_mode": "disabled"}])
        with self.assertRaisesRegex(ValueError, "Trust"):
            self.configure([FREE], [{**PHONE, "paired": False}])

    def test_a_closed_tunnel_is_opened_before_giving_up(self):
        closed = {**PHONE, "connected": False}
        with mock.patch.object(install_app, "open_tunnel", return_value=True) as opened:
            self.assertEqual(self.configure([FREE], [closed])["device_udid"], PHONE["udid"])
        opened.assert_called_once_with(PHONE["udid"])
        with mock.patch.object(install_app, "open_tunnel", return_value=False):
            with self.assertRaisesRegex(ValueError, "paired but not reachable"):
                self.configure([FREE], [closed])

    def test_signed_build_names_the_phone_so_xcode_registers_it(self):
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / ".local").mkdir()
            device = {"team": PAID["id"], "dev_bundle_id": "org.example.kept"}
            (Path(folder) / ".local/device.json").write_text(json.dumps(device))
            with mock.patch.object(build_mono, "ROOT", Path(folder)):
                generic = build_mono.signing_arguments(False)
                (Path(folder) / ".local/device.json").write_text(json.dumps({**device, "device_udid": PHONE["udid"]}))
                named = build_mono.signing_arguments(False)
        self.assertIn("generic/platform=iOS", generic)
        self.assertIn(f"id={PHONE['udid']}", named)
        self.assertIn("-allowProvisioningDeviceRegistration", named)
        self.assertIn(f"PRODUCT_BUNDLE_IDENTIFIER={device['dev_bundle_id']}", named)


if __name__ == "__main__":
    unittest.main()
