#!/usr/bin/env python3
"""Pre-solve the high-value smoke targets on every map so the first click there
is instant instead of a ~28s cold solve.

The solver is target-first (give it a landing spot, it returns every throw that
reaches it, from spawn areas and everywhere else) and caches each solve to
data/cache/ keyed by the quantized target. This just drives the live /api/lineup
for the targets players actually aim at - each map's named callouts, plus the
pro-demo landing hotspots where available - so those solves are already on disk.

It is safe to re-run: an already-cached target returns immediately and is
skipped past cheaply. The cache is keyed by mesh build, so re-warm after a map
is re-extracted.

  rig/warm-cache.py                       # all maps, against localhost:8189
  BASE=http://127.0.0.1:8189 rig/warm-cache.py de_mirage de_nuke
"""
import json
import os
import sys
import time
import urllib.request
from concurrent.futures import ThreadPoolExecutor

BASE = os.environ.get("BASE", "http://127.0.0.1:8189")
DATA = os.path.join(os.path.dirname(__file__), "..", "data")


def get(path):
    return json.load(urllib.request.urlopen(BASE + path, timeout=30))


def targets_for(map_name):
    """High-value landing targets: pro hotspots (precise, where pros smoke) plus
    every named callout centre (the spots players aim at). Deduped to the 16u
    cache grid so we don't warm the same cell twice."""
    out = []
    protargets = os.path.join(DATA, f"{map_name}.protargets.json")
    if os.path.exists(protargets):
        for t in json.load(open(protargets)).get("targets", []):
            out.append([t["x"], t["y"], t["z"]])
    try:
        vm = get(f"/data/{map_name}.viewer-map.json")
        for c in vm.get("callouts", []):
            # callouts are [name, x, y]; the solver snaps a 2D target to ground.
            out.append([c[1], c[2]])
    except Exception as e:
        print(f"  {map_name}: no callouts ({e})", file=sys.stderr)
    seen, deduped = set(), []
    for t in out:
        key = (round(t[0] / 16), round(t[1] / 16))
        if key not in seen:
            seen.add(key)
            deduped.append(t)
    return deduped


def solve(map_name, target):
    body = json.dumps({"map": map_name, "target": target}).encode()
    req = urllib.request.Request(BASE + "/api/lineup", body, {"Content-Type": "application/json"})
    t0 = time.time()
    lineups = 0
    for raw in urllib.request.urlopen(req, timeout=300):
        line = raw.decode().strip()
        if line.startswith('{"result"'):
            lineups = len(json.loads(line)["result"].get("lineups", []))
    return time.time() - t0, lineups


def main():
    maps = sys.argv[1:] or [m["map"] for m in get("/api/maps") if m["hasLineups"]]
    grand_t0 = time.time()
    total = 0
    for m in maps:
        targets = targets_for(m)
        print(f"=== {m}: warming {len(targets)} targets ===")
        done = 0
        # The server caps concurrent solves at 2 (SolveGate); match it.
        with ThreadPoolExecutor(max_workers=2) as pool:
            for target, (dt, n) in zip(targets, pool.map(lambda t: solve(m, t), targets)):
                done += 1
                total += 1
                tag = "cached" if dt < 1 else f"{dt:.0f}s"
                print(f"  [{done}/{len(targets)}] {target} -> {n} lineups ({tag})")
        print(f"=== {m} done ===")
    print(f"warmed {total} targets in {(time.time() - grand_t0) / 60:.0f} min")


if __name__ == "__main__":
    main()
