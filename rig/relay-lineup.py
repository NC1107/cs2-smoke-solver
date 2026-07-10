"""Format a bestlineup result as chat lines + beams and deliver it to the
plugin. A solver crash is reported as a distinct error, never conflated
with a legitimate "no lineups" answer."""
import json
import sys

import calibipc

req = json.loads(sys.argv[1])
name = req["name"]

try:
    res = json.loads(sys.argv[2])
except (IndexError, json.JSONDecodeError):
    calibipc.log.error("bestlineup produced no parseable result for %r: argv=%r",
                       name, sys.argv[2:])
    calibipc.send_chat(f"solver error while finding a lineup for '{name}' - check rig.log")
    sys.exit(1)

if not res.get("found"):
    calibipc.send_chat(f"no lineups reach '{name}'")
    sys.exit(0)

strength = res["strength"]
click = "left" if strength >= 0.99 else ("left+right" if strength >= 0.49 else "right")
feet = res["feet"]
pitch, yaw = res["pitch"], res["yaw"]
hint = f"{res['type']} {click} click"
aim = list(res["aim"]) + [yaw]
calibipc.send({
    "chat": [
        f" [calib] best lineup for {name}: {res['dist']:.0f}u from you ({res['candidates']} candidates)",
        " blue beam = stand spot, yellow X = aim point - or type !goto to snap there",
        f" then: {hint}",
    ],
    "beam": feet,
    "aimbeam": aim,
    "store": {"pos": feet, "pitch": pitch, "yaw": yaw, "hint": hint, "aim": aim, "slot": req.get("slot", -1)},
})
