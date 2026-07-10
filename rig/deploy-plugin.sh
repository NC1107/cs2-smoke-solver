#!/bin/bash
# Build and deploy the CalibrationThrower plugin as one atomic unit (dll,
# pdb, deps, runtimeconfig), sync server.cfg, then hot-reload via the
# request channel. Partial hand-copies left mismatched file sets before.
set -euo pipefail
RIG="$(dirname "$(readlink -f "$0")")"
set -a; source "$RIG/rig.env"; set +a
export PATH="$HOME/.dotnet:$PATH"

cd "$RIG/CalibrationThrower"
dotnet build -c Release -v q

SRC=$(ls -d bin/Release/net*/ | head -1)
DEST="$CS2_RIG_DIR/server/game/csgo/addons/counterstrikesharp/plugins/CalibrationThrower"
mkdir -p "$DEST"
rsync -a --delete-after \
  "$SRC/CalibrationThrower.dll" "$SRC/CalibrationThrower.pdb" \
  "$SRC/CalibrationThrower.deps.json" "$SRC/CalibrationThrower.runtimeconfig.json" \
  "$DEST/" 2>/dev/null || cp "$SRC"/CalibrationThrower.{dll,pdb,deps.json,runtimeconfig.json} "$DEST/"

cp "$RIG/server.cfg" "$CS2_RIG_DIR/server/game/csgo/cfg/server.cfg"

if pgrep -f "cs2 -dedicated" > /dev/null; then
  PYTHONPATH="$RIG" python3 -c "import calibipc; calibipc.send({'cmd': 'css_plugins reload'})"
  echo "deployed and hot-reloaded"
else
  echo "deployed (server not running; will load on next start)"
fi
