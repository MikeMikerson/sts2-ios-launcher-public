import json
import plistlib
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

import make_altstore_source as source

INFO = {
    "CFBundleIdentifier": "io.github.example.sts2mobile",
    "CFBundleShortVersionString": "0.2.1",
    "CFBundleVersion": "17",
    "MinimumOSVersion": "15.0",
    "NSMicrophoneUsageDescription": "Voice chat.",
    "UIFileSharingEnabled": False,
}


class AltStoreSourceTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def ipa(self, info=INFO, apps=("FullGame",)):
        path = self.root / "app.ipa"
        with zipfile.ZipFile(path, "w") as archive:
            for app in apps:
                archive.writestr(f"Payload/{app}.app/Info.plist", plistlib.dumps(info))
        return path

    def test_source_describes_the_ipa(self):
        info = source.read_info(self.ipa())
        result = source.make_source(
            info, size=123, download_url="https://d/app.ipa", icon_url="https://d/icon.png",
            website="https://w", date="2026-11-07", notes="First release.",
        )
        app = result["apps"][0]
        self.assertEqual(app["bundleIdentifier"], "io.github.example.sts2mobile")
        self.assertEqual(app["versions"][0]["version"], "0.2.1")
        self.assertEqual(app["versions"][0]["buildVersion"], "17")
        self.assertEqual(app["versions"][0]["size"], 123)
        self.assertEqual(app["versions"][0]["minOSVersion"], "15.0")
        self.assertEqual(app["appPermissions"], {"entitlements": [], "privacy": {"NSMicrophoneUsageDescription": "Voice chat."}})
        json.dumps(result)

    def test_refuses_unexpanded_settings(self):
        info = source.read_info(self.ipa(dict(INFO, CFBundleShortVersionString="$(MARKETING_VERSION)")))
        with self.assertRaises(ValueError):
            source.make_source(info, size=1, download_url="", icon_url="", website="", date="", notes="")

    def test_refuses_more_than_one_app(self):
        with self.assertRaises(ValueError):
            source.read_info(self.ipa(apps=("One", "Two")))

    def test_command_writes_the_source(self):
        output = self.root / "source.json"
        subprocess.run(
            [sys.executable, str(Path(source.__file__)), str(self.ipa()), "--download-url", "https://d/app.ipa",
             "--icon-url", "https://d/icon.png", "--website", "https://w", "--output", str(output)],
            check=True, capture_output=True,
        )
        self.assertEqual(json.loads(output.read_text())["apps"][0]["versions"][0]["downloadURL"], "https://d/app.ipa")
