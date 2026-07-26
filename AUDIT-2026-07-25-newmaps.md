# Audit 2026-07-25: new-map cleanup pass

Self-directed audit across all 15 maps (7 original + the 8 competitive/premier maps added this week), looking for oddities left behind by onboarding.
Findings below; items 1 and 4 have since been fixed (see the "Fixed" note under each), the rest are documented as non-issues or deliberately deferred.

## 1. cs_italy's textured 3D preview is badly corrupted (confirmed, severe) - FIXED

`rig/fix-prop-scale.mjs` is misfiring on cs_italy specifically, shrinking real building geometry down to ~2.5% scale.
Visually confirmed: the textured 3D view, which should look like connected building walls and rooftops (compare `de_dust2`'s clean render), instead shows the map disintegrated into hundreds of small disconnected floating fragments with huge gaps where whole wall/roof sections used to be.

Root cause: the script's `isWorldGeometry()` guard is a regex, `^n\d+_lr\d+`, meant to exclude the map's own baked terrain/world-brush nodes from the "oversized single-instance prop" correction.
The actual glTF node names for cs_italy's aggregate world-geometry fragments look like `node000_world_lr0_agg406_196_italy_roof_trim_1` - the regex requires a digit immediately after `n`, so `node...` never matches.
Every other map's real per-instance props (crates, chairs, radios, trees) get corrected correctly; only cs_italy has enough individually-unique aggregate fragments exceeding 8m (wide plaster walls, long roof trims, sidewalk runs) to trip the false-positive path at scale.

Verified two ways:
- The onboarding log shows 5,559 `[oversized]` corrections for cs_italy, 5,493 of which hit `nodeNNN_(world|model)_lrN_aggN_...`-named fragments (i.e. real world geometry, not props). Every other new map's corrections are 100% genuine, distinctly-named props (0 aggregate-pattern hits each).
- The deployed `cs_italy_textured.glb` has 381 of 1,545 nodes (24.7%) at ~0.0254 scale where the name matches the aggregate pattern; a quick scan of scale magnitude alone is not a reliable signal by itself (0.0254 is the normal, correct per-node scale for legitimate props too - it's the metres-to-inches conversion the viewer expects), so the name-pattern cross-check is what makes this conclusive.

Not affected: the actual collision mesh / solver (`.s2geo`) and gameplay lineups - this bug lives entirely in the separate textured-GLB visual pipeline.
cs_italy's solves and 2D radar are unaffected.

**Fixed**: `isWorldGeometry()` in `rig/fix-prop-scale.mjs` now also excludes `_world_lr\d+_agg\d+`-named nodes.
Deliberately narrower than "contains `_agg`": a placed vehicle prop's own internal LOD/submesh split is named `_model_lr\d+_agg\d+` (confirmed on cs_italy's bicycles and vespa, which really did need the 39x correction and still get it), so only the `_world_` variant - never used for an individually-placed prop, only for a worldnode's shared aggregate batch - is excluded.
Verified against the onboarding log: of cs_italy's 5,559 corrections, 5,270 were `_world_...agg` (the bug) and 223 were `_model_...agg` (genuine bicycle/vespa/chair fixes, correctly preserved).
Re-ran the full `exportgltf -> fix-prop-scale -> optimize-textured-glb` pipeline plus the mobile tier for cs_italy and confirmed visually: the textured 3D view now renders as connected buildings, not floating fragments.

## 2. Cache/vertigo's blocky 2D radar is unchanged - same known limitation, not a new bug

Re-screenshotted both maps' 2D radar after the static-prop collision fix landed.
Both still render as coarse rectangular blobs (compare `de_dust2`'s crisp radar with real wall thickness and door gaps).
This is expected, not a regression: the radar is drawn from the actual collision mesh, and cache/vertigo's fine architectural detail is overwhelmingly `AggregateSceneObjects` (batched props with no recoverable collision - the already-documented, still-unsolved limitation from this week's static-prop fix).
The fix that shipped this week only recovers *individually-placed* props (debris, lamps), which is a small fraction of these two maps' detail.
No action taken; flagging so the blocky radar isn't mistaken for something the recent fix should have addressed.

## 3. `de_anubis` (and `de_dust2`, `de_mirage`) report zero triangles in the new `EntitySolid` bucket - investigated, not a bug

`dotnet run -- info` on `de_anubis.s2geo` shows `EntitySolid: 0 triangles`, which looked like a possible extraction miss next to cache's 126,524.
Traced with temporary debug instrumentation in `AppendStaticProps` (reverted, not committed): de_anubis has 244 individually-placed `SceneObjects`, but all 244 are auto-generated per-worldnode shadow/overlay/lightshaft meshes (`.../worldnodes/n0_lr0_..._blocklight*`, `..._overlay*`, `models/anubis/lightshaft.vmdl`) that correctly carry no physics.
de_dust2 and de_mirage are the same story.
These three maps' real decorative props are 100% inside `AggregateSceneObjects`, so the individually-placed-prop fix genuinely has zero material to recover there - this is map-data variance (how each map's compiler happened to batch geometry), not an extraction bug.

## 4. `TargetSolver.cs`'s voxel grid Z lower bound isn't clamped to the target - looked at, deliberately left alone

`min.Z` is set directly to `meshMin.Z` in three places (`TargetSolver.cs:82`, `91`, `141`), with no lower clamp relative to the target the way `max.Z` is clamped to `target.Z + 900` (line 98).
Found while investigating why `cs_shelter`'s reported map bounds span Z = -14400 to +14400 (a 28,800-unit range vs. the usual ~1,500-3,000).

Traced the extreme bounds to two tiny 12-triangle, 128x128-unit marker clusters at the extreme top and bottom (X/Y centered near world origin), tagged `conditionallysolid`.
Since production's collision filter (`PlayerSolidFilter`/`GrenadeSolidFilter`, and the `--attrs "Default,default,EntitySolid"` used everywhere) explicitly excludes `ConditionallySolid`, these markers are never actually solid - they cannot be snapped to as a floor and never appear in the exact-triangle collider players/grenades interact with.
Measured the actual cost: building the affected voxel grid with the real (unclamped) Z range took 35ms vs. 19ms clamped to a sane +/-1500 band - negligible next to a 45-60s full solve.
`de_boulder` has two similar small junk clusters (2-4 triangles each, also `conditionallysolid`).

Net: cosmetic bounds inflation and a few extra milliseconds per solve, nothing functionally wrong today.

Considered mirroring the max.Z clamp (e.g. `MathF.Max(meshMin.Z, target.Z - 900)`) and decided against it: a fixed margin like that would silently exclude real, legitimate origins on genuinely vertical maps during a full map-wide search.
de_nuke's real Z range alone is -1216 to 808 (a ~2000-unit span), and a map-wide query's origin search box covers the whole map in X/Y, so a target near the top with valid origins near the real bottom is a normal case, not an edge case - a same-sized margin below the target that happens to work for cs_shelter's stray triangles would cut those off.
Fixing this properly would mean trimming the two junk clusters at the extraction source (or detecting genuinely disconnected micro-islands) rather than papering over it with a magic number in the solver, and that's a separate, higher-risk change for a problem that is currently provably harmless (zero effect on any real query since the triangles are non-solid by attribute already).
Left as-is; not worth the risk for no measured benefit.

## 5. Crates and ledges the nav mesh omits were never usable as throw positions - FIXED

Raised separately by Nick ("some ledges and boxes aren't considered player-standable by the solver").
Confirmed and root-caused: Valve authors the nav mesh for bot pathing, bots never jump onto anything, so crates/platforms/ledges carry no nav area - and origin generation reads nav areas exclusively (`TargetSolver` is nav-only; the raw-geometry `FindStandableOrigins` is a CLI-only fallback), so no lineup could ever be thrown from one.

Measured across all 14 maps: 19-27 surfaces per map are genuinely standable (player-solid floor, hull-sized footprint, full headroom) and reachable by a jump from walkable ground, yet produce no origin.
Two hand-verified cases: `de_dust2 [-1413,2852]` is standable at z=47 while the nearest origin master produces is z=8 (the floor, 45u away), and `de_mirage [-2192,-672]` is standable at z=-8 with the nearest origin 128u below it.

Fixed by adding those surfaces, anchored to each nav area and gated on being within jump height of it - the gate preserving the anti-rooftop property the nav-only design existed for.
A/B over 24 real callout queries: three gained lineups (dust2 OutsideTunnel 43->48, dust2 TRamp 11->12, inferno Middle 23->28), two lost one each to ranking churn in the capped list, and 11 of the 12 newly-reachable stand spots sit at an XY the nav mesh does not cover at all.
Cost is +3.7% to +9.5% origins map-wide.

## Not investigated further (flagging, not chasing)

- **`de_dust2` map-wide solves return zero lineups** for several targets tried (`[1098,2554]` BombsiteA, `[-1757,2593]` BombsiteB), while `de_cache` returns 300+ for the same shape of query.
  Reproduced identically on master, so it is NOT caused by any change in this audit - but it looks wrong and is worth its own investigation.
  Origin-clicked dust2 queries do return lineups normally, so this appears specific to the map-wide path or to those targets' resolved ground Z.

- `cs_office`'s 5.7% nav-vs-collision gap rate (vs. 0.1-1.7% on every other map) remains an unexplained outlier from the earlier audit; no time spent on it this pass.
- The aggregate-prop collision problem itself (root cause of #2) is unchanged from what was already documented as an open research question with no known extraction path.
