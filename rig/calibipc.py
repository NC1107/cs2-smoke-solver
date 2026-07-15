"""Shared IPC helpers for the calibration rig.

The plugin consumes ``request.json`` from the calib dir (claiming it by
rename before reading). Writers must therefore make the file appear
atomically: write a temp file in the same directory and rename it into
place. A pending unconsumed request is a hard error, never silently
overwritten - losing a message corrupts a calibration run.
"""
import json
import logging
import os
import sys
import time
from pathlib import Path

CALIB_DIR = Path(os.environ.get(
    "SMOKESOLVER_CALIB_DIR",
    Path(__file__).resolve().parents[1] / "data" / "calib",
))
REQUEST_PATH = CALIB_DIR / "request.json"
LOG_PATH = CALIB_DIR / "rig.log"

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(name)s %(levelname)s %(message)s",
    handlers=[logging.FileHandler(LOG_PATH), logging.StreamHandler(sys.stderr)],
)
log = logging.getLogger("calibipc")


class RequestPendingError(RuntimeError):
    """The plugin did not consume the previous request in time."""


def send(payload: dict, timeout: float = 10.0) -> None:
    """Atomically deliver one payload to the plugin's request mailbox."""
    deadline = time.monotonic() + timeout
    while REQUEST_PATH.exists():
        if time.monotonic() > deadline:
            log.error("request.json still pending after %.0fs; refusing to clobber: %s",
                      timeout, payload.get("chat") or list(payload)[:1])
            raise RequestPendingError(
                f"plugin did not consume {REQUEST_PATH} within {timeout}s")
        time.sleep(0.1)
    tmp = REQUEST_PATH.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(payload))
    os.rename(tmp, REQUEST_PATH)
    log.info("sent %s", ",".join(payload.keys()))


def send_chat(*lines: str) -> None:
    send({"chat": [f" [calib] {line}" for line in lines]})


def parse_solver_result(argv, name: str, action_desc: str) -> dict:
    """Parse the solver's JSON result from argv[2], or report the crash to
    chat and exit. Shared by the relay scripts so a third relay cannot fork
    the error-handling (or the wording) again."""
    try:
        return json.loads(argv[2])
    except (IndexError, json.JSONDecodeError):
        log.error("%s produced no parseable result for %r: argv=%r",
                  action_desc, name, argv[2:])
        send_chat(f"solver error while {action_desc} '{name}' - check rig.log")
        sys.exit(1)


def click_hint_aim(res: dict) -> tuple[str, list]:
    """The player-facing click hint and the aim vector for a solver result.
    The click bands mirror CliParsing.ClickName - one wording everywhere."""
    strength = res["strength"]
    click = "left" if strength >= 0.99 else ("left+right" if strength >= 0.49 else "right")
    hint = f"{res['type']} {click} click"
    return hint, list(res["aim"]) + [res["yaw"]]
