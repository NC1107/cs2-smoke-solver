# Design: CS2 Inverse Smoke Solver

## Status: Draft

## Problem Statement

Finding smoke grenade lineups in Counter-Strike 2 is manual trial and error: players discover individual throws, memorize them, and share them on lineup sites.
This tool inverts the problem: given a target sightline on a map (validation case: Dust 2 mid doors), compute the full set of throw positions and view angles whose smoke ends up blocking that sightline.

Nobody has built this.
Existing tools (scope.gg grenade predictor, grenades.website, lineup videos) only catalog known forward throws; none solve the inverse query.

## Requirements

### Must Have

- Given a map and a target sightline definition, output the set of viable (origin, view angles, throw type) tuples whose settled smoke volume blocks that sightline.
- Results must be faithful to the real game: a reported throw, performed in-game, must actually block the sightline.
- Support the standard throw types: normal throw, lob (right click), medium (left+right click), and jump throw.
- Restrict throw origins to positions a player can actually stand on.
- Work fully offline after a one-time extraction and precompute step per map version.
- Validation case shipped end-to-end: Dust 2, T-side origins, mid-doors sightline.

### Nice to Have

- Web viewer (three.js) rendering the map, candidate landing zones, and a viable-throw heatmap.
- Running/walking throws (movement velocity added to grenade velocity).
- Ranking results by practicality (lineup stability, exposure of the throw position, time to bloom).
- Additional maps beyond Dust 2.

### Out of Scope

- Other grenade types (molotov, flash, HE).
- Real-time in-game overlay or any game client modification.
- Smoke interaction dynamics after settling (bullet holes, HE displacement, molly extinguish).
- Matchmaking or anti-cheat-sensitive contexts; the tool is an offline analysis aid.

### Assumptions

- Nick has CS2 installed locally and can run a practice server with `sv_cheats 1`, so game-in-the-loop calibration is available from day one.
- Grenade trajectories in CS2 are deterministic given origin, view angles, throw type, and tick stepping; this is the premise that makes lineups work at all.
- Map geometry and constants only change on game updates, so precomputed data can be keyed to a game build id and refreshed per update.

## Background: What the Research Established

- **Geometry extraction is solved.**
  [ValveResourceFormat](https://github.com/ValveResourceFormat/ValveResourceFormat) (Source 2 Viewer) is a mature C# library and CLI that parses CS2 VPK archives and exposes map assets, including the physics collision meshes.
  Smoke collides against physics hulls, not render geometry, so we consume the physics data programmatically via the library rather than using the glTF export (which targets render meshes).
  Verified against build 2000872: world physics lives as a PHYS block embedded in `maps/<map>/world_physics.vmdl_c` inside the map VPK (not a standalone `.vphys_c`), read via `Model.GetEmbeddedPhys()`; hull shapes need fan triangulation from their half-edge structure alongside the triangle meshes.
  The `.nav` file ships in the same VPK at `maps/<map>.nav`, and `env_cs_place` callout entities (MidDoors, TopofMid, ...) provide data-driven sightline anchor coordinates.
- **Walkable positions come from the nav mesh.**
  CS2 ships `.nav` files enumerating walkable areas.
  [awpy](https://github.com/pnxenopoulos/awpy) has a Python parser for them, but it has lagged behind nav format version bumps (v36 was unsupported for a while, awpy issue #485).
  We port the parsing logic to C# and own it, using awpy as the reference implementation.
- **Smoke is a voxel flood fill.**
  The smoke volume starts at the voxel cell where the grenade comes to rest, receives maximum density, and iteratively floods unblocked neighbor cells until a total fill budget is reached.
  Multiple public reimplementations document the algorithm (Roblox/Luau, UE5, Unity, DX11 recreations).
  The exact constants (voxel size, fill budget, max radius, settle timing) are not published and must be calibrated against the real game.
- **Trajectories are deterministic and simulatable.**
  A throw is a pure function of origin, view angles, throw type, and tick stepping.
  Constants to calibrate: initial speed per throw type, gravity (default `sv_gravity 800`), bounce restitution, drag, and the eye/hand offset of the release point.
  RESOLVED (2026-07-09): flight physics were measured directly from per-tick server telemetry (18,280 in-air tick pairs, 358 bounce events on cs_flatgrass) and cross-confirmed against the public Source SDK 2013 grenade code, replacing the earlier end-to-end fitted constants.
  The engine integrates once per 64/s tick: velocity gets full-tick gravity (scale 0.40, zero drag), position advances by the trapezoid of old/new velocity.
  A bounce is a pure reflection (overbounce 2.0) with STOP_EPSILON component snapping, scaled uniformly by elasticity 0.45 (no tangential friction term exists).
  FLOOR impacts (normal z > 0.7) faster than 689 u/s and steeper than 60 degrees additionally damp by (1.5 - cos impact angle); wall impacts never damp (0/122 gated wall bounces damped across the dust2 validation runs vs 68/76 ground bounces - the flatgrass batch could not constrain this because its wall hits were all below the speed gate).
  A floor impact whose post-bounce speed is under 19.685 u/s (0.5 m/s) stops dead instantly; no rolling phase exists.
  The grenade sweeps as a +-2 unit hull, not a large sphere.
  Replaying all 96 flatgrass calibration captures through the C# integrator gives median 1.7u / p90 4.7u / max 18u rest error; remaining de_dust2 error (median 22u) is collision-mesh fidelity, not physics.
- **The game itself provides ground truth.**
  A local server with `sv_cheats 1`, `sv_grenade_trajectory 1`, `cl_grenadepreview`, and `sv_rethrow_last_grenade`, driven by a [CounterStrikeSharp](https://github.com/roflmuffin/CounterStrikeSharp) plugin, can throw grenades programmatically and record rest points and smoke behavior.

## Proposed Architecture

### Overview

An offline C# simulation pipeline, calibrated game-in-the-loop, queried through a CLI.

```
CS2 VPKs + .nav ──► extraction ──► intermediate geometry (per map + build id)
                                        │
                                        ▼
                          sim (voxelizer, flood fill, ballistics)
                                        │
              calibration (CS2 server) ─┤  constants fitted against ground truth
                                        ▼
                                     solver ──► result set (JSON) ──► viewer (later)
```

### The Core Algorithm: Two-Stage Inverse Decomposition

Brute force (origins × yaw × pitch × throw types, each followed by a flood fill and occlusion test) is intractable and unnecessary.
The problem decomposes into two independent, cacheable stages:

**Stage 1: good landing zones.**
Enumerate candidate rest cells near the target sightline: voxel cells within the smoke max radius of the sightline segment whose below-neighbor is solid (a grenade must rest on something).
Flood fill each candidate and run the occlusion test.
Keep the cells whose smoke blocks the sightline; this set is the "good landing zone".
This is thousands of flood fills, each cheap, and depends only on (map, sightline).

**Stage 2: throws that land in the zone.**
Precompute a landing-point cache per map: for each sampled standable origin (from the nav mesh), sweep view angles per throw type, forward-simulate the trajectory, and store the rest cell.
A query is then a lookup: which cached (origin, angles, throw type) entries rest inside the good landing zone.
The cache is computed once per map version, coarse-to-fine (coarse angle grid first, refinement passes only around cells adjacent to good zones), with range pruning to skip origin/zone pairs beyond maximum flight distance.

### Conservative Bloom Mode

The sim supports two smoke volume models, selectable per query.

**Calibrated mode** reproduces the real flood fill using constants fitted by the calibration pipeline.

**Conservative mode** requires no calibration and relies on only two behaviors that hold regardless of the unknown constants: smoke never passes through solid geometry, and every smoke expands to at least a minimum extent observable from any in-game smoke.
The conservative volume is the set of cells flood-reachable from the rest cell (respecting walls and ceilings) within a deliberately underestimated minimum radius.
Because it is a strict underestimate of the real volume, any sightline it blocks is guaranteed blocked by the real smoke: full precision, reduced recall.
Conservative mode is the default until calibration lands, and remains available afterward as a "guaranteed to work" filter on solver results.

### Components

Planned repo layout, one project per component under a single .NET solution:

- **`src/Extraction`**: consumes the CS2 install directory via the ValveResourceFormat library.
  Dumps physics collision triangles (with collision group metadata) and parsed nav areas into the intermediate format.
  Records the game build id (from `steam.inf`) and an extraction schema version in the output header.
- **`src/Sim`**: the physics core, no game-file dependencies.
  Voxelizer: builds a solid-occupancy grid from the collision triangles at the calibrated voxel size and grid alignment.
  Smoke: BFS flood fill from a rest cell with fill budget and radius bound.
  Ballistics: fixed-step integrator for grenade flight (initial velocity from view angles and throw type, gravity, bounce with restitution against collision geometry, rest detection).
- **`src/Solver`**: implements the two stages above plus the occlusion test.
  Occlusion test: sample eye-position pairs across both ends of the sightline (a grid of positions, not a single ray), march each ray through the smoke density grid, and declare the sightline blocked when at least a configurable fraction of rays exceed an accumulated-density threshold.
  Geometry clearance of the rays is tested against exact collision triangles, never the voxel grid (ADR-006); rays the geometry already blocks are dropped rather than credited to the smoke.
- **`src/Calibration`**: a CounterStrikeSharp server plugin plus capture scripts.
  The plugin teleports a bot or host player to scripted origins, sets view angles, throws, and logs grenade entity positions per tick and the rest position via server events.
  Capture output feeds a fitting step that estimates the sim constants and reports residuals.
- **`src/Cli`**: single `smokesolver` binary with subcommands: `extract`, `precompute` (landing cache), `query` (sightline in, JSON result set out), `calibrate` (fit constants from capture logs).
- **`viewer/`**: three.js/TypeScript web app consuming the JSON results; explicitly a later phase.

### Data Model

- **Intermediate geometry file**: one binary blob per map, containing schema version, game build id, collision triangle soup with collision groups, and nav areas (polygon corners plus connectivity).
- **Landing-point cache**: per (map, build id, constants hash): compact records of origin, yaw, pitch, throw type, rest cell index.
  Invalidated automatically when the constants hash or build id changes.
- **Constants file**: checked-in, human-readable (JSON) record of all calibrated physics and smoke constants, with the calibration date, game build id, and fit residuals.
- **Query result**: JSON list of viable throws with origin, angles, throw type, rest point, and occlusion score, ready for the viewer or further filtering.

### Dynamic Geometry

Door-like entities change both the sightline and the flood-fill blocking, so the design treats dynamic prop states as a query parameter: extraction captures dynamic entities separately, and the voxelizer stamps them in or out per configured scenario.
Verified on Dust 2 build 2000872: the mid doors are static world geometry (no door entities exist in the entity lump), so the validation case needs no dynamic handling; the mechanism stays in the design for maps that do have functional doors.

### Collision fidelity for grenade flight (resolved 2026-07-09)

Grenade collision uses a per-attribute-group filter derived from the physics interaction layers stored in the map (m_InteractAsStrings), not from the ambiguous group names: player/NPC clips and sky volumes do not block grenades (validated against 301 real bounce events and observed fly-throughs on de_dust2), while Default/default, passbullets, and csgo_grenadeclip groups do.
Extraction additionally appends solid brush-entity models (func_brush, func_clip_vphysics, doors, breakables) from the map VPK, transformed by their entity origin/angles, under distinct attribute names (EntitySolid, EntityPhysicsClip) so grenade and sightline consumers can include them independently: func_clip_vphysics blocks grenades but not vision or bullets, and on de_dust2 it seals the mid-doors gap.
The grenade sweeps as the engine's +-2 unit box hull via an exact swept-AABB separating-axis test; the earlier radius-8 (and radius-2) sphere approximations missed edge contacts that box corners catch.
Replay of all captured real throws through the final model: cs_flatgrass median 1.3u / max 6.3u (96 throws); de_dust2 median 2.0u / p90 92u / max 235u (54 throws), where the remaining tail is confined to rooftop-edge grazes in one mid-area cluster whose 1-2 tick contact differences amplify through subsequent bounces.

## Alternatives Considered

### Option A: Offline C# sim, game-in-the-loop calibration (proposed)

Described above.
Fast interactive queries after precompute; accuracy maintained by calibrating against the real game per update.

### Option B: Pure game-in-the-loop search, no sim

Drive the real server to physically perform every candidate throw and observe results.
Perfect fidelity and zero physics reverse engineering, but throughput is bounded by real time: even at a few throws per second, the landing cache (hundreds of millions of throws) is unreachable, and even a single zone-targeted query would take hours.
Rejected as the primary engine, but its machinery is exactly the calibration component, so it is built anyway at small scale.

### Option C: Rust sim core, C# extraction shell

Maximum hot-path performance, but two toolchains, an FFI or interchange boundary, and no reuse of C# across extraction, sim, and the CounterStrikeSharp plugin.
Rejected: C# with structs, spans, and parallelism is fast enough for a precompute measured in hours, and simplicity wins.

### Option D: Python prototype on the awpy ecosystem

Fastest algorithm iteration, but the solver sweep would need a rewrite in a fast language later, and nav parser version lag becomes a runtime dependency risk.
Rejected as the product path; awpy remains the reference implementation for nav parsing logic.

## Trade-off Matrix

| Criterion | A: C# sim + calibration | B: pure game-in-the-loop | C: Rust core hybrid | D: Python prototype |
|-----------|------------------------|--------------------------|---------------------|---------------------|
| Query speed | Interactive after precompute | Hours per query | Interactive | Slow at solver scale |
| Fidelity | High, bounded by calibration | Perfect | High, bounded by calibration | High, bounded by calibration |
| Toolchain complexity | One language | One language + game ops | Two languages + FFI | Two ecosystems long-term |
| Robustness to game updates | Recalibrate per update | Immune | Recalibrate per update | Recalibrate + parser lag risk |
| Long-term maintainability | Good | Poor (throughput wall) | Moderate | Poor (rewrite pending) |

## Architecture Decisions

### ADR-001: C# end-to-end

**Context**: extraction (ValveResourceFormat) and calibration (CounterStrikeSharp) are both C# ecosystems; the sim needs to be fast but runs offline.
**Decision**: one .NET solution for extraction, sim, solver, calibration plugin, and CLI; the viewer is the only non-C# component.
**Consequences**: single toolchain and shared types across all pipeline stages; we accept that peak sim performance is below a native-language core and compensate with parallelism and the two-stage decomposition.

### ADR-002: Physics collision mesh, not render mesh, not heightmap

**Context**: smoke flood fill and grenade bounces resolve against collision hulls, which differ from visual geometry; a heightmap is 2.5D and cannot represent overhangs, tunnels, or doorways.
**Decision**: extract and simulate against `.vphys` physics collision data exclusively.
**Consequences**: correct blocking behavior in exactly the places smokes are interesting; requires programmatic use of the VRF library instead of its simpler glTF export path.

### ADR-003: Two-stage inverse decomposition with a per-map landing cache

**Context**: the naive inverse search multiplies origins, angles, throw types, flood fills, and occlusion tests into an intractable product.
**Decision**: stage 1 computes sightline-blocking landing zones (flood fills only near the target); stage 2 matches throws against zones using a precomputed, query-independent landing-point cache.
**Consequences**: queries become cache lookups plus a small refinement pass; we accept a large one-time precompute per map version and cache invalidation tied to constants and build id.

### ADR-004: Game-in-the-loop calibration as ground truth from day one

**Context**: the constants that determine fidelity (throw speeds, restitution, voxel size, fill budget) are unpublished, and community values drift with game updates.
**Decision**: build the CounterStrikeSharp capture plugin early and fit all sim constants against recorded real throws and smokes, re-running the fit after game updates.
**Consequences**: fidelity is measurable instead of assumed; we accept the operational cost of running a local CS2 server as part of the development loop.

### ADR-005: Own the nav parser in C#, ported from awpy

**Context**: awpy's nav parser is the best-documented reference but is Python and has historically lagged nav format version bumps.
**Decision**: port the parsing logic into `src/Extraction` and maintain it, tracking awpy for format changes.
**Consequences**: no Python runtime dependency and immediate fixability on format bumps; we accept maintaining a parser for an undocumented format.

### ADR-006: Vision rays use exact triangles, only smoke volume uses voxels

**Context**: the Dust 2 mid-doors gap is roughly 10 units wide; a 16-unit voxel grid seals it, falsely declaring the map's most famous sightline blocked (verified empirically: the gap sits at x = -441 and exact rays thread it while voxel rays cannot).
**Decision**: geometry clearance for sightlines is computed by segment-vs-triangle tests (Möller-Trumbore) against the collision mesh; the voxel grid is used only for smoke fill and smoke-crossing counts.
**Consequences**: sub-voxel sightlines are handled correctly at any smoke voxel size; the solver pays a per-query raycast cost, mitigated by region-filtering triangles to the query bounds.

## Implementation Plan

### Phase 0: Extraction

Extract Dust 2 physics collision geometry and nav areas into the intermediate format, keyed by game build id.
Deliverable: `smokesolver extract` producing a geometry file, sanity-checked by rendering a debug view (point cloud or wireframe dump loadable in Blender).

### Phase 1: Smoke sim + calibration harness

Voxelizer and flood fill; CounterStrikeSharp capture plugin able to throw and record.
Calibrate voxel size, budget, and radius against captured real smokes.
Deliverable: given a rest point, the sim produces a smoke volume matching the game within the acceptance bounds below.

### Phase 2: Trajectory sim + calibration

Ballistics integrator; calibrate throw speeds, restitution, and release offset per throw type against captured trajectories.
Deliverable: predicted rest points match real rest points within one voxel for the calibration set.

### Phase 3: Inverse solver, Dust 2 mid doors

Stage 1 and stage 2, occlusion test, landing cache precompute, `query` subcommand.
The occlusion goal is sealing a choke, not blocking a single ray: a query takes one denied eye plus multiple target points, and the zone must block every geometry-clear ray to all of them.
Deliverable: the validation query returns known community mid-doors smokes and novel solutions that verify in-game.

### Phase 4: Viewer

Local-first web app (served by `smokesolver serve`) rendering map geometry, good landing zones, sightlines, and lineups with copyable practice commands.
A canvas top-down viewer exists; a three.js 3D view is a possible later upgrade.

The critical path is 0 → 1 → 2 → 3; the calibration plugin (in phase 1) is the only component with an external dependency (local server setup) and should be started early.
The next concrete milestone after this document is a thin vertical slice through phases 0-3: extract Dust 2, hardcode one known smoke lineup, and verify its simulated volume blocks the mid-doors sightline.
Post-roadmap ideas (for example the lineup usability scoring system) are tracked in BACKLOG.md.

## Risks and Mitigations

- **Unpublished smoke constants**: mitigated by ADR-004; community reimplementation values serve as starting points for the fit, not as truth.
  Conservative bloom mode additionally removes calibration from the critical path: solver results remain trustworthy (precision-safe) before any constant is fitted.
- **Smoke volume capture is the hardest ground-truth signal**: rest points are easy to log server-side, but the filled voxel set is not directly exposed.
  Mitigation: fixed-camera screenshot differentials compared against rendered sim volumes, cross-checked with scripted bot line-of-sight probes through the smoke; see open questions.
- **Format drift on game updates**: VPK, `.vphys`, and `.nav` formats can change.
  Mitigation: intermediates keyed to build id, VRF is actively maintained, nav parser is owned (ADR-005), and the calibration suite doubles as a regression test after each update.
- **Stage 2 combinatorics**: the landing cache is large.
  Mitigation: coarse-to-fine angle search, range pruning, restriction to nav-sampled origins, and embarrassingly parallel precompute.
- **Subtick and timing sensitivity**: release timing may shift trajectories in ways a fixed-step integrator misses.
  Mitigation: calibration fits the integrator step and release offset directly against captured tick-by-tick trajectories.
- **Smoke settling dynamics**: the volume blooms over about a second and animates with visual noise.
  Mitigation: define the query target as the settled steady-state volume, and set the occlusion density threshold conservatively so visual wisps do not count as cover.

## Open Questions

- RESOLVED (2026-07-09): grenade collision per attribute group is settled empirically; see "Collision fidelity for grenade flight" above (player/NPC clips and sky do not block grenades, csgo_grenadeclip and passbullets do).
- The overhead beams and banners above mid are solid `Default` geometry in the physics mesh but real grenades fly through them (verified against a measured overflight).
  The fix is per-triangle surface properties: `Shapes.Mesh.Materials` exposes them, extraction must carry them (format v2), and grenade collision must skip cloth-like surfaces.

- How is the smoke voxel grid anchored (world-origin aligned or rest-point aligned), and what is the exact voxel size? First calibration target.
- What is the most reliable programmatic capture of the real smoke volume: screenshot differentials, bot LOS probes, demo-file smoke events, or a combination?
- Do dynamic entities other than doors (breakables, physics props) matter for the Dust 2 validation case, or can they be deferred?
- How should jump throws model the vertical player velocity at release, and does subtick input timing affect it?
- Cache sizing: what angle resolution does stage 2 refinement actually need to not miss viable lineups (hypothesis: coarse 5 degrees, refined 0.5 degrees near zones)?

## Success Metrics

- Trajectory: predicted rest point within one voxel of the real rest point for at least 95% of calibration throws.
- Smoke volume: intersection-over-union of at least 0.85 against captured ground-truth volumes.
- Solver recall: at least 90% of documented community mid-doors smokes appear in the result set.
- Solver precision: spot-checked novel results (10+ throws performed in-game) actually block the sightline.
- Query latency: under 5 seconds per sightline query after precompute.

## Reviewers and Timeline

Single-developer project; review is self-review plus in-game validation.
This document should be revisited at the end of each phase and updated to Approved once the phase 0-1 vertical slice confirms the extraction and sim assumptions.
