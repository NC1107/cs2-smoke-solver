"""Format a pointlineup result as chat + sky aim cross + stored angles for
!goto, then fire a verification throw of the solution so the player sees
where it really lands. Solver crashes surface as errors, not false
negatives."""
import json
import sys

import calibipc

req = json.loads(sys.argv[1])
name = req["name"]
player = req["player"]

try:
    res = json.loads(sys.argv[2])
except (IndexError, json.JSONDecodeError):
    calibipc.log.error("pointlineup produced no parseable result for %r: argv=%r",
                       name, sys.argv[2:])
    calibipc.send_chat(f"solver error while solving '{name}' from your spot - check rig.log")
    sys.exit(1)

if not res.get("found"):
    calibipc.send_chat(
        f"no throw from your exact spot lands on '{name}'",
        f"closest possible: {res['err']:.0f}u away ({res['type']} at pitch {res['pitch']:.1f} yaw {res['yaw']:.1f})",
    )
    sys.exit(0)

strength = res["strength"]
click = "left" if strength >= 0.99 else ("left+right" if strength >= 0.49 else "right")
pitch, yaw = res["pitch"], res["yaw"]
hint = f"{res['type']} {click} click"
aim = list(res["aim"]) + [yaw]
calibipc.send({
    "chat": [
        f" [calib] '{name}' is throwable from your spot - predicted {res['err']:.1f}u off the marker",
        f" put your crosshair on the yellow X - {hint}",
        " verification throw incoming - watch where it lands",
    ],
    "aimbeam": aim,
    "store": {"pos": player, "pitch": pitch, "yaw": yaw, "hint": hint, "aim": aim, "slot": req.get("slot", -1)},
})

calibipc.send({"throws": [{
    "pos": res["initpos"],
    "vel": res["initvel"],
    "predict": res["rest"],
    "note": f"verify '{name}' -> predict ({res['rest'][0]:.0f},{res['rest'][1]:.0f},{res['rest'][2]:.0f})",
}]})
