#!/usr/bin/env bash
# Gather pro smoke data for every supported map from HLTV demos.
#
# For each map it scans HLTV results (map-filtered), downloads each match's demo
# archive, extracts only that map's .dem, parses smoke throw origins + landings
# (T/CT tagged) into data/<map>.prosmokes.json, and clusters the landings into
# data/<map>.protargets.json - the spots pros actually smoke, which are the
# coverage the solver should be tested against.
#
# Archives are hundreds of MB each, so every rar and dem is deleted the moment
# it has been parsed; peak disk stays around one archive. Built to run for hours
# unattended in the background; re-runnable (each map is rebuilt from scratch).
#
#   rig/gather-pro-demos.sh                 # all maps, defaults
#   DEMOS_PER_MAP=25 rig/gather-pro-demos.sh
#   MAPS="de_nuke de_train" rig/gather-pro-demos.sh
set -uo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="${GATHER_WORK:-$HOME/.cache/cs2-demo-gather}"
LOG="${GATHER_LOG:-$REPO/data/gather-pro-demos.log}"
PY="$REPO/rig/parse-demo-smokes.py"
UA="Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"

read -r -a MAPS <<<"${MAPS:-de_mirage de_inferno de_nuke de_ancient de_anubis de_dust2 de_overpass}"
DEMOS_PER_MAP="${DEMOS_PER_MAP:-18}"
MAX_MATCH_PAGES="${MAX_MATCH_PAGES:-8}"   # HLTV results pages to scan per map (100 results/page)

mkdir -p "$WORK"
log(){ printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*" | tee -a "$LOG"; }
fetch(){ curl -sL --compressed --retry 3 --retry-delay 5 -A "$UA" "$@"; }

# Fail fast if a dependency is missing rather than after the first download.
for dep in curl unrar python3; do
  command -v "$dep" >/dev/null || { echo "missing dependency: $dep" >&2; exit 1; }
done
python3 -c "import demoparser2" 2>/dev/null || { echo "missing python module: demoparser2 (pip install demoparser2)" >&2; exit 1; }

log "=== gather start: maps=[${MAPS[*]}] demos/map=$DEMOS_PER_MAP work=$WORK ==="

for map in "${MAPS[@]}"; do
  short="${map#de_}"
  out="$REPO/data/$map.prosmokes.json"
  targets="$REPO/data/$map.protargets.json"
  tmp_out="$WORK/$map.prosmokes.json"
  tmp_tgt="$WORK/$map.protargets.json"
  rm -f "$tmp_out" "$tmp_tgt"

  # Collect candidate match URLs across several result pages.
  declare -a match_urls=()
  for off in $(seq 0 100 $(((MAX_MATCH_PAGES - 1) * 100))); do
    page="$(fetch "https://www.hltv.org/results?map=$map&offset=$off")"
    while IFS= read -r u; do [ -n "$u" ] && match_urls+=("$u"); done \
      < <(printf '%s' "$page" | grep -oE '/matches/[0-9]+/[a-z0-9-]+' | sort -u)
    sleep 2
  done
  # Dedup while preserving nothing in particular (order does not matter).
  if [ "${#match_urls[@]}" -gt 0 ]; then
    mapfile -t match_urls < <(printf '%s\n' "${match_urls[@]}" | sort -u)
  fi
  log "MAP $map: ${#match_urls[@]} candidate matches over $MAX_MATCH_PAGES pages"

  got=0
  for mu in "${match_urls[@]}"; do
    [ "$got" -ge "$DEMOS_PER_MAP" ] && break
    mpage="$(fetch "https://www.hltv.org$mu")"
    demo_path="$(printf '%s' "$mpage" | grep -oE '/download/demo/[0-9]+' | head -1)"
    [ -z "$demo_path" ] && { sleep 1; continue; }

    rar="$WORK/$short.rar"
    rm -f "$rar"
    fetch -o "$rar" "https://www.hltv.org$demo_path"
    if [ ! -s "$rar" ] || ! head -c4 "$rar" | grep -qa 'Rar'; then
      log "  ${mu##*/}: not a demo archive (challenged or empty), skip"
      rm -f "$rar"; sleep 3; continue
    fi

    unrar e -o+ -inul "$rar" "*$short*.dem" "$WORK/" 2>/dev/null
    rm -f "$rar"
    shopt -s nullglob
    dems=("$WORK"/*"$short"*.dem)
    shopt -u nullglob
    if [ "${#dems[@]}" -eq 0 ]; then
      log "  ${mu##*/}: no $short .dem in archive"; sleep 2; continue
    fi
    for dem in "${dems[@]}"; do
      if python3 "$PY" --map "$map" --append --out "$tmp_out" --targets "$tmp_tgt" "$dem" >>"$LOG" 2>&1; then
        got=$((got + 1))
      fi
      rm -f "$dem"
    done
    log "  MAP $map: $got/$DEMOS_PER_MAP demos parsed (${mu##*/})"
    sleep 3
  done

  if [ -s "$tmp_out" ]; then
    cp "$tmp_out" "$out"
    [ -s "$tmp_tgt" ] && cp "$tmp_tgt" "$targets"
    log "MAP $map DONE: $out ($got demos)"
  else
    log "MAP $map: nothing collected (HLTV may be rate-limiting)"
  fi
done

log "=== gather complete ==="
log "Next: rsync data/*.prosmokes.json data/*.protargets.json to the server, then purge the CDN."
