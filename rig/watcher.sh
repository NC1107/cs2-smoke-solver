#!/bin/bash
# Services in-game !test and !lineup requests written by the CalibrationThrower
# plugin. !test runs a full validate round; !lineup finds the nearest lineup
# and relays it back to chat with a beam at the stand spot.
export PATH="$HOME/.dotnet:$PATH"
CALIB=/home/npc/Documents/projects/cs2-smoke-solver/data/calib
cd /home/npc/Documents/projects/cs2-smoke-solver
CLI="dotnet run --project src/Cli -c Release -v q --"

while true; do
  if [ -f "$CALIB/test-request.json" ]; then
    REQ=$(cat "$CALIB/test-request.json"); rm -f "$CALIB/test-request.json"
    TARGET=$(echo "$REQ" | python3 -c 'import json,sys; p=json.load(sys.stdin)["pos"]; print(f"{p[0]},{p[1]},{p[2]}")')
    NAME=$(echo "$REQ" | python3 -c 'import json,sys; print(json.load(sys.stdin)["name"])')
    PASS=$(echo "$REQ" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("pass", 1))')
    TOL=$(echo "$REQ" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("tolerance", 80))')
    LIMIT=$(echo "$REQ" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("limit", 0))')
    echo "[watcher] test run for marker $NAME @ $TARGET (pass $PASS, tolerance $TOL, limit $LIMIT)"
    OUT=$($CLI validate --geo data/de_dust2.s2geo --nav data/de_dust2.navareas.json --target " $TARGET" --pass "$PASS" --tolerance "$TOL" --limit "$LIMIT" 2>&1)
    echo "$OUT" | tail -4
    if echo "$OUT" | grep -q "^0 lineups solved"; then
      python3 /home/npc/Documents/projects/cs2-smoke-solver/rig/relay-chat.py "no lineups found for '$NAME' - nothing can land within ${TOL}u of it"
    else
      IFS=$'\n' readarray -t SUMMARY < <(python3 /home/npc/Documents/projects/cs2-smoke-solver/rig/summarize-run.py)
      python3 /home/npc/Documents/projects/cs2-smoke-solver/rig/relay-chat.py "${SUMMARY[@]}"
    fi
    echo "[watcher] test for $NAME done"
  fi
  if [ -f "$CALIB/lineup-request.json" ]; then
    REQ=$(cat "$CALIB/lineup-request.json"); rm -f "$CALIB/lineup-request.json"
    echo "[watcher] lineup request: $REQ"
    TARGET=$(echo "$REQ" | python3 -c 'import json,sys; p=json.load(sys.stdin)["pos"]; print(f" {p[0]},{p[1]},{p[2]}")')
    NEAR=$(echo "$REQ" | python3 -c 'import json,sys; p=json.load(sys.stdin)["player"]; print(f" {p[0]},{p[1]},{p[2]}")')
    RESULT=$($CLI bestlineup --geo data/de_dust2.s2geo --nav data/de_dust2.navareas.json --target "$TARGET" --near "$NEAR" 2>/dev/null | tail -1)
    echo "[watcher] result: $RESULT"
    python3 /home/npc/Documents/projects/cs2-smoke-solver/rig/relay-lineup.py "$REQ" "$RESULT"
  fi
  if [ -f "$CALIB/plineup-request.json" ]; then
    REQ=$(cat "$CALIB/plineup-request.json"); rm -f "$CALIB/plineup-request.json"
    echo "[watcher] plineup request: $REQ"
    TARGET=$(echo "$REQ" | python3 -c 'import json,sys; p=json.load(sys.stdin)["pos"]; print(f" {p[0]},{p[1]},{p[2]}")')
    FROM=$(echo "$REQ" | python3 -c 'import json,sys; p=json.load(sys.stdin)["player"]; print(f" {p[0]},{p[1]},{p[2]}")')
    MODE=$(echo "$REQ" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("mode", "quick"))')
    RESULT=$($CLI pointlineup --geo data/de_dust2.s2geo --from "$FROM" --target "$TARGET" --mode "$MODE" 2>/dev/null | tail -1)
    echo "[watcher] result: $RESULT"
    python3 /home/npc/Documents/projects/cs2-smoke-solver/rig/relay-plineup.py "$REQ" "$RESULT"
  fi
  sleep 2
done
