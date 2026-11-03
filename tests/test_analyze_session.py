import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools" / "mono"))
import analyze_session


def sample(ms, fps=60, script=10.0, managed=50, worst=17, footprint=900):
    return (
        f"[stats] ms={ms} fps={fps} process_ms={script} static_mb=0 managed_mb={managed} worst_frame_ms={worst} "
        f"footprint_mb={footprint} peak_mb=1000 vram_mb=300 gc0=5 gc1=1 thermal=1 at=10:00:00"
    )


class AnalyzeSessionTests(unittest.TestCase):
    def test_uses_the_last_launch(self):
        marks, samples, _ = analyze_session.parse([
            "[stats] launcher-ready ms=1", sample(1000), "[stats] launcher-ready ms=2", sample(2000),
        ])
        self.assertEqual(marks, {"launcher-ready": 2})
        self.assertEqual([s["ms"] for s in samples], [2000])

    def test_phases_come_from_the_games_lines(self):
        _, samples, _ = analyze_session.parse([
            "[stats] launcher-ready ms=1",
            sample(1000),
            "[stats] main-menu ms=1500",
            sample(2000),
            "[INFO] Continuing run with character: CHARACTER.IRONCLAD",
            sample(3000),
            "[INFO] Preloading 'Merchant Room' assets... count=2 vfx=0",
            sample(4000),
            "[INFO] Player vote changed for 1: ->MapVote (gen: 1 coord: (2, 3))",
            sample(5000),
            "[INFO] Preloading 'Combat Room' assets... count=10 vfx=0",
            sample(6000),
            "[INFO] Creating NCombatRoom with mode=ActiveCombat encounter=THIEVING_HOPPER_WEAK.",
            sample(7000),
        ])
        self.assertEqual(
            [s["phase"] for s in samples],
            ["startup", "menu", "continue", "merchant", "map", "map", "fight THIEVING_HOPPER_WEAK"],
        )

    def test_a_hitch_names_what_came_before_it(self):
        _, _, hitches = analyze_session.parse([
            "[stats] launcher-ready ms=1",
            "[stats] main-menu ms=2",
            "[INFO] Creating NCombatRoom with mode=ActiveCombat encounter=NIBBITS.",
            "[INFO] Player 1 playing card TREMBLE (targeting Nibbit (index 1))",
            "[stats] hitch ms=5000 frame_ms=640",
        ])
        self.assertEqual(hitches, [{"ms": 5000, "frame_ms": 640, "phase": "fight NIBBITS", "after": "TREMBLE", "returned": False}])

    def test_a_frame_on_coming_back_to_the_app_is_not_counted(self):
        text = "\n".join(analyze_session.report(*analyze_session.parse([
            "[stats] launcher-ready ms=1",
            "[stats] main-menu ms=2",
            sample(1000),
            "[INFO] MuteInBackground: FocusIn received [tween=True, msSinceFocusOut=1101, windowFocused=True]",
            "[stats] hitch ms=5000 frame_ms=1144",
            "[stats] hitch ms=6000 frame_ms=120",
        ])))
        self.assertIn("frames of 100 ms or more: 1, not counting 1 on coming back to the app", text)
        self.assertNotIn("1144 ms", text)

    def test_report_gives_fights_memory_and_the_worst_frames(self):
        text = "\n".join(analyze_session.report(*analyze_session.parse([
            "[stats] launcher-ready ms=1",
            "[stats] game-start ms=1000",
            "[stats] main-menu ms=7000",
            "[INFO] Creating NCombatRoom with mode=ActiveCombat encounter=NIBBITS.",
            sample(8000, fps=50, script=40.0, worst=300),
            sample(9000, fps=60, script=20.0),
            "[stats] hitch ms=9500 frame_ms=300",
        ])))
        self.assertIn("game start to main menu: 6.0 s", text)
        self.assertIn("all fights: 2 s, fps med 55 min 50", text)
        self.assertIn("worst frame 300 ms", text)
        self.assertIn("in play: managed heap max 50 MB, footprint max 900 MB", text)
        self.assertIn("300 ms at 10 s in fight NIBBITS", text)

    def test_older_lines_tagged_proof_without_the_later_fields_still_parse(self):
        _, samples, _ = analyze_session.parse([
            "[proof] launcher-ready ms=1",
            "[proof] ms=3000 fps=60 process_ms=17.1 static_mb=0 managed_mb=67",
        ])
        self.assertEqual(samples[0]["managed_mb"], 67)
        self.assertEqual(samples[0]["thermal"], -1)
        self.assertEqual(samples[0]["battery"], -1)
        self.assertEqual(analyze_session.battery_lines(samples), [])

    def test_report_gives_battery_charging_and_heat(self):
        def line(ms, battery, power, thermal):
            return (
                f"[stats] ms={ms} fps=60 process_ms=10.0 static_mb=0 managed_mb=50 thermal={thermal} "
                f"battery={battery} power={power} at=10:00:00"
            )

        text = "\n".join(analyze_session.report(*analyze_session.parse([
            "[stats] launcher-ready ms=1",
            "[stats] main-menu ms=2",
            line(60000, 90, "charging", 0),
            line(120000, 85, "charging", 1),
            line(240000, 85, "unplugged", 2),
        ])))
        self.assertIn("battery: 90% to 85% over 3.0 minutes, charging then unplugged", text)
        self.assertIn("battery steps seen: 5 points", text)
        self.assertIn("thermal state: 1 from 2.0 minutes, 2 from 4.0 minutes", text)

    def test_report_gives_the_apps_usage_per_second_of_play(self):
        def line(ms, cpu, energy):
            return f"[stats] ms={ms} fps=60 process_ms=10.0 static_mb=0 managed_mb=50 cpu_s={cpu} energy_j={energy} at=10:00:00"

        text = "\n".join(analyze_session.report(*analyze_session.parse([
            "[stats] launcher-ready ms=1",
            line(1000, 500.0, 900.0),
            "[stats] main-menu ms=1500",
            line(2000, 10.0, 20.0),
            line(12000, 25.0, 50.0),
        ])))
        self.assertIn("app's usage over 10 s: cpu 1.50 cores, energy 3.00 W, not counting the GPU or the screen", text)

    def test_a_simulator_log_has_no_battery_line(self):
        text = "\n".join(analyze_session.report(*analyze_session.parse([
            "[stats] launcher-ready ms=1",
            "[stats] ms=1000 fps=60 process_ms=10.0 static_mb=0 managed_mb=50 thermal=0 battery=-1 power=unknown at=10:00:00",
        ])))
        self.assertNotIn("battery:", text)


if __name__ == "__main__":
    unittest.main()
