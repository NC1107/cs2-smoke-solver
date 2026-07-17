#!/usr/bin/env python3
"""Extract pro smoke throw origins and landing positions from CS2 demos.

Demo world coordinates are the same Hammer units the solver and meshes use, so
the output drops straight onto the map with no transform. Landings come from the
smokegrenade_detonate event (the exact bloom position); throw origins from the
first tracked position of each CSmokeGrenadeProjectile (the release point, at
the thrower's stand spot).

Usage:
  rig/parse-demo-smokes.py --map de_mirage --out data/de_mirage.prosmokes.json <demo.dem> [demo.dem ...]

Aggregates every demo whose header map matches --map into one JSON:
  { "map": "de_mirage", "demos": N, "throws": [[x,y],...], "lands": [[x,y],...] }
"""
import argparse
import json
import sys

from demoparser2 import DemoParser


def extract(path):
    """Return (throws, lands) as lists of [x, y] ints, or None if the parse fails."""
    p = DemoParser(path)
    grenades = p.parse_grenades()
    proj = grenades[grenades["grenade_type"] == "CSmokeGrenadeProjectile"].dropna(subset=["x", "y"])
    # First tracked tick of each thrown projectile is the release point.
    firsts = proj.sort_values("tick").groupby("grenade_entity_id").first()
    throws = [[round(x), round(y)] for x, y in firsts[["x", "y"]].values]
    det = p.parse_event("smokegrenade_detonate")
    lands = [[round(x), round(y)] for x, y in det[["x", "y"]].dropna().values]
    return throws, lands


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("demos", nargs="+")
    args = ap.parse_args()

    throws, lands, used = [], [], 0
    for demo in args.demos:
        try:
            header_map = DemoParser(demo).parse_header().get("map_name", "")
        except Exception as e:
            print(f"skip {demo}: header read failed ({e})", file=sys.stderr)
            continue
        if header_map != args.map:
            print(f"skip {demo}: map is {header_map}, not {args.map}", file=sys.stderr)
            continue
        try:
            t, l = extract(demo)
        except Exception as e:
            print(f"skip {demo}: parse failed ({e})", file=sys.stderr)
            continue
        throws += t
        lands += l
        used += 1
        print(f"  {demo}: {len(t)} throws, {len(l)} lands", file=sys.stderr)

    payload = {"map": args.map, "demos": used, "throws": throws, "lands": lands}
    with open(args.out, "w") as f:
        json.dump(payload, f, separators=(",", ":"))
    print(f"wrote {args.out}: {used} demos, {len(throws)} throws, {len(lands)} lands", file=sys.stderr)


if __name__ == "__main__":
    main()
