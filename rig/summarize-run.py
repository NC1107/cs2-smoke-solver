"""Turn the newest validation report into 1-2 chat lines for the in-game
summary. Reads the run's pass radius from the report header so the chat
matches whatever bar the test was held to."""
import glob
import os
import re
import sys

reports = sorted(glob.glob("/home/npc/Documents/projects/cs2-smoke-solver/data/validation/*.md"),
                 key=os.path.getmtime)
if not reports:
    print("test finished but no report was written")
    sys.exit(0)
text = open(reports[-1]).read()

thrown = re.search(r"(\d+) lineups solved, (\d+) thrown, (\d+) captured, (\d+) failed to detonate", text)
overall = re.search(r"median ([\d.]+)u, mean [\d.]+u, p90 ([\d.]+)u, max ([\d.]+)u", text)
passre = re.search(r"pass radius ([\d.]+)u", text)
withinpass = re.search(r"Within ([\d.]+)u: (\d+)/(\d+) \((\d+)%\)", text)

lines = []
if thrown:
    solved, nthrown, captured, failed = thrown.groups()
    lines.append(f"test done: {solved} lineups, {nthrown} thrown, {captured} captured"
                 + (f", {failed} FAILED TO DETONATE" if failed != "0" else ""))
if withinpass and overall:
    radius, hit, total, pct = withinpass.groups()
    med, p90, mx = overall.groups()
    lines.append(f"within {radius}u: {hit}/{total} ({pct}%) - median {med}u, p90 {p90}u, worst {mx}u")
for line in lines:
    print(line)
