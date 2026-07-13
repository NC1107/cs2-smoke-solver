# Backlog

Ideas parked until their prerequisites land.
Each entry states the idea, why it matters, and what has to exist first.

## Lineup usability scoring

**Status**: v1 of the aim-reference factor shipped 2026-07-13; the rest is open.
**Origin**: Nick, 2026-07-09.

### Progress (2026-07-13)

Prerequisite cleared: simulation accuracy work completed (angular refinement + exact-sim re-aim, live-validated at 0.7u median).
Shipped v1 of the aim-reference factor (`src/Solver/AimReference.cs`): a 9x9 ray cone (+-6 deg) around the aim direction measures sky fraction and nearest silhouette (hit/miss boundary or >25% depth jump between adjacent rays).
Sky shots (>95% sky) sink below every referenced lineup in the API ordering, and each lineup card shows a badge: silhouette distance in degrees, `flat`, or `SKY`.
Measured impact: 91 of 221 verified lineups at the B target (41%) were sky shots and now rank last.
Next for this factor: tier the silhouette quality (enclosedness for the window case), and validate tiers against hand-labeled throws.

### Screenshot previews - resolved 2026-07-13, rendered rather than captured

Live capture from the actual game client was tried and abandoned as a dead end, not just a fallback:
- Route 1 (`ExecuteClientCommandFromServer("screenshot")` via the plugin's `!shot` command): produced nothing.
  The engine's server-forced-command allowlist almost certainly excludes `screenshot`, the same way it excludes anything that could let a malicious server write to a client's disk.
- Route 2 (client-side key injection - `xdotool` X11 XTest events, then `ydotool` via a real `/dev/uinput` virtual keyboard): both produced nothing for Steam's overlay F12 hotkey and for the engine's own console `screenshot` command (even once the right console-toggle key was found from the client's own `autoexec.cfg`, which rebinds it off the default backtick to apostrophe).
  Physical key presses worked every time; every form of synthetic injection failed uniformly.
  That pattern means CS2 and/or Steam's overlay filter out synthetic input at a layer beneath both X11 and kernel uinput - and the next step to defeat that (crafting a virtual device that more convincingly spoofs real hardware bus/vendor IDs) is the same technique underlying game macros and input cheats, so it was deliberately not pursued further.

Route 3 shipped instead: headless render from the same collision mesh the solver reasons about.
`viewer/js/view3d.js` exports `renderPreview({feet, type, pitchDeg, yawDeg})`, which points the existing three.js camera at the lineup's exact eye position/eye-height/aim direction (same convention as `GrenadeTrajectory`/`AimReference`) and renders one frame; a `body.preview-mode` CSS class hides all UI chrome for a clean full-viewport shot.
`rig/render-previews.py` drives this end to end: solves a target via the running viewer's own API, skips sky-tier lineups, and for each remaining lineup drives a headless `google-chrome-stable` (via `chrome-devtools-axi`) through `ensure3d()` + `renderPreview()` + a screenshot, saving to `data/previews/<target>/`.
Gotcha worth remembering: `chrome-devtools-axi` needs a *native* Chrome binary at the standard path for its own driver, not just something to attach to via `--remote-debugging-port` - a Flatpak-only Chrome install (`com.google.Chrome` via flathub) does not satisfy it even when reachable over CDP; install `google-chrome-stable` from Google's own RPM (`sudo dnf install -y https://dl.google.com/linux/direct/google-chrome-stable_current_x86_64.rpm`, since Fedora's repos don't carry it directly).
Not photoreal (collision-shaded like the rest of the 3D viewer, not textured), but reliable, fast, and scales to any number of lineups without touching the live game.
Next: decide whether the viewer should request/display these on lineup cards on demand, or whether pre-generating previews for a target's top-N stays a rig-triggered batch step.

### Rating system (future weight fitting)

Users rate lineups (like/dislike) in the viewer; ratings become the fit target for the scoring weights, replacing hand-labeling over time.

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

## Deferred test coverage

- LineupSolver: the `result.Lost || FlightTime >= MaxFlightSeconds` skip branch in `Solve`/`VerifyExact` has no test.
  Constructing a deterministic lost or timed-out trajectory needs physics-tuned fixtures (an open-void throw with no ground in reach).
  Flagged by the 2026-07-13 review loop as follow-up rather than blocking.
