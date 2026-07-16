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
  Fixed (`rig/fix-prop-scale.mjs`, task #32). Every duplicated small prop that had a sibling at the normal scale (soda cups, bottles, cans, buckets, chairs, paint cans, coffee mugs, clay pots, crates, the ceiling fan) got reset to its siblings' consistent scale.
- **Giant curtains, a giant stool, a giant blue panel (a TV).**
  Fixed. These are one-off props with no sibling to compare against, so the median pass never saw them - they came out at 58m, 34m and 32m respectively. Caught by the rendered-size pass described at the bottom of this doc, along with 11 more nobody had spotted (a 13m shoe, a 10m spray paint can, and so on).

## de_inferno

- **This is the Wingman version of inferno, it blocks players off at A site.**
  Investigated, not fixed yet. `de_inferno.vpk` is the correct, single map file - confirmed via its own entity data containing `terrorist_wingman_intro`/`counterterrorist_wingman_intro` spawn entities, meaning Valve bundles both the Wingman and full Defusal layouts into one file and only enables/disables the A-site-blocking brush via in-game entity I/O based on game mode.
  Our static VRF export doesn't run that entity logic, so the Wingman-only blocker renders unconditionally - same underlying class of problem as the Retake-mode markers above, but the current entity extraction (`data/de_inferno.entities.json`) doesn't capture entity I/O/outputs, so the specific blocking brush can't be identified from data alone yet.
  Need the in-game (x, y, z) of the block, or the name of the room/callout, to pinpoint and exclude the right node - can add it to the same node-filter mechanism once identified, no data regeneration needed.
  **Update 2026-07-16 - the identifier is now known.** The signal is `startdisabled=1` on the entity: Wingman enables these; Defusal never does (verified by scanning every entity's I/O connections - zero outputs target the blockers). Inferno has 24 `[PR#]brush.blocker` `func_brush` entities flagged `startdisabled=1`, clustered around A (origins near (455,592), (760,-680), (2338,1552), etc.). The collision side is already fixed: extraction now skips `startdisabled=1` solid entities, dropping 296 triangles from `de_inferno.s2geo`. The textured GLB is still to do - VRF ignores `startdisabled`, so the visual A-site blocker remains; the node-drop can now be keyed to the enumerated `startdisabled=1` entity origins/models rather than a hand-found (x,y,z).
- **Giant yellow soap bottle and giant fan.**
  Fixed. Same scale bug as Mirage's props, but each is the *only* instance of its mesh on this map, so there was no sibling to compare against - required an explicit, human-confirmed correction (dish soap 6.39 -> 0.16, ceiling fan blades 25.3 -> 0.64) rather than the automatic sibling comparison.

## de_nuke

- **Misc walls, wrong texture on the B bomb silo and its concrete.**
  Fixed. Root cause: `materials/dev/reflectivity_30.vmat`, a lighting-checker debug texture, was not caught by the old tools-material filter (same root cause class as Mirage's tape, see above).
- **The giant wall on A on nuke might be another prop bug.**
  Fixed. Two one-off props were rendering at 74m and 77m (`nuke_vent_bombsite_breakable_c` and `nuke_vent_slats`), correcting to 1.88m and 1.95m. The vent_bombsite one is the same prop an earlier draft of the fix gave up on - with only 2 instances there was no majority to vote against it, and averaging the pair produced a number matching neither. Measuring its rendered size settles it without needing a sibling at all.
- **Looks like a giant radiator prop bug and a red wall in the middle of a site.**
  "Radiator" fixed: it's actually `airduct_hvac_001`, an HVAC duct prop (`metal_door_001_br`, a related duplicated prop, was the one actually caught - both are part of the same general fix pass, task #32).
  Red wall fixed. Root cause: `models/ui/retakes/retakes_blocker.vmat` - a solid red, textureless Retake-game-mode wall (same class of bug as Mirage's clock tape and Anubis's black wall, just a third vmat path prefix, `models/ui/`, not caught until now). Filter broadened to catch this prefix too.
- **Ramp and silo doors render as closed doors (Wingman 2v2-only geometry).**
  Not fixed - visual only, the collision is already correct.
  Reported 2026-07-16 (Nick's Images #6/#8): a rollup door at the top of the ramp stairs and the silo doorway show as solid doors.
  These are `prop_dynamic` props tagged `[PR#]props.2v2` with `startdisabled=1`: `rollup_door_001_base_192` at (1246, -2368, -416) and two `nuke_industrial_silo_door_001` at (296, 86, -416) and (984, 80, -416).
  In Defusal they are disabled (never spawned); Wingman enables them to wall off the route.
  Their co-located collision blockers (`startdisabled=1` `func_brush`) were removed from `de_nuke.s2geo` on 2026-07-16, so the sim does not block smokes there - but VRF's `GltfModelExporter` bakes every entity into the GLB regardless of `startdisabled`, so the textured view still draws the doors.
  Fix: drop these nodes (by model path or world position) in the node-filter step, keyed to the `startdisabled=1` entity list.
- **B site: an animated blue water/caustic texture covers the ceiling and upper walls.**
  Not investigated yet.
  Reported 2026-07-16 (Nick's Image #7): the water shader surface appears mapped onto geometry high above the floor pool.
  Distinct from the flat-white water case fixed on Ancient/Inferno/Nuke: that case had no diffuse map at all, this one shows a caustic texture, so it took a different code path in `textured-scene.js`.
  Needs a look at the GLB material/geometry at the B-site ceiling to tell whether it is a mis-placed water-volume face or a mis-assigned material.

## de_overpass

- **Giant wall in monster area, actually looks like a giant turbine or something, along with an orange tie.**
  Turbine fixed: `hvac_fanblade_spinning_01` (two instances, 25.408 and 0.529 scale - corrected the outlier to 0.645, cross-checked against inferno's independently-confirmed fan blade fix landing in the same range). The "orange tie" is now identified and fixed: `construction_safetyribbon_01`, an orange construction ribbon rendering at 34m, corrected to 0.85m. A 111m door (`metal_door_112`) turned up in the same pass; nobody had reported it.
- **Connector/B route walled off by a Wingman (2v2-only) door and blockers.**
  This is the connector "wall" that was stopping smokes through the top of connector (Nick's original report).
  Collision fixed 2026-07-16: the `[PR#]brush.blocker` `func_brush`/`func_clip_vphysics` (all `startdisabled=1`) that sealed the B route were removed from `de_overpass.s2geo` (-532 triangles), so the sim no longer blocks smokes there.
  Textured GLB still renders the co-located Wingman door prop and any visual blocker; same node-drop fix as nuke's doors, keyed to the `startdisabled=1` entity list.

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
- **Giant flag.**
  Fixed. `clothes_b` was rendering at 256m. It's the clearest confirmation of the root cause found anywhere: the map holds a second, un-bugged instance of the same cloth at 6.50m, and 256.09 / (1/0.0254) = 6.50 exactly.

## Not map-specific

- **Free camera "down" control didn't match CS2's spectator free-cam.**
  Fixed. Added Ctrl (left/right) as a down key alongside the existing Q/C, matching Space already being up.
  - for some reason holding control and doing this crashes the window? oh its because im hitting ctrl + w to move around which closes tab
  - Fixed too: the browser was intercepting Ctrl+W as "close tab" before our code ever saw it, since none of the flight keys called `preventDefault()`. Now all of them do.

- **One-sided "false walls": a wall visible from inside a room but see-through from the far side.**
  Fixed 2026-07-16 (reported on secret and vents).
  Map walls are compiled one-sided (backfaces culled in-engine for perf), so from behind they vanished.
  The textured view now renders opaque materials `THREE.DoubleSide` (`textured-scene.js`), so a wall shows from both sides; translucent water/glass keep their original side to avoid depth-sort artifacts.

- **Wingman (2v2) geometry rendering in Defusal - the general pattern.**
  Several map findings above (nuke ramp/silo doors, overpass connector, inferno A blocker) are the same class: geometry that only exists in Wingman, walling off routes the 2v2 layout removes.
  The signal is `startdisabled=1` on the entity, verified reliable by there being zero entity-I/O outputs that enable them (Defusal never turns them on; Wingman does).
  Collision is handled: `MapExtractor` skips `startdisabled=1` solid entities, so the sim stops bouncing smokes off Wingman walls (overpass -532, inferno -296, nuke -96 triangles).
  The textured GLB is the open half: VRF's `GltfModelExporter` bakes every entity in regardless of `startdisabled`, so the doors/blockers still render.
  Pending fix: extend the node-filter (drop by model path / world position) using the enumerated `startdisabled=1` entity list, which needs a GLB rebuild + rsync + Cloudflare purge for the affected maps.

## Scale bug fix (task #32)

`rig/fix-prop-scale.mjs` checks all 7 maps. The bug is always the same factor - 1/0.0254, a metres-to-inches conversion applied one time too many - and it turned up on mirage, inferno, nuke, overpass and anubis. Only dust2 and ancient are clean.

The pass that catches props with 3+ instances (replace any outlier with the group's median scale) was right the first time and hasn't changed. Getting the *rest* of them right took several bad drafts, all of which are worth recording because each failed for a different reason:

- A first version tried to auto-detect any implausibly-large one-off prop by looking at `node.scale` alone. On inferno it flagged 4500+ nodes - essentially every wall, roof and vehicle on the map. The mistake was treating `node.scale` as if it meant "how big this is", when it means nothing on its own: a mesh authored large legitimately carries a small scale, and vice versa.
- Retreating from that, singleton props were only ever touched via an explicit, human-confirmed allowlist. Safe, but it only ever fixed what somebody had already noticed by eye, and it quietly missed anubis's flag (rendering at 256m), overpass's 111m door, and nine more on mirage - which is how this doc came to claim anubis was clean when it wasn't.
- Matching that allowlist by *material* rather than by node corrected a perfectly fine mesh (the ceiling fan's housing) along with the broken one (its blades), since both use the same shared kit material.

What actually works is measuring the thing the bug is visible in: **mesh extent x node scale**, which is a real size in metres. A prop is not 10 metres across. Across all 7 maps the largest genuine prop in this class is ~6.6m and the smallest bugged one is ~10m, and every bugged one lands back in the 0.2-6.5m range once divided by the conversion factor - anubis's flag corrects to 6.50m, which is exactly the size of its own un-bugged sibling. Props with 3+ instances are never judged this way, which is what keeps overpass's genuinely 22m subway train (3 instances, all agreeing) out of it. One prop needs an explicit exception: ancient's tallest waterfall really is ~11.8m, and it's a singleton, so there's nothing to prove it against.

Ordering matters now: `fix-prop-scale.mjs` **must** run before `optimize-textured-glb.mjs`. The optimizer flattens node transforms into the vertex data, and once that's happened the per-instance scale this reads no longer exists and the bug is baked in for good.