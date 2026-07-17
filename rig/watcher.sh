#!/bin/bash
# Services in-game requests written by the CalibrationThrower plugin.
#
# Directory contract for $CALIB (default: <repo>/data/calib):
#   request.json           written by relays/CLI (atomic rename), consumed by the plugin
#   test-request.json      written by plugin (!test), claimed and serviced here
#   lineup-request.json    written by plugin (!lineup), claimed and serviced here
#   plineup-request.json   written by plugin (!plineup), claimed and serviced here
#   stop-request           written by plugin (!stop), consumed by the validate CLI
#   captures.jsonl         appended by the plugin, tailed by the validate CLI
#   watcher.heartbeat      touched every loop; staleness means this script died
#   rig.log                shared log for watcher + python relays
#
# set -e is deliberately omitted: a failed request must not kill the poll
# loop. Each request is claimed by rename first, so a crash mid-service
# never replays or loses the original file.
set -u -o pipefail

REPO="$(cd "$(dirname "$(readlink -f "$0")")/.." && pwd)"
CALIB="${SMOKESOLVER_CALIB_DIR:-$REPO/data/calib}"
LOG="$CALIB/rig.log"
export PATH="$HOME/.dotnet:$PATH"
export PYTHONPATH="$REPO/rig${PYTHONPATH:+:$PYTHONPATH}"
cd "$REPO"
# Prefer the published binary (rig/deploy: dotnet publish src/Cli -o rig/bin):
# it is decoupled from the working tree, so a mid-edit compile error cannot
# turn into silent in-game failures. dotnet run remains as a dev fallback.
if [[ -x "$REPO/rig/bin/SmokeSolver.Cli" ]]; then
  CLI=("$REPO/rig/bin/SmokeSolver.Cli")
else
  CLI=(dotnet run --project src/Cli -c Release -v q --)
fi

logw() { printf '%s watcher %s\n' "$(date '+%F %T')" "$*" | tee -a "$LOG"; }

# Requests written while the watcher was down must not fire hours later.
for f in "$CALIB"/test-request.json "$CALIB"/lineup-request.json "$CALIB"/plineup-request.json; do
  if [[ -f "$f" ]]; then
    logw "discarding stale request left from previous session: $f"
    rm -f "$f"
  fi
done

# Claim a request by rename; parse only after the claim so a mid-write file
# is retried on the next poll instead of being destroyed.
claim() {
  local src="$1" dst="$1.processing"
  mv "$src" "$dst" 2>/dev/null && echo "$dst"
}

fields() {
  local file="$1"; shift
  python3 - "$file" "$@" << 'PYEOF'
import json, sys
doc = json.load(open(sys.argv[1]))
for key in sys.argv[2:]:
    default = {"pass": 1, "tolerance": 80, "limit": 0, "mode": "quick"}.get(key, "")
    v = doc.get(key, default)
    if isinstance(v, list):
        print(",".join(str(x) for x in v))
    else:
        print(v)
PYEOF
}

# A request solved against the wrong map's geometry produces confidently
# wrong answers, so the map field is mandatory and its assets must exist.
map_assets() {
  local map="$1"
  GEO="data/$map.s2geo"
  NAV="data/$map.navareas.json"
  if [[ ! -f "$GEO" || ! -f "$NAV" ]]; then
    python3 rig/relay-chat.py "no extracted geometry for map '$map' - run the extract command first"
    return 1
  fi
}

service_test() {
  local f="$1"
  local name target pass tol limit map
  { read -r name; read -r target; read -r pass; read -r tol; read -r limit; read -r map; } \
    < <(fields "$f" name pos pass tolerance limit map) || { logw "bad test request, skipping"; rm -f "$f"; return; }
  map="${map:-de_dust2}"
  map_assets "$map" || { rm -f "$f"; return; }
  logw "test run for '$name' @ $target on $map (pass $pass, tolerance $tol, limit $limit)"
  local out
  out=$("${CLI[@]}" validate --geo "$GEO" --nav "$NAV" \
        --target " $target" --pass "$pass" --tolerance "$tol" --limit "$limit" 2>> "$LOG")
  echo "$out" | tail -4 >> "$LOG"
  if echo "$out" | grep -q "^0 lineups solved"; then
    python3 rig/relay-chat.py "no lineups found for '$name' - nothing can land within ${tol}u of it"
  else
    readarray -t summary < <(python3 rig/summarize-run.py)
    python3 rig/relay-chat.py "${summary[@]}"
  fi
  rm -f "$f"
  logw "test for '$name' done"
}

service_lineup() {
  local f="$1" relay="$2"
  local name target from mode map result
  { read -r name; read -r target; read -r from; read -r mode; read -r map; } \
    < <(fields "$f" name pos player mode map) || { logw "bad lineup request, skipping"; rm -f "$f"; return; }
  map="${map:-de_dust2}"
  map_assets "$map" || { rm -f "$f"; return; }
  logw "$relay for '$name' from ($from) on $map mode=$mode"
  if [[ "$relay" == "relay-plineup.py" ]]; then
    result=$("${CLI[@]}" pointlineup --geo "$GEO" --from " $from" --target " $target" \
             --mode "$mode" 2>> "$LOG" | tail -1)
    # A miss from the exact spot is a dead end. Fall back to the nearest spot
    # that DOES land and deliver it through the !lineup formatter, so the player
    # gets a stand-beam and a one-word !goto to snap there instead of raw coords.
    case "$result" in
      *'"found":true'*) : ;;
      *)
        logw "plineup miss - falling back to nearest working lineup"
        result=$("${CLI[@]}" bestlineup --geo "$GEO" --nav "$NAV" \
                 --target " $target" --near " $from" 2>> "$LOG" | tail -1)
        relay=relay-lineup.py
        ;;
    esac
  else
    result=$("${CLI[@]}" bestlineup --geo "$GEO" --nav "$NAV" \
             --target " $target" --near " $from" 2>> "$LOG" | tail -1)
  fi
  logw "result: ${result:-<empty>}"
  python3 "rig/$relay" "$(cat "$f")" "$result"
  rm -f "$f"
}

logw "watcher started (calib dir: $CALIB)"
while true; do
  touch "$CALIB/watcher.heartbeat"
  if f=$(claim "$CALIB/test-request.json"); then service_test "$f"; fi
  if f=$(claim "$CALIB/lineup-request.json"); then service_lineup "$f" relay-lineup.py; fi
  if f=$(claim "$CALIB/plineup-request.json"); then service_lineup "$f" relay-plineup.py; fi
  sleep 2
done
