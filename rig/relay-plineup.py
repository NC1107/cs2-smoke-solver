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

res = calibipc.parse_solver_result(sys.argv, name, "solving from your spot")

if not res.get("found"):
    calibipc.send_chat(
        f"no throw from your exact spot lands on '{name}'",
        f"closest possible: {res['err']:.0f}u away ({res['type']} at pitch {res['pitch']:.1f} yaw {res['yaw']:.1f})",
    )
    sys.exit(0)

pitch, yaw = res["pitch"], res["yaw"]
hint, aim = calibipc.click_hint_aim(res)
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
