# CS2 Smoke Solver

Computes CS2 smoke grenade volumes against real map collision geometry.
Goal: given a target sightline, find every throw whose smoke blocks it.
See `DESIGN.md` for the architecture and roadmap.

## Status

- Phase 0 (extraction), phase 1 offline (voxelizer, flood fill, occlusion), and first cuts of phase 2 (trajectory sim) and phase 3 (choke-seal zone + lineup solver) work end-to-end on Dust 2.
- First manual calibration landed: bounce elasticity 0.15 fitted against a real mid-doors jumpthrow (rest error 26u); remaining constants are placeholders, so keep verifying lineups in-game.
- Lineups are solved against the voxel model, then re-simulated against exact collision triangles under small aim perturbations; the agreement fraction ships as a per-lineup stability score.
  The exact model traces a point and snags on thin trim a real (spherical) grenade rolls over, so it annotates rather than vetoes; sphere-swept collision is the planned upgrade that makes it a hard gate.
- A local web viewer (phase 4 first cut) renders the map, zone, sightlines, and lineups.

## Requirements

- .NET 10 SDK
- A local CS2 install (for `extract` only; the `.s2geo` output is self-contained)

## Usage

```bash
# One-time per map + game update: pull collision geometry and entities from the game files.
dotnet run --project src/Cli -- extract \
  --game ~/.local/share/Steam/steamapps/common/"Counter-Strike Global Offensive" \
  --map de_dust2 --out data

# Inspect a geometry file.
dotnet run --project src/Cli -- info --geo data/de_dust2.s2geo

# Simulate a smoke at a rest point (--obj dumps the volume for Blender).
dotnet run --project src/Cli -- smoke --geo data/de_dust2.s2geo \
  --rest "-450,1950,96" --conservative --attrs "Default,default"

# Does a smoke at --rest block the sightline --from -> --to?
dotnet run --project src/Cli -- sightline --geo data/de_dust2.s2geo \
  --from "-441,1950,-48" --to "-370,800,0" --rest "-441,1500,-100" \
  --conservative --attrs "Default,default"

# Stage 1 inverse solve: every landing cell whose smoke blocks the sightline.
# Example: the Dust 2 mid-doors gap (CT eye at the gap, T crossing mid).
dotnet run --project src/Cli -- solve --geo data/de_dust2.s2geo \
  --from "-441,1950,-48" --to "-370,800,0" --conservative --attrs "Default,default" \
  --json data/middoors-zone.json --obj data/middoors-zone.obj

# Terrain probe: ground height along a 2D line (for picking eye coordinates).
dotnet run --project src/Cli -- ground --geo data/de_dust2.s2geo \
  --from "-450,2100" --to "-400,76" --steps 25 --attrs "Default,default"

# Stage 2: T-side lineups whose smoke seals the mid-doors choke.
# --to takes multiple targets (semicolon-separated): the smoke must block them all.
dotnet run --project src/Cli -c Release -- lineups --geo data/de_dust2.s2geo \
  --from "-441,1950,-48" --to "-370,800,50;-450,1000,20;-500,1200,-30" \
  --origins "-1450,-1150,150,350,-50,350" --types "stand,jump,runjump" \
  --conservative --attrs "Default,default" --top 15 --json data/viewer-solve.json

# Replay a getpos lineup through the sim (throw types: stand, jump, runjump).
dotnet run --project src/Cli -- throw --geo data/de_dust2.s2geo \
  --pos "-452.11,-660.06,175.36" --ang "-9.74,88.67" --type jump \
  --attrs "Default,default" --solve data/viewer-solve.json

# Local web viewer: one-time map dump, then serve viewer/ + data/ at localhost:8137.
# With --geo/--nav/--attrs the server also answers the viewer's interactive
# two-click queries (pick a landing target, pick a throw area, get lineups).
dotnet run --project src/Cli -- viewerdata --geo data/de_dust2.s2geo \
  --entities data/de_dust2.entities.json --region "-1500,-1200,300,2300" \
  --attrs "Default,default" --out data/viewer-map.json
dotnet run --project src/Cli -- serve --port 8137 --geo data/de_dust2.s2geo \
  --nav data/de_dust2.navareas.json --attrs "Default,default"
```

Add `--nav data/de_dust2.navareas.json` to `lineups` to restrict throw origins to nav-mesh walkable positions (extracted automatically by `extract`); without it, origins come from raw geometry and can include unreachable ledges.

## Calibration protocol

No custom test map is needed (CS2's Hammer tools are Windows-only anyway): the tool holds exact Dust 2 geometry, so its flat open areas are the measurement rig.

1. On the practice server, stand somewhere repeatable and run `getpos` (bind it to a key for speed).
2. Throw, walk onto the settled smoke's center, `getpos` again.
3. Append the pair to `data/throws.json`: `{"throw": "<first getpos line>", "type": "stand|jump|runjump", "strength": 1, "landing": "<second getpos line>"}` (strength: 1 left, 0.5 left+right, 0 right).
4. Coverage that pins the constants fastest: three flat-ground throws at roughly level, -20, and -40 pitch (full strength); one medium and one soft throw; one jumpthrow and one run-jumpthrow; one throw straight into a large flat wall.
5. `dotnet run --project src/Cli -- calibrate --geo data/de_dust2.s2geo --throws data/throws.json --attrs "Default,default"` fits all constants and writes `data/throw-constants.json`, which every command picks up automatically.

Avoid throws that cross the overhead beams and banners above mid: they are solid in the physics mesh but transparent to real grenades until the surface-property filter lands, so they poison the fit.

## Validating in CS2

On a local practice server (defusal maps take `mp_roundtime_defuse`, not `mp_roundtime`):

```
sv_cheats 1; mp_warmup_end; mp_roundtime_defuse 60; mp_freezetime 0; mp_restartgame 1;
sv_infinite_ammo 1; sv_grenade_trajectory 1; sv_autobunnyhopping 1; sv_enablebunnyhopping 1;
give weapon_smokegrenade
```

Each lineup in the viewer (and in the lineups JSON) carries a `setpos ...; setang ...` command.
Paste it, throw as described (stand / jumpthrow bind / W + jumpthrow), and compare the real trajectory and bloom against the predicted rest point.
Deviations are calibration data, not surprises: constants are placeholders until the calibration pass.

`--attrs "Default,default"` excludes `ConditionallySolid` clip volumes, which block rays but not vision in game.

## Tests

```bash
dotnet test
```
