"""Format a bestlineup result as chat lines + beams and deliver it to the
plugin. A solver crash is reported as a distinct error, never conflated
with a legitimate "no lineups" answer."""
import json
import sys

import calibipc

req = json.loads(sys.argv[1])
name = req["name"]

res = calibipc.parse_solver_result(sys.argv, name, "finding a lineup for")

if not res.get("found"):
    calibipc.send_chat(f"no lineups reach '{name}'")
    sys.exit(0)

feet = res["feet"]
pitch, yaw = res["pitch"], res["yaw"]
hint, aim = calibipc.click_hint_aim(res)
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
