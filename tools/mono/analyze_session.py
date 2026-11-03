#!/usr/bin/env python3
"""Summarize a play session from a pulled full-game-startup.log, phase by phase.

Usage: analyze_session.py <log>

The phases come from the game's own log lines, which sit between the launcher's one-a-second
[stats] samples (SessionStats.cs; older logs tag them [proof]), so nobody has to note the times during play. The log quotes
no game file's content, but it stays gitignored like every log of a run.
"""

import re
import statistics
import sys

STAT = re.compile(r"\[(?:stats|proof)\] ms=(\d+) fps=([\d.]+) process_ms=([\d.]+) static_mb=\d+ managed_mb=(\d+)(.*)")
EXTRA = re.compile(r"(\w+)=(\S+)")
MARK = re.compile(r"\[(?:stats|proof)\] (launcher-ready|game-start|main-menu) ms=(\d+)")
HITCH = re.compile(r"\[(?:stats|proof)\] hitch ms=(\d+) frame_ms=(\d+)")
ENCOUNTER = re.compile(r"Creating NCombatRoom with mode=\w+ encounter=(\w+)")
ROOM = re.compile(r"Preloading '(\w+) Room' assets")
# Coming back to the app after a notification or Control Center: the frame spans the time away.
RETURNED = re.compile(r"FocusIn received")
ACTION = re.compile(r"Player \d+ playing card (\w+)|Monster (\w+) performing move")
# The game's log lines that start a phase. Nothing here ends a fight on its own, so a fight runs
# until the map vote, the next room, or the return to the menu.
PHASES = (
    (re.compile(r"Player vote changed for \d+: ->MapVote"), "map"),
    (re.compile(r"Continuing run with character"), "continue"),
    (re.compile(r"Preloading 'MainMenuEssentials' assets"), "menu"),
)


def phase_of(line):
    """The phase a game log line starts, or None."""
    match = ENCOUNTER.search(line)
    if match:
        return f"fight {match.group(1)}"
    match = ROOM.search(line)
    if match and match.group(1) != "Combat":
        return match.group(1).lower()
    for pattern, name in PHASES:
        if pattern.search(line):
            return name
    return None


def parse(lines):
    """Reads the log's last launch into marks, samples tagged by phase, and hitches with what came just before."""
    marks, samples, hitches = {}, [], []
    phase, last_action, returned = "startup", "", False
    for line in lines:
        mark = MARK.search(line)
        if mark:
            if mark.group(1) == "launcher-ready":
                marks, samples, hitches = {}, [], []
                phase, last_action, returned = "startup", "", False
            marks[mark.group(1)] = int(mark.group(2))
            if mark.group(1) == "main-menu":
                phase = "menu"
            continue
        stat = STAT.search(line)
        if stat:
            extra = dict(EXTRA.findall(stat.group(5)))
            samples.append({
                "ms": int(stat.group(1)),
                "fps": float(stat.group(2)),
                "process_ms": float(stat.group(3)),
                "managed_mb": int(stat.group(4)),
                "worst_frame_ms": int(extra.get("worst_frame_ms", 0)),
                "footprint_mb": int(extra.get("footprint_mb", 0)),
                "peak_mb": int(extra.get("peak_mb", 0)),
                "vram_mb": int(extra.get("vram_mb", 0)),
                "malloc_mb": int(extra.get("malloc_mb", 0)),
                "objects": int(extra.get("objects", 0)),
                "resources": int(extra.get("resources", 0)),
                "nodes": int(extra.get("nodes", 0)),
                "orphans": int(extra.get("orphans", 0)),
                "gc1": int(extra.get("gc1", 0)),
                "thermal": int(extra.get("thermal", -1)),
                "battery": int(extra.get("battery", -1)),
                "power": extra.get("power", "unknown"),
                "cpu_s": float(extra.get("cpu_s", -1)),
                "energy_j": float(extra.get("energy_j", -1)),
                "at": extra.get("at", ""),
                "phase": phase,
            })
            continue
        hitch = HITCH.search(line)
        if hitch:
            hitches.append({"ms": int(hitch.group(1)), "frame_ms": int(hitch.group(2)), "phase": phase, "after": last_action, "returned": returned})
            returned = False
            continue
        if RETURNED.search(line):
            returned = True
        started = phase_of(line)
        if started:
            phase = started
        action = ACTION.search(line)
        if action:
            last_action = action.group(1) or action.group(2)
    return marks, samples, hitches


def percentile(values, fraction):
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(len(ordered) * fraction))]


def summary(name, rows):
    """One line for a group of samples."""
    fps = [r["fps"] for r in rows]
    process = [r["process_ms"] for r in rows]
    return (
        f"{name}: {len(rows)} s, fps med {statistics.median(fps):.0f} min {min(fps):.0f}, "
        f"script ms med {statistics.median(process):.0f} p95 {percentile(process, 0.95):.0f} max {max(process):.0f}, "
        f"worst frame {max(r['worst_frame_ms'] for r in rows)} ms"
    )


def battery_lines(samples):
    """Battery level and charging state over the log, and when each thermal state began. Empty where the log has neither."""
    lines = []
    known = [s for s in samples if s["battery"] >= 0]
    if known:
        first, last = known[0], known[-1]
        minutes = (last["ms"] - first["ms"]) / 60000
        states = []
        for sample in known:
            if not states or states[-1] != sample["power"]:
                states.append(sample["power"])
        lines.append(f"battery: {first['battery']}% to {last['battery']}% over {minutes:.1f} minutes, {' then '.join(states)}")
        steps = sorted({abs(a["battery"] - b["battery"]) for a, b in zip(known, known[1:])} - {0})
        if steps:
            lines.append(f"battery steps seen: {', '.join(str(step) for step in steps)} points")
    heat = []
    for state in sorted({s["thermal"] for s in samples} - {-1, 0}):
        start = next(s for s in samples if s["thermal"] == state)
        heat.append(f"{state} from {start['ms'] / 60000:.1f} minutes")
    if heat:
        lines.append("thermal state: " + ", ".join(heat))
    return lines


def usage_line(rows):
    """What iOS counted against the app across these samples, per second of the log. None where the log lacks it."""
    if len(rows) < 2 or rows[0]["cpu_s"] < 0 or rows[-1]["ms"] == rows[0]["ms"]:
        return None
    seconds = (rows[-1]["ms"] - rows[0]["ms"]) / 1000
    cpu = (rows[-1]["cpu_s"] - rows[0]["cpu_s"]) / seconds
    watts = (rows[-1]["energy_j"] - rows[0]["energy_j"]) / seconds
    return f"app's usage over {seconds:.0f} s: cpu {cpu:.2f} cores, energy {watts:.2f} W, not counting the GPU or the screen"


def report(marks, samples, hitches):
    """The whole report, as lines."""
    lines = [f"marks: {marks}"]
    if "game-start" in marks and "main-menu" in marks:
        lines.append(f"game start to main menu: {(marks['main-menu'] - marks['game-start']) / 1000:.1f} s")
    if not samples:
        return lines + ["no samples"]
    played = [s for s in samples if s["phase"] != "startup"]
    if played:
        lines.append(f"session: {(played[-1]['ms'] - played[0]['ms']) / 60000:.1f} minutes, {played[0]['at']} to {played[-1]['at']}")
    fights = [s for s in played if s["phase"].startswith("fight ")]
    if fights:
        lines.append(summary("all fights", fights))
    order = []
    for sample in played:
        if not order or order[-1][0] != sample["phase"]:
            order.append((sample["phase"], []))
        order[-1][1].append(sample)
    lines.extend(summary(f"  {name}", rows) for name, rows in order)
    # Preparation before the game starts allocates far more managed memory than play, so the managed peak is for play alone.
    measured = played or samples
    lines.append(
        f"in play: managed heap max {max(s['managed_mb'] for s in measured)} MB, footprint max {max(s['footprint_mb'] for s in measured)} MB, "
        f"peak {max(s['peak_mb'] for s in samples)} MB, vram max {max(s['vram_mb'] for s in measured)} MB, "
        f"major collections {samples[-1]['gc1']}, thermal state max {max(s['thermal'] for s in samples)}"
    )
    lines.extend(battery_lines(samples))
    usage = usage_line(played)
    if usage:
        lines.append(usage)
    if len(played) >= 20:
        # Later against earlier play shows a leak that a peak does not.
        tenth = len(played) // 10
        first, last = played[:tenth], played[-tenth:]
        fields = ("footprint_mb", "managed_mb", "vram_mb", "malloc_mb", "objects", "resources", "nodes", "orphans")
        lines.append("first tenth to last tenth: " + ", ".join(
            f"{field} {statistics.median(s[field] for s in first):.0f} to {statistics.median(s[field] for s in last):.0f}" for field in fields
        ))
    in_play = [h for h in hitches if h["phase"] != "startup" and not h["returned"]]
    away = sum(1 for h in hitches if h["returned"])
    lines.append(f"frames of 100 ms or more: {len(in_play)}, not counting {away} on coming back to the app, worst first:")
    for hitch in sorted(in_play, key=lambda h: -h["frame_ms"])[:15]:
        lines.append(f"  {hitch['frame_ms']} ms at {hitch['ms'] / 1000:.0f} s in {hitch['phase']}, after {hitch['after'] or '-'}")
    return lines


def main():
    with open(sys.argv[1], errors="replace") as log:
        print("\n".join(report(*parse(log))))


if __name__ == "__main__":
    main()
