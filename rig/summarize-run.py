"""Print 1-2 chat lines summarizing the newest validation run, read from the
CLI's machine-readable JSON report (data/validation/*.json)."""
import glob
import json
import os
import sys

import calibipc

reports = sorted(
    glob.glob(str(calibipc.CALIB_DIR.parents[0] / "validation" / "*.json")),
    key=os.path.getmtime,
)
if not reports:
    print("test finished but no report was written - check rig.log")
    sys.exit(1)

with open(reports[-1]) as f:
    report = json.load(f)
s = report["summary"]

line1 = (f"test done: {s['lineups']} lineups, {s['submitted']} thrown, {s['matched']} captured"
         + (f", {s['notDetonated']} FAILED TO DETONATE" if s.get("notDetonated") else ""))
line2 = (f"within {s['passRadius']:g}u: {s['withinPass']}/{s['matched']}"
         f" ({100 * s['withinPass'] / max(1, s['matched']):.0f}%)"
         f" - median {s['errMedian']:.1f}u, p90 {s['errP90']:.1f}u, worst {s['errMax']:.1f}u")
print(line1)
print(line2)
