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
    """Return (throws, lands) as [x, y, side] ints (side 0=T, 1=CT), or None.

    The smoke projectile's entity id equals the detonate event's entity id, so
    each throw origin (first projectile tick) joins to its landing and to the
    thrower's team - which makes a smoke "T" (attacker, from the T side) or "CT"
    (defender). Team side is what defines the smoke, so it beats a geometric
    guess even though teams swap halves.
    """
    p = DemoParser(path)
    det = p.parse_event("smokegrenade_detonate", player=["team_num"]).dropna(subset=["x", "y"])
    # entity id -> (land_x, land_y, side); side 0 = T (team 2), 1 = CT (team 3).
    by_id = {}
    for _, r in det.iterrows():
        by_id[int(r["entityid"])] = (r["x"], r["y"], r["z"], 0 if int(r["user_team_num"]) == 2 else 1)

    grenades = p.parse_grenades()
    proj = grenades[grenades["grenade_type"] == "CSmokeGrenadeProjectile"].dropna(subset=["x", "y"])
    firsts = proj.sort_values("tick").groupby("grenade_entity_id").first()

    throws, lands = [], []
    for eid, row in firsts.iterrows():
        hit = by_id.get(int(eid))
        if hit is None:
            continue  # thrown but never detonated (defused round end, etc.)
        lx, ly, lz, side = hit
        throws.append([round(row["x"]), round(row["y"]), side])
        lands.append([round(lx), round(ly), round(lz), side])  # z kept for targets
    return throws, lands


def cluster_targets(lands, cell=80, min_count=4):
    """Grid-bin the landing points (x,y,z,side) into hotspot targets: cells with
    at least min_count smokes, returned as { x, y, z, count } at the mean
    position. These are the spots pros actually smoke - the coverage the solver
    should be tested against."""
    bins = {}
    for x, y, z, _ in lands:
        key = (round(x / cell), round(y / cell))
        bins.setdefault(key, []).append((x, y, z))
    out = []
    for pts in bins.values():
        if len(pts) < min_count:
            continue
        n = len(pts)
        out.append({
            "x": round(sum(p[0] for p in pts) / n),
            "y": round(sum(p[1] for p in pts) / n),
            "z": round(sum(p[2] for p in pts) / n),
            "count": n,
        })
    out.sort(key=lambda t: -t["count"])
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--map", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--targets", help="also write clustered hotspot targets here")
    ap.add_argument("--append", action="store_true", help="merge into existing --out")
    ap.add_argument("demos", nargs="+")
    args = ap.parse_args()

    # throws are [x, y, side]; lands are [x, y, z, side] (z kept for targets).
    # The viewer reads [0]/[1] for position and the LAST element for side, so it
    # ignores the extra land z with no special-casing.
    throws, lands, used = [], [], 0
    if args.append:
        try:
            prev = json.load(open(args.out))
            throws = prev.get("throws", [])
            lands = prev.get("lands", [])
            used = prev.get("demos", 0)
        except FileNotFoundError:
            pass

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

    if args.targets:
        targets = cluster_targets(lands)
        with open(args.targets, "w") as f:
            json.dump({"map": args.map, "targets": targets}, f, separators=(",", ":"))
        print(f"wrote {args.targets}: {len(targets)} hotspot targets", file=sys.stderr)


if __name__ == "__main__":
    main()
