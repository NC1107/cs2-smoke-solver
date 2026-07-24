#!/usr/bin/env bash
# Full per-map onboarding pipeline: extract -> derive the viewerdata region
# from the nav mesh -> viewerdata -> textured GLB (desktop + mobile).
# Idempotent per stage (skips work whose output file already exists), so a
# re-run after a mid-pipeline failure resumes instead of redoing finished maps.
#
# Usage: rig/add-map.sh <map>
# Env: CS2_GAME_DIR overrides the CS2 install root (default: standard Steam path).
set -euo pipefail

map="${1:?usage: add-map.sh <map>}"
game_dir="${CS2_GAME_DIR:-$HOME/.local/share/Steam/steamapps/common/Counter-Strike Global Offensive}"
repo="$(cd "$(dirname "$0")/.." && pwd)"
export PATH="$HOME/.dotnet:$PATH"
cd "$repo"

if [[ ! -f "data/$map.s2geo" ]]; then
  echo "==> [$map] extract"
  dotnet run --project src/Cli -- extract --game "$game_dir" --map "$map" --out data
fi

if [[ ! -f "data/$map.viewer-map.json" ]]; then
  echo "==> [$map] derive region from nav areas"
  region=$(python3 - "data/$map.navareas.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
xs, ys = [], []
for area in d:
    for c in area["Corners"]:
        xs.append(c[0]); ys.append(c[1])
pad = 200
print(f"{min(xs)-pad:.0f},{min(ys)-pad:.0f},{max(xs)+pad:.0f},{max(ys)+pad:.0f}")
PY
)
  echo "    region=$region"
  echo "==> [$map] viewerdata"
  dotnet run --project src/Cli -- viewerdata --geo "data/$map.s2geo" \
    --entities "data/$map.entities.json" --attrs "Default,default" \
    --region "$region" --out "data/$map.viewer-map.json"
fi

if [[ ! -f "data/${map}_textured.glb" ]]; then
  echo "==> [$map] textured glb"
  rig/build-textured-glb.sh "$map" "$game_dir/game/csgo/maps"
fi

if [[ ! -f "data/${map}_textured.mobile.glb" ]]; then
  echo "==> [$map] mobile glb"
  node rig/optimize-textured-glb.mjs "data/${map}_textured.glb" "data/${map}_textured.mobile.glb" --mobile
fi

echo "==> [$map] done"
