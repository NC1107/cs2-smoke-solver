#!/bin/bash
# Unattended accuracy sweep: ensures the rig CS2 server is up, runs the
# batchvalidate CLI across the given maps (default: every extracted map that
# has nav data, minus flatgrass), and optionally pushes the reports to the
# live site so the Accuracy dashboard updates.
#
#   accuracy-run.sh [--maps de_mirage,de_overpass] [--push] [batchvalidate args...]
#
# Everything after the flags is forwarded to batchvalidate verbatim, e.g.
#   accuracy-run.sh --maps de_mirage --push --targets-per-map 4 --limit 60
set -euo pipefail

RIG_ENV="$(dirname "$(readlink -f "$0")")/rig.env"
set -a; source "$RIG_ENV"; set +a
REPO="$SMOKESOLVER_REPO"
DOTNET="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
CLI="$REPO/src/Cli/bin/Debug/net10.0/SmokeSolver.Cli.dll"
DEPLOY_HOST="npc@10.0.0.100"
DEPLOY_DATA="/home/npc/docker-server/npc_projects/cs2-smoke-solver/data"

MAPS=""
PUSH=0
ARGS=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --maps) MAPS="$2"; shift 2 ;;
    --push) PUSH=1; shift ;;
    *) ARGS+=("$1"); shift ;;
  esac
done

if [[ ! -f "$CLI" ]]; then
  echo "==> building the CLI (no Debug binary found)"
  "$DOTNET" build "$REPO/src/Cli" -v q
fi

# Default map pool: everything extracted WITH nav data. flatgrass is the
# physics test bed, not a validation scenario.
if [[ -z "$MAPS" ]]; then
  for geo in "$REPO"/data/*.s2geo; do
    map="$(basename "$geo" .s2geo)"
    [[ "$map" == "flatgrass" ]] && continue
    [[ -f "$REPO/data/$map.navareas.json" ]] || continue
    MAPS="${MAPS:+$MAPS,}$map"
  done
fi
[[ -n "$MAPS" ]] || { echo "no maps with nav data under $REPO/data" >&2; exit 1; }

if ! systemctl --user is-active --quiet cs2-server; then
  echo "==> starting cs2-server"
  systemctl --user start cs2-server
  # Server boot + plugin load; batchvalidate's changelevel handshake verifies
  # actual liveness before any throw is submitted.
  sleep 45
fi

echo "==> accuracy sweep over: $MAPS"
cd "$REPO"
"$DOTNET" "$CLI" batchvalidate --maps "$MAPS" "${ARGS[@]}"

if [[ "$PUSH" == 1 ]]; then
  echo "==> pushing reports to the live site"
  rsync -av "$REPO/data/validation/" "$DEPLOY_HOST:$DEPLOY_DATA/validation/"
fi
