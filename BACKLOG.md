# Backlog

Ideas parked until their prerequisites land.
Each entry states the idea, why it matters, and what has to exist first.

## Lineup usability scoring

**Status**: backlogged until simulation accuracy work is complete.
**Origin**: Nick, 2026-07-09.

### Problem

The solver ranks lineups by physical properties (stability, bounces, flight time), but physical viability says nothing about whether a human can actually execute the throw in a match.
A lineup that aims into featureless sky is nearly unusable no matter how stable its trajectory is, because there is nothing to place the crosshair against.
The ranking should reflect execution difficulty and in-round cost, not just trajectory quality.

### Scoring factors

#### Aim reference quality (the big one)

How much visual structure sits near the required crosshair placement.
Proposed tiers, best to worst:

1. A window or doorframe the crosshair fits inside: an enclosed silhouette self-corrects both axes.
2. A distinct point feature at or near the crosshair: pole tip, antenna, texture corner, roofline corner.
3. An edge or line feature: a roofline or wall edge that pins one axis but leaves the other loose.
4. A color or shading boundary near the aim point: usable but fuzzy.
5. Open sky with nothing in the periphery: worst case, effectively a no-reference throw.

Implementation sketch: cast the aim direction from the eye position, then measure the angular distance from the crosshair to the nearest geometry silhouette (depth discontinuity) within the view frustum.
Enclosedness (window case) can be approximated by checking whether silhouette edges surround the aim point within a small angular radius.
Phase 1 can be purely geometric from the collision mesh; texture and color boundaries need rendered imagery and can come later.

#### Position reference quality

How precisely the player can reproduce the standing spot without a getpos console command.

1. Wedged into a corner (two walls touching the hull): position is self-correcting, perfect.
2. Back or side against a single wall or a distinct object (crate edge, pole, door frame): one axis pinned.
3. Open ground with only visual alignment: worst.

Implementation sketch: probe the collision mesh around the feet position for wall contacts within a player-hull radius, and score by the number of independent contact directions.
The solver could also actively prefer snapping candidate origins to nearby corners when a corner within a few units still lands the smoke, trading a little trajectory optimality for a lot of reproducibility.

#### Noise discipline

Jump throws and run+jump throws make landing noise that tells nearby enemies a utility player is there.
Penalize a jump variant when a standing or crouching throw from roughly the same area reaches the same target: the noisy version adds risk without adding value.
No penalty when the jump is the only way to make the range or clear the obstruction.
Running (run+jump) also emits footstep noise on the approach and should carry a slightly larger penalty than a stationary jump throw.

#### Time to bloom

Total time from key press to the smoke actually blocking vision: flight time plus the bloom expansion delay.
A lineup that takes many seconds of flight telegraphs the play and may bloom after the fight already happened; a 30 second total is unusable in practice.
Score continuously rather than with a hard cutoff, since a slow lob can still be fine for a pre-round setup smoke.
The bloom expansion delay itself should be measured from the calibration captures (we already record DidSmokeEffect ticks) rather than assumed.

#### Explicitly NOT a factor: bounce count

Some of the best lineups bounce four times within a quarter second because the grenade is wedged into a corner pocket, and those are excellent, reproducible throws.
The existing stability score (perturbed re-simulation) already captures whether bounces make the outcome fragile, which is the thing that actually matters.
Keep stability as the physical-risk factor and do not double-penalize bounces.

### Additional factors worth considering (not from the original request)

- **Execution complexity**: stand < crouch < jumpthrow bind < crouch+jump < run+jump; more moving parts means more ways to throw it wrong under pressure.
- **Pitch extremity**: aiming steeper than about -60 degrees is disorienting even with a reference, because the reference leaves the screen during the flick back down; mild penalty scaling with pitch.
- **Throw-spot exposure**: standing in an open lane (for example mid doors gap) to throw is a death sentence in a real round; approximate by checking sightline exposure of the origin from the enemy-approach areas for the chosen target.
- **Setup reachability**: for round-start smokes, the origin must be reachable from spawn in time for the smoke to matter; score by nav-path distance from the team spawn.
- **Landing margin**: distance from the rest point to the target center relative to the bloom radius; a smoke that barely clips the target edge fails under small execution error even if the sim says it lands.
- **Crosshair travel**: lineups where the reference point and the final aim point are far apart (line up on X, then flick) are harder than aim-and-throw; measurable as angular distance if reference detection lands.

### Scoring model sketch

Normalize each factor to 0..1, combine as a weighted sum with configurable weights, and expose the per-factor breakdown in the viewer card so the ranking is explainable.
Hard filters (unusable) stay separate from soft scores (worse): no aim reference at all and bloom-too-late-for-purpose are candidates for hard filters.
Weights will need playtesting; start by sorting the existing mid-doors result set by hand into good/bad and fitting weights to match that ordering.

### Prerequisites

- Simulation accuracy work finished (flight physics is engine-exact as of 2026-07-09; remaining work is map-fidelity tails and crouch eye-height validation).
- Silhouette or reference-point extraction from map geometry (new capability, phase 1 of aim-reference scoring).
- Measured bloom expansion timing from calibration captures (data already collected, analysis not yet done).
