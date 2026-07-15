# Grenade physics simulation

This document describes the smoke-grenade flight model the solver runs: the coordinate frame, the launch model, the per-tick integrator, the collision filter, and how each constant was obtained.
It is a reference for the current behaviour of `src/Sim/GrenadeTrajectory.cs`, not a design proposal.

Every constant below was **measured directly from CS2 per-tick server telemetry** (358 bounce events and 18,280 in-air tick pairs captured on `cs_flatgrass` via the CalibrationThrower plugin), then cross-checked against Valve's published references and the public Source SDK 2013 grenade code.
They are engine constants, not free fit parameters.
Do not re-fit them against rest positions: an earlier end-to-end fit silently traded gravity error against bounce error and produced a wrong `GravityScale` of 0.34.

## Coordinate frame and units

- **Units** are Hammer units (u). 1 metre = 39.37u, so 1 u/s = 0.0254 m/s.
- **Axes**: x/y horizontal, z up. Yaw is measured in the xy plane; pitch is negative looking up (Source convention).
- **Tick rate**: the server integrates grenades once per tick at 64 Hz, so the timestep is `dt = 1/64 s`. Matching the tick boundary matters because the position update uses a trapezoid rule (below).
- **Ground truth**: per-tick position *and* velocity logged from the live server. The simulator replays the same integrator, so "correct" means "reproduces the captured ticks", not "matches a closed-form arc".

## Launch model (`DeriveInitial`)

A throw is specified by `ThrowSpec(EyePosition, YawDeg, PitchDeg, Type, Strength)`.
`DeriveInitial` turns it into a release position and a launch velocity that both the simulator and the live validation pipeline feed to the real server verbatim, so sim and game start from identical state.

### Aim direction and upward bias

The engine lobs grenades slightly above the crosshair. The effective pitch is biased upward, most at level aim and tapering to zero at vertical:

```
effectivePitch = pitch - (90 - |pitch|) / 90 * 10        (degrees)
forward = ( cos(effectivePitch)·cos(yaw),
            cos(effectivePitch)·sin(yaw),
           -sin(effectivePitch) )
```

At level aim (`pitch = 0`) the effective launch is 10° above the crosshair; at `pitch = -90` (straight up) the bias is 0.

### Release point

```
release = EyePosition + forward · 16
```

The grenade is born 16u along the aim direction from the eye.
Eye height above the feet is a view offset (`VEC_VIEW` / `VEC_DUCK_VIEW`):

| Stance | Eye height (u) |
|---|---|
| Standing | 64.06 |
| Crouched | 46.04 |

Both were confirmed against live captures (a grounded stand throw and a grounded crouch throw each released within 0.04u of feet plus these heights).

### Launch speed by click

Base throw speed is **675 u/s** (left click). The other clicks are independently calibrated multipliers, not the folklore `0.7 + 0.3·strength` curve (which mispredicted a right-click Long A throw by ~285u):

| Click | `Strength` | Speed multiplier | Speed (u/s) |
|---|---|---|---|
| Left | 1.0 | 1.00 | 675.0 |
| Mid (L+R) | 0.5 | 0.65 | 438.7 |
| Right | 0.0 | 0.30 | 202.5 |

Confirmed exactly by three same-position/same-aim throws measuring 202.5 / 438.7 / 675.0.

### Jump and run additions

For any jump throw, a vertical velocity is added and the release point is raised, because the grenade leaves the hand several ticks into the jump (after the rise has bled some vertical speed, and after the player has climbed):

| Addition | Value | Notes |
|---|---|---|
| Jump vertical (`JumpVelocity`) | +273.6 u/s | 12 live jump throws, spread 0.2. Not 300: the toss happens mid-rise. |
| Crouch-jump vertical (`CrouchJumpVelocity`) | +277.5 u/s | Releases slightly later up its own arc. |
| Run-jump horizontal (`RunSpeed`) | +306 u/s along facing | Two full-speed run jumps both landed on 306.1, independent of click. Folklore 250 is the *ground* run speed. |
| Release rise, right click (`ReleaseRiseRight`) | +14.0 u | 9 throws, spread 0.4. |
| Release rise, mid click (`ReleaseRiseBoth`) | +20.0 u | Linear in click power: harder throws wind up longer and release later/higher. |
| Release rise, left click (`ReleaseRiseLeft`) | +26.1 u | |

Grounded throws are not raised (two grounded controls released at 0.00 and -0.04u).
The release rise models the raised birth *position*; the birth *velocity* already carries the matching jump vz.

## Integrator (`SimulateExactRaw`)

Replicates the engine's `PhysicsToss` + `ResolveFlyCollisionCustom` tick loop against exact collision triangles with true surface normals.
Per tick:

1. **Clamp** each velocity component to `sv_maxvelocity = 3500` u/s.
2. **Gravity** on velocity, full tick: `vz -= 800 · 0.40 · dt`. (`sv_gravity = 800`, per-projectile scale `GravityScale = 0.40`.)
3. **Position update**, trapezoid on z (average of pre- and post-gravity vz), rectangle on x/y:
   ```
   move = ( vx·dt, vy·dt, (vzOld + vz)/2 · dt )
   next = position + move
   ```
4. **Sweep** the ±2u box hull (`GRENADE_DEFAULT_SIZE`) from `position` to `next`. If it hits a triangle:
   - Back off to the contact point (`t - 1e-3`).
   - **Reflect** the whole velocity about the surface normal (`w - 2·(w·n)·n`), then **snap** any component with magnitude `< STOP_EPSILON (0.1)` to exactly zero *before* the restitution multiply.
   - **Restitution** multiplies the *whole* reflected vector by `Elasticity = 0.45`. There is no tangential-friction term in the grenade path.
   - **Angle damp (floor only)**: if impact speed `> DampGateSpeed (689 u/s)` *and* the impact angle is steeper than 60° (`|w·n|/|w| > 0.5`) *and* the surface is floor-like (`n.z > 0.7`), additionally scale by `(1.5 - |cos angle|)`. Walls never damp (validated: 0/122 gated wall bounces damped vs 68/76 gated ground bounces).
   - **Stop rule**: if post-bounce speed `< StopSpeed (19.685 u/s ≈ 0.5 m/s)` on a floor (or with floor directly below), the grenade rests. There is no rolling phase.
   - Otherwise consume the remainder of the tick with the bounced velocity (single bounce resolved per tick). Slow contact against a wall with no floor beneath **slides** along the surface rather than freezing.

The voxel `Simulate` is the coarse stage-1 model (inflated voxels, axis-aligned reflection); `SimulateExact` re-verifies finalist lineups against true triangle normals.

### Constants at a glance

| Constant | Value | Source |
|---|---|---|
| `ThrowSpeed` (base) | 675 u/s | Measured (same-aim triple) |
| `GravityScale` | 0.40 | Measured (in-air tick pairs) |
| `BaseGravity` (`sv_gravity`) | 800 u/s² | Engine convar |
| `Elasticity` | 0.45 | Measured; matches CS:GO `grenade` surfaceprop |
| `StopSpeed` | 19.685 u/s | Measured bracket (19.498, 19.782] |
| `DampGateSpeed` | 689 u/s | Measured bracket (684.1, 696.6] |
| `MaxVelocityPerAxis` (`sv_maxvelocity`) | 3500 u/s | Engine convar |
| `StopEpsilon` (`STOP_EPSILON`) | 0.1 | Source SDK |
| `FloorNormalZ` | 0.7 | `sv_standable_normal` |
| `GrenadeRadius` (hull half-extent) | 2 u | `GRENADE_DEFAULT_SIZE`; rest sits 2.03u above floor |

No drag, no tangential friction on bounce, and no rolling phase — all three are absent in the CS2 telemetry (zero-drag in-air, no tangential loss across bounces, instant stop below `StopSpeed`).

## Collision filter (`GrenadeSolidFilter`)

A triangle blocks a grenade unless its physics interaction layers (`m_InteractAsStrings`) include `playerclip`, `npcclip`, or `sky`.
Everything else is solid to grenades — including `csgo_grenadeclip` and `passbullets`.
Validated on de_dust2 (301 real bounce events; 281 player-clip and 11 sky fly-throughs observed, zero grenade-clip fly-throughs).

The collision mesh itself comes from `world_physics.vmdl_c` (the PHYS block) plus an allowlist of solid brush entities (`func_brush`, `func_clip_vphysics`, `func_door`, `func_door_rotating`, `func_breakable`).
It does **not** currently include `prop_static` physics hulls (see Limits).

## Accuracy and limits

On clean world geometry the physics is accurate to roughly **1 unit**: replaying the measured model reproduced all 60 open-ground `cs_flatgrass` captures with a median rest error of 1.09u and touchdown tick within ±1, and a 72-throw synthetic battery put the integrator at a median 0.82u.
Solver lineups thrown back in-game via the plugin landed 2-8u from target, matching the sim.

The **dominant remaining real-world error is collision-mesh fidelity, not physics.**
Static props (`prop_static`) carry their collision hull inside their own `.vmdl`, not in `world_physics`, so they are absent from the extracted `.s2geo`.
Two cross-map de_mirage throws land on elevated structures (resting at z=-52 and z=66) that our mesh does not represent, so the sim's grenade passes where the real one stops.
This is the `prop_static` signature and is the subject of a separate mesh-fidelity follow-up (merge each static prop's placed hull into the `.s2geo`).

## Validation against Valve's references

Our constants were measured from CS2 directly, so Valve's CS:GO references are an independent cross-check.
The smoke grenade entity (`CBaseCSGrenade`) is shared across CS:S / CS:GO / CS2, and the values align everywhere they overlap.

### Constants cross-check (CS:GO Mapper's Reference / Surface Types)

| Quantity | Valve reference | Ours | Reconciliation |
|---|---|---|---|
| Standing eye height | 64.093811 above floor, feet 0.031250 above floor | 64.06 above feet | 64.0938 − 0.0313 = 64.0625 ≈ 64.06. The 0.03u feet gap is unmodelled and negligible. |
| Crouched eye height | 46.076218 above floor | 46.04 above feet | 46.0762 − 0.0313 = 46.045 ≈ 46.04. |
| Walkable slope | ≤ 45.573° = `sv_standable_normal 0.7` | `FloorNormalZ = 0.7` | Exact. |
| Max velocity | `sv_maxvelocity 3500` | 3500 per-axis clamp | Exact. |
| Grenade restitution | `grenade` surfaceprop elasticity ≈ 0.45 | `Elasticity = 0.45` | Exact. |

The same `grenade` surfaceprop also lists friction ≈ 0.5 and a dampening term.
Those govern VPhysics *resting* interactions, not the thrown grenade's flight, which uses the custom toss resolver (pure reflection × fixed restitution, no tangential friction, no drag) — already confirmed absent in CS2 telemetry.
So the surface-friction entries do not imply a missing term in the flight model.

### Independent flight check: max smoke distance on a flat plane

Valve's Mapper's Reference publishes the maximum smoke throw distance on flat ground for several movement types.
Reproducing those distances validates the whole launch + flight model against Valve's own measurements, not just against our telemetry.
Left-click smokes thrown on a flat plane at Valve's stated pitches:

| Movement | Valve pitch | Valve distance | Sim distance | Delta |
|---|---|---|---|---|
| Standing | -32.9° | 1754 u | 1762.5 u | +8.5 u (+0.5%) |
| Jump throw | -27.4° | 2653 u | 2692.1 u | +39.1 u (+1.5%) |
| Run-jump throw | -33.0° | 4380 u | 4434.2 u | +54.2 u (+1.2%) |

All three land within ~1.5%, confirming the launch speeds, the jump/run additions, and the gravity/flight integration together against an external source.

The residual is a consistent ~0.5-1.5% *overshoot* (we land slightly farther than Valve), and it is **not** an artifact of the eye height or the pitch quoting:
- Substituting the CS:GO eye heights (64.0625 above feet / 64.0938 above floor vs our 64.06) moves each distance by ≤0.03u - the eye-height difference is ~2000× smaller than the residual, so it cannot be the lever, and a higher launch would nudge the overshoot the wrong way.
- Sweeping the pitch across Valve's ±0.1° quoting resolution moves each distance by <2u, so the residual is not pitch-rounding noise. The flatness is expected: Valve quotes the distance-*maximizing* pitch, where distance is stationary.

So the ~1% is a genuine small magnitude difference, most plausibly the measurement convention (Valve's distance origin vs the smoke's ~12-16u-forward spawn point, or first-landing vs settle point) and the precision of a hand-measured reference table. It is an order of magnitude below the collision-mesh-fidelity error and is not worth re-fitting the (telemetry-measured) constants against.

Valve's fourth row, a plain *running* (ground, no jump) throw of 3045u @ -40.7°, is not reproduced here: the sim has no ground-running throw type (only `RunJumpThrow` adds movement velocity), so there is no calibrated constant for it.
