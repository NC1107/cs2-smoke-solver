#!/bin/bash
# Time-boxed heavy accuracy run: loops randomized fuzz batches (fresh random
# targets each iteration) with one-tick perturbation probes until the time
# budget runs out, pushing reports to the live dashboard after every batch.
#
#   accuracy-overnight.sh [--hours 4] [--maps a,b,c] [--no-push]
set -euo pipefail

RIG_ENV="$(dirname "$(readlink -f "$0")")/rig.env"
set -a; source "$RIG_ENV"; set +a
REPO="$SMOKESOLVER_REPO"
DOTNET="${DOTNET_ROOT:-$HOME/.dotnet}/dotnet"
CLI="$REPO/src/Cli/bin/Debug/net10.0/SmokeSolver.Cli.dll"
DEPLOY_HOST="npc@10.0.0.100"
DEPLOY_DATA="/home/npc/docker-server/npc_projects/cs2-smoke-solver/data"

HOURS=4
MAPS=""
PUSH=1
while [[ $# -gt 0 ]]; do
  case "$1" in
    --hours) HOURS="$2"; shift 2 ;;
    --maps) MAPS="$2"; shift 2 ;;
    --no-push) PUSH=0; shift ;;
    *) echo "unknown arg $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$MAPS" ]]; then
  for geo in "$REPO"/data/*.s2geo; do
    map="$(basename "$geo" .s2geo)"
    [[ "$map" == "flatgrass" ]] && continue
    [[ -f "$REPO/data/$map.navareas.json" ]] || continue
    MAPS="${MAPS:+$MAPS,}$map"
  done
fi

echo "==> building the CLI"
"$DOTNET" build "$REPO/src/Cli" -v q

if ! systemctl --user is-active --quiet cs2-server; then
  echo "==> starting cs2-server"
  systemctl --user start cs2-server
  sleep 45
fi

cd "$REPO"
END=$((SECONDS + HOURS * 3600))
ITER=0
STAMP="$(date +%Y%m%d-%H%M)"
echo "==> heavy run for ${HOURS}h over: $MAPS"
while (( SECONDS < END )); do
  ITER=$((ITER + 1))
  echo "==> iteration $ITER ($(( (END - SECONDS) / 60 )) min left)"
  # Fresh random targets each pass (seed varies per iteration); 25 base
  # lineups per target plus 4 one-tick probes each = ~125 real throws per
  # target. Markers are skipped - the nightly-pilot already covers them.
  "$DOTNET" "$CLI" batchvalidate --maps "$MAPS" --no-markers --targets-per-map 0 \
    --fuzz 3 --seed "$STAMP-$ITER" --limit 25 --perturb 0.25 --pass 2 \
    --batch "overnight-$STAMP" || echo "iteration $ITER failed; continuing"
  if [[ "$PUSH" == 1 ]]; then
    rsync -a "$REPO/data/validation/" "$DEPLOY_HOST:$DEPLOY_DATA/validation/" || true
  fi
done
echo "==> done: $ITER iteration(s)"
