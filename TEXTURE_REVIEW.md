# Textured 3D View - Map Review

Tracking doc for visual review of the "Textured" 3D view across all 7 Active Duty maps.
Nick adds findings as he reviews each map; findings get a status once investigated/fixed.

## de_dust2

Not yet reviewed in this pass (was extensively reviewed earlier in the project - coordinate alignment, VFX/alpha-cutout, free-look camera all confirmed working).

- Note: `materials/dev/black_simple.vmat` (the same debug material that caused Anubis's black wall, see below) is present in this map's GLB too.
  Not yet confirmed whether it was visible before the filter fix below, or hidden in a spot nobody looked at.

## de_mirage

- **Clock/timer tape at both A and B sites.**
  Fixed. Root cause: `materials/tools/wrongway_timer.vmat` (CS2 Retake-mode "Wrong Way" site marker, not real world geometry) was not caught by the old tools-material filter, which only checked the material's display name, not its file path.
- **Giant ceiling fans, giant box/crack, a "block rock", a giant bush.**
  Fixed (`rig/fix-prop-scale.mjs`, task #32). 14 outlier nodes corrected - every duplicated small prop that had a sibling at the normal scale (soda cups, bottles, cans, buckets, chairs, paint cans, coffee mugs, clay pots, crates, the ceiling fan) got reset to its siblings' consistent scale. Operates directly on the already-optimized GLB (no VPK re-export needed) since node scale survives quantize()/draco() untouched.

## de_inferno

- **This is the Wingman version of inferno, it blocks players off at A site.**
  Investigated, not fixed yet. `de_inferno.vpk` is the correct, single map file - confirmed via its own entity data containing `terrorist_wingman_intro`/`counterterrorist_wingman_intro` spawn entities, meaning Valve bundles both the Wingman and full Defusal layouts into one file and only enables/disables the A-site-blocking brush via in-game entity I/O based on game mode.
  Our static VRF export doesn't run that entity logic, so the Wingman-only blocker renders unconditionally - same underlying class of problem as the Retake-mode markers above, but the current entity extraction (`data/de_inferno.entities.json`) doesn't capture entity I/O/outputs, so the specific blocking brush can't be identified from data alone yet.
  Need the in-game (x, y, z) of the block, or the name of the room/callout, to pinpoint and exclude the right node - can add it to the same node-filter mechanism once identified, no data regeneration needed.
- **Giant yellow soap bottle and giant fan.**
  Fixed. Same scale bug as Mirage's props, but each is the *only* instance of its mesh on this map, so there was no sibling to compare against - required an explicit, human-confirmed correction (dish soap 6.39 -> 0.16, ceiling fan blades 25.3 -> 0.64) rather than the automatic sibling comparison.

## de_nuke

- **Misc walls, wrong texture on the B bomb silo and its concrete.**
  Fixed. Root cause: `materials/dev/reflectivity_30.vmat`, a lighting-checker debug texture, was not caught by the old tools-material filter (same root cause class as Mirage's tape, see above).
- **The giant wall on A on nuke might be another prop bug.**
  Likely fixed as part of the general scale-bug pass below, not individually confirmed live.
- **Looks like a giant radiator prop bug and a red wall in the middle of a site.**
  "Radiator" fixed: it's actually `airduct_hvac_001`, an HVAC duct prop (`metal_door_001_br`, a related duplicated prop, was the one actually caught - both are part of the same general fix pass, task #32).
  Red wall fixed. Root cause: `models/ui/retakes/retakes_blocker.vmat` - a solid red, textureless Retake-game-mode wall (same class of bug as Mirage's clock tape and Anubis's black wall, just a third vmat path prefix, `models/ui/`, not caught until now). Filter broadened to catch this prefix too.
- **Open, unresolved**: `nuke_vent_bombsite_breakable_c` has exactly 2 instances (37.000 and 0.940 scale) with no third sibling and no other-map cross-reference to determine which is correct - a first attempt auto-corrected this by averaging the pair, which was wrong (matched neither value), so it's been left at its original, unmodified values rather than guessed at. If this looks wrong in the viewer, that's this exact node - let me know which of the two looks right (or if both look wrong) and I can fix it directly.

## de_overpass

- **Giant wall in monster area, actually looks like a giant turbine or something, along with an orange tie.**
  Turbine fixed: `hvac_fanblade_spinning_01` (two instances, 25.408 and 0.529 scale - corrected the outlier to 0.645, cross-checked against inferno's independently-confirmed fan blade fix landing in the same range). The "orange tie" is still unidentified - no separate orange/wrong-texture material found on this map beyond the already-fixed glass category, so it may have been part of the same turbine mesh and already resolved, or may need a fresh look.

## de_ancient

- **Water renders as flat white.**
  Fixed, and generalized to every map with water (Inferno and Nuke also use the same water shader per the GLB's own material data).
  Root cause: the water shader (`csgo_water_fancy.vfx`) is fully procedural (reflection/refraction/caustics computed at runtime) and carries no static diffuse texture at all, so it fell back to the renderer's default white.
  Now tinted using the water material's own `g_vWaterFogColor` art-direction param when present, so each map's water keeps its own color (e.g. Ancient's muddy jungle tone) instead of one generic blue.
- Also has `materials/dev/reflectivity_50b.vmat` (same debug-texture category as Nuke's silo bug) - covered by the same fix, not separately confirmed live yet.

## de_anubis

- **Giant black wall thing on B site.**
  Fixed. Root cause: `materials/dev/black_simple.vmat`, a level-wide debug placeholder present on every map checked (same root cause class as Nuke's and Ancient's dev-material bugs above).
- **Some wall textures look like water reflection textures.**
  Fixed (pending your confirmation). Root cause: almost certainly the glass shader (`csgo_glass.vfx`), which - like water - is procedural with no diffuse texture and was rendering as flat opaque white. Now tinted translucent pale blue instead.

## Not map-specific

- **Free camera "down" control didn't match CS2's spectator free-cam.**
  Fixed. Added Ctrl (left/right) as a down key alongside the existing Q/C, matching Space already being up.
  - for some reason holding control and doing this crashes the window? oh its because im hitting ctrl + w to move around which closes tab
  - Fixed too: the browser was intercepting Ctrl+W as "close tab" before our code ever saw it, since none of the flight keys called `preventDefault()`. Now all of them do.

## Scale bug fix (task #32)

`rig/fix-prop-scale.mjs` checked all 7 maps directly, not just the 4 reported above. dust2, ancient, and anubis had none of this bug present - only mirage, inferno, nuke, and overpass did, all covered above.
Worth recording since it wasn't obvious going in: an automatic fix for a "few instances way too big" pattern is easy to get subtly wrong, and this one went through a few bad drafts before landing:
- A first, broader version tried to auto-detect *any* implausibly-large one-off prop (not just ones with a normal-sized sibling to compare against) - on inferno alone it flagged 4500+ nodes, essentially every large wall/roof/vehicle mesh on the map, since ordinary world geometry legitimately spans the same size range for unrelated reasons. Dropped entirely; singleton props are now only ever touched via an explicit, human-confirmed list.
- Matching that confirmed list by *material* rather than by the specific node initially corrected a perfectly fine mesh (the ceiling fan's base/housing) right along with the actual broken one (its blades), since both use the same shared kit material. Fixed to match by exact node name instead.
- For props with exactly 2 instances, there's no real statistical majority to check against - averaging the pair (nuke's `nuke_vent_bombsite_breakable_c`) produced a value that matched neither original number. That one is left unresolved above rather than guessed at; overpass's fan blade had enough outside corroboration (matching inferno's independently-fixed fan blade) to correct with confidence instead.