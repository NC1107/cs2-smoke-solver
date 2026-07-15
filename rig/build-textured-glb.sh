#!/usr/bin/env bash
# Builds the viewer's textured GLB for a map in the ONE correct order:
#
#   exportgltf (VRF)  ->  fix-prop-scale  ->  optimize-textured-glb
#
# fix-prop-scale MUST sit in the middle: it undoes VRF's per-instance 39x
# unit-conversion bug by editing node.scale, and optimize-textured-glb's
# flatten() bakes node.scale into the vertices. Run the optimizer on an
# unfixed export and the giant props (a 39x TV, stool, ceiling fan, curtain)
# are baked in permanently, with no node.scale left to correct. Every textured
# GLB the viewer serves must come through this script so that step can never be
# skipped again.
#
# Usage: rig/build-textured-glb.sh <map> [vpk-dir]
#   map     : e.g. de_mirage (the .vpk basename)
#   vpk-dir : directory holding <map>.vpk; defaults to $CS2_MAPS_DIR, then the
#             standard Steam install path.
set -euo pipefail

map="${1:?usage: build-textured-glb.sh <map> [vpk-dir]}"
vpkdir="${2:-${CS2_MAPS_DIR:-$HOME/.local/share/Steam/steamapps/common/Counter-Strike Global Offensive/game/csgo/maps}}"
vpk="$vpkdir/$map.vpk"
[ -f "$vpk" ] || { echo "no VPK at $vpk" >&2; exit 1; }

repo="$(cd "$(dirname "$0")/.." && pwd)"
dll="$repo/src/Cli/bin/Release/net10.0/SmokeSolver.Cli.dll"
[ -f "$dll" ] || { echo "build the CLI first: dotnet build src/Cli -c Release" >&2; exit 1; }

# The raw export also drops hundreds of loose PNGs beside its output; a scratch
# dir keeps them out of data/ and cleans them up on exit. It defaults to $HOME
# rather than $TMPDIR because a raw export is hundreds of MB of GLB plus loose
# textures - more than a RAM-backed /tmp holds ("Disk quota exceeded"). Point
# GLB_BUILD_TMP at any dir on a roomy disk to override.
work="$(mktemp -d -p "${GLB_BUILD_TMP:-$HOME}" .glb-build.XXXXXX)"
trap 'rm -rf "$work"' EXIT

echo "==> [$map] export from VPK"
dotnet "$dll" exportgltf --vpk "$vpk" --out "$work/$map.raw.glb"

echo "==> [$map] fix prop scale"
node "$repo/rig/fix-prop-scale.mjs" "$work/$map.raw.glb" "$work/$map.fixed.glb"

echo "==> [$map] optimize"
node "$repo/rig/optimize-textured-glb.mjs" "$work/$map.fixed.glb" "$repo/data/${map}_textured.glb"

echo "==> [$map] done -> data/${map}_textured.glb ($(du -h "$repo/data/${map}_textured.glb" | cut -f1))"
