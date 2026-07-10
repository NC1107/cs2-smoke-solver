"""Format a pointlineup result as chat + sky aim cross + stored angles for
!goto (which snaps the view since the player is already standing there)."""
import json
import os
import sys
import time

req = json.loads(sys.argv[1])
try:
    res = json.loads(sys.argv[2])
except Exception:
    res = {"found": False, "err": -1}

name = req["name"]
player = req["player"]
if not res.get("found"):
    if res.get("err", -1) < 0:
        payload = {"chat": [f" [calib] solver failed for '{name}'"]}
    else:
        payload = {"chat": [
            f" [calib] no throw from your exact spot lands on '{name}'",
            f" closest possible: {res['err']:.0f}u away ({res['type']} at pitch {res['pitch']:.1f} yaw {res['yaw']:.1f})",
        ]}
else:
    strength = res["strength"]
    click = "left" if strength >= 0.99 else ("left+right" if strength >= 0.49 else "right")
    ttype = res["type"]
    pitch, yaw = res["pitch"], res["yaw"]
    hint = f"{ttype} {click} click"
    aim = list(res["aim"]) + [yaw]
    payload = {
        "chat": [
            f" [calib] '{name}' is throwable from your spot - predicted {res['err']:.1f}u off the marker",
            f" put your crosshair on the yellow X - {hint}",
            f" verification throw incoming - watch where it lands",
        ],
        "aimbeam": aim,
        "store": {"pos": player, "pitch": pitch, "yaw": yaw, "hint": hint, "aim": aim},
    }

path = "/home/npc/Documents/projects/cs2-smoke-solver/data/calib/request.json"


def send(p):
    for _ in range(100):
        if not os.path.exists(path):
            break
        time.sleep(0.1)
    with open(path, "w") as f:
        json.dump(p, f)


send(payload)

# Fire the solution as a real synthetic throw so the player sees exactly
# where it lands, with the standard predicted-vs-real error line in chat.
if res.get("found"):
    send({"throws": [{
        "pos": res["initpos"],
        "vel": res["initvel"],
        "predict": res["rest"],
        "note": f"verify '{name}' -> predict ({res['rest'][0]:.0f},{res['rest'][1]:.0f},{res['rest'][2]:.0f})",
    }]})
