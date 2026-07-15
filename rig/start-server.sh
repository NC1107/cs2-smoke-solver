#!/bin/bash
# Launch the pinned CS2 dedicated server for calibration. Paths come from
# rig.env; the Steam client's bin dir is on the library path because the
# dedicated build lacks a few shared objects (libv8.so and friends).
set -euo pipefail
RIG_ENV="$(dirname "$(readlink -f "$0")")/rig.env"
set -a; source "$RIG_ENV"; set +a

# Overridable like CS2_MAPS_DIR in build-textured-glb.sh: the standard Steam
# path is only a default, not an assumption.
CLIENT_BIN="${CS2_CLIENT_DIR:-$HOME/.local/share/Steam/steamapps/common/Counter-Strike Global Offensive/game/bin/linuxsteamrt64}"
cd "$CS2_RIG_DIR/server/game/bin/linuxsteamrt64"
export LD_LIBRARY_PATH="$CS2_RIG_DIR/server/game/bin/linuxsteamrt64:$CS2_RIG_DIR/server/game/csgo/bin/linuxsteamrt64:$CLIENT_BIN:${LD_LIBRARY_PATH:-}"
export SMOKESOLVER_CALIB_DIR

# flatgrass (the map every flight constant was measured on) ships as a
# community addon, which lives outside the normal map search path - without
# -addon a changelevel to it is silently ignored.
ADDON_ARGS=()
if [[ -n "${CS2_ADDON:-}" ]]; then
  ADDON_ARGS=(-addon "$CS2_ADDON")
fi

exec ./cs2 -dedicated -insecure -port "$CS2_SERVER_PORT" "${ADDON_ARGS[@]}" \
  +sv_lan 1 +map "${CS2_START_MAP:-de_dust2}" +sv_hibernate_when_empty 0 +exec server.cfg
