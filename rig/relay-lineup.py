"""Format a bestlineup result as chat lines + beam and hand it to the plugin
via the request-file channel (waits for any pending request to clear first)."""
import json
import os
import sys
import time

req = json.loads(sys.argv[1])
try:
    res = json.loads(sys.argv[2])
except Exception:
    res = {"found": False}

name = req["name"]
if not res.get("found"):
    payload = {"chat": [f" [calib] no lineups reach '{name}'"]}
else:
    strength = res["strength"]
    click = "left" if strength >= 0.99 else ("left+right" if strength >= 0.49 else "right")
    feet = res["feet"]
    pitch, yaw, ttype = res["pitch"], res["yaw"], res["type"]
    hint = f"{ttype} {click} click"
    aim = list(res["aim"]) + [yaw]
    payload = {
        "chat": [
            f" [calib] best lineup for {name}: {res['dist']:.0f}u from you ({res['candidates']} candidates)",
            f" blue beam = stand spot, yellow X = aim point - or type !goto to snap there",
            f" then: {hint}",
        ],
        "beam": feet,
        "aimbeam": aim,
        "store": {"pos": feet, "pitch": pitch, "yaw": yaw, "hint": hint, "aim": aim},
    }

path = "/home/npc/Documents/projects/cs2-smoke-solver/data/calib/request.json"
for _ in range(100):
    if not os.path.exists(path):
        break
    time.sleep(0.1)
with open(path, "w") as f:
    json.dump(payload, f)
