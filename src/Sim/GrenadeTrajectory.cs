using System.Numerics;

namespace SmokeSolver.Sim;

public enum ThrowType
{
    Stand,
    Crouch,
    JumpThrow,
    CrouchJumpThrow,
    RunJumpThrow,
}

/// <summary>
/// Strength maps the mouse buttons: 1 = left, 0.5 = left+right, 0 = right.
/// Each click's velocity multiplier is a calibrated constant (ThrowConstants).
/// RunYawOffsetDeg is the movement-key direction of a running jump throw
/// relative to the facing: 0 = W, +90 = A (Source yaw grows to the left),
/// -90 = D, +-45 = the W+A / W+D diagonals. Ground speed is the same in every
/// direction (the engine normalizes the wish direction), so the carried
/// velocity only rotates, never changes magnitude.
/// </summary>
// Struct: allocated once per simulated throw, and solves run millions of
// simulations inside Parallel.ForEach - heap records were pure gen0 churn.
public readonly record struct ThrowSpec(Vector3 EyePosition, float YawDeg, float PitchDeg, ThrowType Type, float Strength = 1f, float RunYawOffsetDeg = 0f);

public readonly record struct TrajectoryResult(Vector3 RestPoint, int Bounces, float FlightTime, bool Lost, Vector3? FirstTouch = null);

/// <summary>One exact-sim bounce, kept for diagnostics: which tick, where, off what.</summary>
public readonly record struct BounceRecord(int Tick, Vector3 Contact, Vector3 Normal, int Triangle, Vector3 VelocityBefore, Vector3 VelocityAfter);

/// <summary>
/// Physics constants for grenade flight, extracted so calibration can fit them.
/// Defaults are the current best fit (see DESIGN.md, calibration).
/// </summary>
// Flight constants were MEASURED directly from per-tick server telemetry (358
// bounce events, 18,280 in-air tick pairs on cs_flatgrass; see
// data/calib/captures.jsonl and the physics measurement report), then
// cross-confirmed against the public Source SDK 2013 grenade code
// (sdk_basegrenade_projectile.cpp, ResolveFlyCollisionCustom lineage).
// Replaying the measured model reproduces all 60 open-ground captures with
// median 1.09u rest error and touchdown tick within +-1. These are engine
// constants, not fit parameters - do not re-fit them against rest positions
// (an earlier end-to-end fit produced gravity 0.34 by silently trading
// gravity error against bounce error).
public sealed record ThrowConstants(
    float ThrowSpeed = GrenadeTrajectory.ThrowSpeed,
    float GravityScale = 0.40f,
    // One uniform restitution multiplies the WHOLE reflected vector; the
    // engine has no tangential friction term in the grenade path at all.
    float Elasticity = 0.45f,
    // Post-bounce speed below which a floor impact stops dead (no rolling
    // phase exists). Measured bracket (19.498, 19.782]; 19.685 = 0.5 m/s.
    float StopSpeed = 19.685f,
    // FLOOR impacts faster than this AND steeper than 60 degrees additionally
    // scale by (1.5 - |cos impact angle|); wall impacts never damp (validated:
    // 0/122 gated wall bounces damped vs 68/76 ground). Measured bracket
    // (684.1, 696.6] from the flatgrass batch; narrowed to (689.75, 690.75]
    // on 2026-09-04 from 3,030 steep floor bounces in the validation
    // captures (every bounce from 690.75 up damped, 689.25 to 689.75 none,
    // a mixed quarter-tick band between). Not in the SDK-era code; measured
    // in CS2. 690.0 scored 380 -> 373 corpus misses; 689.5 and 690.5 both
    // moved single maps by more than the loop allows.
    float DampGateSpeed = 690f,
    // Vertical velocity a jump adds to the throw. MEASURED at 273.6 (12 live
    // jump throws, spread 0.2), not the 300 a naive "release with full jump
    // velocity" model assumes: the grenade leaves the hand several ticks into
    // the jump, by which point the rise has bled the vertical speed. A crouch
    // jump releases a touch higher up its own arc, so it carries 277.5.
    float JumpVelocity = 273.6f,
    float CrouchJumpVelocity = 277.5f,
    // Whether a grenade that stops balanced on an edge tips off it. Off: the
    // corpus replay (5,133 real dust2 throws, 2026-09-04) showed the engine
    // leaves grenades balanced on pole tops, crate rims and beams far more
    // often than it tips them - tipping fixed 13 throws and broke 50.
    bool EdgeTipping = false,
    // How many contacts one tick may resolve. The game's telemetry at a
    // wall-floor corner shows both reflections landing inside one tick; with
    // only the first resolved, the second surface merely stopped the hull
    // until the next tick. Corpus replay 2026-09-04: 2 fixed 5 throws and
    // broke 1 on dust2, 1 on nuke, nothing elsewhere; 3 adds nothing over 2.
    int BouncesPerTick = 2,
    // A grenade meeting intact breakable glass breaks it and carries on in
    // the same direction at this fraction of its speed; the pane is gone for
    // the rest of the flight. MEASURED on cs_office 2026-09-04: five throws
    // through intact window props left at exactly 0.40 of their speed with
    // the direction unchanged, and one through an already broken pane lost
    // nothing. Zero = treat glass as a solid wall.
    float GlassPassFactor = 0.40f,
    // Horizontal velocity a running jump throw adds along the facing. MEASURED
    // at 306 from two full-speed run jumps (left and right click both landed on
    // 306.1, confirming it is player velocity, independent of the throw). The
    // folklore 250 is the ground run speed; the running jump carries more.
    float RunSpeed = 306f,
    // How far above the eye the grenade is actually released on a jump throw.
    // MEASURED: right 14.0, both 20.0, left 26.1 (9 throws, spread 0.4),
    // linear in click power because the harder throw's longer wind-up releases
    // later, when the jump has carried the player higher. Zero for grounded
    // throws (two grounded controls released at 0.00 and -0.04). Modelling this
    // as a release-height offset reproduces the observed birth position; the
    // birth velocity above already carries the matching jump vz.
    float ReleaseRiseRight = 14.0f,
    float ReleaseRiseBoth = 20.0f,
    float ReleaseRiseLeft = 26.1f,
    // Measured Long A ranges falsified the folklore 0.7 + 0.3 * strength speed
    // curve (right-click flew 152u, the curve predicts ~437u), so each click
    // gets an independently calibrated velocity multiplier. Confirmed exactly
    // via three same-position/same-aim throws (right/both/left): speeds
    // 202.5/438.7/675.0 against a 675 base = 0.30/0.65/1.00.
    float RightClickScale = 0.30f,
    float BothClickScale = 0.65f)
{
    public static ThrowConstants Default { get; } = new();

    public float SpeedScale(float strength) =>
        strength >= 0.99f ? 1f : strength >= 0.49f ? BothClickScale : RightClickScale;

    public float ReleaseRise(float strength) =>
        strength >= 0.99f ? ReleaseRiseLeft : strength >= 0.49f ? ReleaseRiseBoth : ReleaseRiseRight;
}

/// <summary>
/// Grenade flight, replicating the engine's own per-tick integrator (verified
/// against per-tick server telemetry to float precision; see ThrowConstants).
/// The voxel Simulate is the coarse stage-1 model; SimulateExact/SimulateExactRaw
/// run against exact collision triangles with true surface normals.
/// </summary>
public static class GrenadeTrajectory
{
    public const float ThrowSpeed = 675f;
    public const float MaxFlightSeconds = 10f;
    // Engine view offsets (VEC_VIEW / VEC_DUCK_VIEW). Crouching only lowers the
    // release point; throw speed and the pitch bias are unchanged. Both values
    // are confirmed against live captures: a grounded stand throw and a grounded
    // crouch throw each released within 0.04u of feet plus these heights.
    public const float StandEyeHeight = 64.06f;
    public const float CrouchEyeHeight = 46.04f;

    public static float EyeHeight(ThrowType type) =>
        type is ThrowType.Crouch or ThrowType.CrouchJumpThrow ? CrouchEyeHeight : StandEyeHeight;
    // The server integrates grenades once per tick at 64/s; matching it exactly
    // matters because position updates use a trapezoid on the tick boundary.
    const float TimeStep = 1f / 64f;
    // sv_maxvelocity: the engine clamps each velocity component to this before
    // every move.
    const float MaxVelocityPerAxis = 3500f;
    // PhysicsClipVelocity snaps near-zero reflected components to exactly zero
    // BEFORE the elasticity multiply (STOP_EPSILON in the SDK).
    const float StopEpsilon = 0.1f;

    // Source engine base gravity (sv_gravity default); scaled per-projectile
    // by the calibrated GravityScale.
    public const float BaseGravity = 800f;

    // A contact normal with z at or above this is "floor" for bounce and rest
    // decisions; below it the surface is a wall/ramp.
    // Source's slope limit (sv_standable_normal, 45.57 deg). The solver's
    // StandSpots.StandableNormalZ aliases this constant.
    public const float FloorNormalZ = 0.7f;

    // Floor-impact angle damp, the ONE copy both integrators share. It applies
    // to floor impacts only: 122 gated wall bounces across the dust2 validation
    // runs all reflected at exactly 0.45 while 68/76 gated ground bounces
    // damped (flatgrass could not constrain this - its wall hits were all below
    // the speed gate). u is |normal-component of velocity| / speed, computed by
    // each caller against its own contact representation. This was previously
    // duplicated in both integrators and one copy went missing, desyncing
    // stage 1 from the exact path by ~120u on fast steep throws.
    static float FloorImpactDamp(float speed, float u, bool isFloor, ThrowConstants k) =>
        speed > k.DampGateSpeed && u > 0.5f && isFloor ? 1.5f - u : 1f;

    /// <summary>
    /// The exact simulator's bounce: velocity after hitting a surface with
    /// this normal at this incoming velocity. Public so diagnostics can ask
    /// which normal would have produced a recorded real rebound.
    /// </summary>
    public static Vector3 Bounce(Vector3 w, Vector3 normal, ThrowConstants k)
    {
        var speed = w.Length();
        var reflected = SnapStopEpsilon(w - 2f * Vector3.Dot(w, normal) * normal);
        var u = speed > 1e-6f ? MathF.Abs(Vector3.Dot(w, normal)) / speed : 0f;
        var damp = FloorImpactDamp(speed, u, isFloor: normal.Z > FloorNormalZ, k);
        return reflected * (k.Elasticity * damp);
    }

    // Post-contact positional backoff keeping the hull from re-embedding in
    // the surface it just hit.
    const float ContactBackoff = 1e-3f;

    public static TrajectoryResult Simulate(VoxelGrid grid, ThrowSpec spec, ThrowConstants? constants = null)
    {
        var k = constants ?? ThrowConstants.Default;
        // Launch state comes from the same derivation the exact simulator
        // uses: these constants are calibrated against live telemetry, and a
        // second inline copy silently desynchronized the two paths once.
        var (position, velocity) = DeriveInitial(spec, constants);

        var gravityStep = BaseGravity * k.GravityScale * TimeStep;
        var bounces = 0;
        var time = 0f;
        Vector3? firstTouch = null;

        while (time < MaxFlightSeconds)
        {
            var vzOld = velocity.Z;
            velocity.Z -= gravityStep;
            var next = position + new Vector3(velocity.X, velocity.Y, (vzOld + velocity.Z) * 0.5f) * TimeStep;

            var (cx, cy, cz) = grid.CellOf(next);
            if (cx < 0 || cx >= grid.Nx || cy < 0 || cy >= grid.Ny || cz < 0)
            {
                return new TrajectoryResult(next, bounces, time, Lost: true, firstTouch);
            }
            if (cz >= grid.Nz)
            {
                // Above the voxelized region is open sky; keep integrating.
                position = next;
                time += TimeStep;
                continue;
            }
            if (grid.IsSolid(grid.Index(cx, cy, cz)))
            {
                var (contact, axis) = FindContact(grid, position, next);
                var preImpact = velocity;
                position = contact;
                firstTouch ??= contact;
                bounces++;
                // Uniform reflection: flip the crossed axis, scale the whole
                // vector by elasticity (the engine has no tangential friction).
                velocity = axis switch
                {
                    0 => velocity with { X = -velocity.X },
                    1 => velocity with { Y = -velocity.Y },
                    _ => velocity with { Z = -velocity.Z },
                };
                var speed = preImpact.Length();
                var u = speed > 1e-6f ? MathF.Abs(preImpact.Z) / speed : 0f;
                velocity *= k.Elasticity * FloorImpactDamp(speed, u, isFloor: axis == 2, k);
                if (axis == 2 && velocity.Length() < k.StopSpeed && HasGroundBelow(grid, position))
                {
                    return new TrajectoryResult(position, bounces, time, Lost: false, firstTouch);
                }
            }
            else
            {
                position = next;
            }
            time += TimeStep;
        }
        return new TrajectoryResult(position, bounces, time, Lost: !HasGroundBelow(grid, position), firstTouch);
    }

    /// <summary>
    /// Flight against exact collision triangles with true surface normals; slower
    /// than the voxel model but deflects correctly off slanted geometry. Used to
    /// re-verify finalist lineups.
    /// </summary>
    // The engine sweeps grenades as a +-2 unit box hull (GRENADE_DEFAULT_SIZE
    // in the SDK). Confirmed against telemetry: grenades rest with their center
    // exactly 2.03 units above the floor plane (66.03125 over the z=64
    // flatgrass ground), and box corners catch surface edges that a same-size
    // sphere misses.
    public const float GrenadeRadius = 2f;

    // How far from a break point breakable triangles count as the same pane.
    const float BrokenPaneReach = 96f;
    static readonly Vector3 HullHalfExtents = new(GrenadeRadius, GrenadeRadius, GrenadeRadius);
    // Physics steps per server tick (128 Hz inside a 64-tick server).
    // MEASURED 2026-09-04 from the rig captures: which half of the tick a
    // contact lands in decides how much of the tick's gravity the rebound
    // carries (see SimulateExactRaw).
    public const int PhysicsSubsteps = 2;

    // Support probes around the hull's centre: a point ray each, straight
    // down through where the hull rests, on a ring wide enough to reach past
    // the hull's own footprint and find which side the floor is on.
    // How far past the edge line the centre may sit and still be held: a
    // box balanced with half its base on the ledge does not tip.
    const float EdgeNeutralBand = 0.75f;
    static readonly Vector2[] SupportOffsets = [.. Enumerable.Range(0, 8)
        .Select(i => new Vector2(3f * MathF.Cos(i * MathF.PI / 4f), 3f * MathF.Sin(i * MathF.PI / 4f)))];

    /// <summary>
    /// Null when the floor holds the hull's centre; otherwise the horizontal
    /// direction it tips in - away from where the floor is - or, with no
    /// floor anywhere around, along <paramref name="heading"/>.
    /// </summary>
    static Vector3? EdgeTip(TriangleCollider collider, Vector3 position, Vector3 heading)
    {
        var reach = GrenadeRadius + 1f;
        bool Held(float dx, float dy)
        {
            var from = new Vector3(position.X + dx, position.Y + dy, position.Z);
            return collider.FirstHit(from, from + new Vector3(0, 0, -reach)) is { } h && h.Normal.Z > FloorNormalZ;
        }
        if (Held(0f, 0f))
        {
            return null;
        }
        var supported = Vector2.Zero;
        var count = 0;
        foreach (var o in SupportOffsets)
        {
            if (Held(o.X, o.Y))
            {
                supported += o;
                count++;
            }
        }
        Vector2 dir;
        if (count > 0 && supported.LengthSquared() > 1e-6f)
        {
            // Tip away from the side that holds it - unless the edge runs
            // within a unit of the centre: the engine keeps a grenade whose
            // centre sits on the edge line (validated on the T side of the
            // same beam: real rest exactly on the edge, ours had tipped it).
            dir = -Vector2.Normalize(supported);
            if (Held(-dir.X * EdgeNeutralBand, -dir.Y * EdgeNeutralBand))
            {
                return null;
            }
        }
        else
        {
            var h = new Vector2(heading.X, heading.Y);
            if (h.LengthSquared() < 1e-6f)
            {
                return null;
            }
            dir = Vector2.Normalize(h);
        }
        return new Vector3(dir.X, dir.Y, 0f);
    }

    /// <summary>
    /// Initial projectile state for a throw spec: release position (eye plus
    /// 16u along the aim direction) and launch velocity (pitch-biased aim,
    /// per-click speed, jump/run additions). Shared by the simulators and by
    /// the live validation pipeline, which feeds these exact values to the
    /// real server so sim and game start from identical conditions.
    /// </summary>
    // Source's angle convention: yaw around Z, negative pitch aims up
    // (dir.z = -sin(pitch)). The one copy of this trig - the camera model
    // (AimReference) calls it with the raw pitch, the launch model below with
    // the bias-corrected pitch; they diverge in INPUT, never in the formula.
    public static Vector3 ForwardFromAngles(float pitchDeg, float yawDeg)
    {
        var pitch = pitchDeg * MathF.PI / 180f;
        var yaw = yawDeg * MathF.PI / 180f;
        return new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Cos(pitch) * MathF.Sin(yaw),
            -MathF.Sin(pitch));
    }

    public static (Vector3 Position, Vector3 Velocity) DeriveInitial(ThrowSpec spec, ThrowConstants? constants = null)
    {
        var k = constants ?? ThrowConstants.Default;
        var effectivePitch = spec.PitchDeg - (90f - MathF.Abs(spec.PitchDeg)) / 90f * 10f;
        var forward = ForwardFromAngles(effectivePitch, spec.YawDeg);

        var velocity = forward * (k.ThrowSpeed * k.SpeedScale(spec.Strength));
        var release = spec.EyePosition + forward * 16f;
        var isJump = spec.Type is ThrowType.JumpThrow or ThrowType.CrouchJumpThrow or ThrowType.RunJumpThrow;
        if (isJump)
        {
            velocity.Z += spec.Type is ThrowType.CrouchJumpThrow ? k.CrouchJumpVelocity : k.JumpVelocity;
            // The player has risen off the ground by release; the grenade is
            // born from that raised eye, not the standing one.
            release.Z += k.ReleaseRise(spec.Strength);
        }
        if (spec.Type is ThrowType.RunJumpThrow)
        {
            var runYaw = (spec.YawDeg + spec.RunYawOffsetDeg) * MathF.PI / 180f;
            velocity += new Vector3(MathF.Cos(runYaw), MathF.Sin(runYaw), 0) * k.RunSpeed;
        }
        return (release, velocity);
    }

    public static TrajectoryResult SimulateExact(TriangleCollider collider, ThrowSpec spec, ThrowConstants? constants = null, List<string>? trace = null)
    {
        var (position, velocity) = DeriveInitial(spec, constants);
        return SimulateExactRaw(collider, position, velocity, constants, trace);
    }

    /// <summary>
    /// Same integrator as <see cref="SimulateExact"/> but takes the initial
    /// position/velocity directly, bypassing the yaw/pitch/click derivation.
    /// Replicates the engine's PhysicsToss + ResolveFlyCollisionCustom tick
    /// loop as measured from real per-tick server telemetry: full-tick gravity
    /// on velocity, trapezoid z position update, whole-vector 0.45 restitution
    /// on reflection, gated angle damping, and an instant stop rule.
    /// </summary>
    // NOTE for future sim archaeology: smokes do NOT fizzle at sky-layer
    // surfaces. A sky-termination rule was tried here (2026-07-16) off
    // capture evidence of flights ending mid-air, and re-scoring 4,075
    // recorded throws showed 965+ regressions - accurate flights routinely
    // cross sky ceilings and land exactly as simulated. The mid-air-ending
    // captures were undetonated projectiles culled by the engine during
    // dense validation batches, a harness artifact ValidateCommand now
    // detects and excludes instead.
    // AggressiveOptimization keeps this method out of tiered compilation.
    // With on-stack replacement enabled, .NET 10 mis-compiled the sub-step
    // loop below once it got hot: the same throw that lands 1u from the
    // capture when simulated alone fell through the floor after sixty other
    // throws had run in the process (2026-09-04, DOTNET_TC_OnStackReplacement=0
    // and DOTNET_TieredCompilation=0 both restored the right result).
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static TrajectoryResult SimulateExactRaw(TriangleCollider collider, Vector3 position, Vector3 velocity, ThrowConstants? constants = null, List<string>? trace = null, List<(Vector3 Position, Vector3 Velocity)>? tickTrace = null, List<BounceRecord>? bounceTrace = null)
    {
        var k = constants ?? ThrowConstants.Default;
        var gravityStep = BaseGravity * k.GravityScale * TimeStep;
        var bounces = 0;
        var time = 0f;
        var tick = 0;
        Vector3? firstTouch = null;
        // Glass this flight has already broken: every breakable triangle near
        // a break point is air from then on (a pane is several triangles and
        // the mesh does not say which belong together).
        List<Vector3>? broken = null;
        Func<int, bool>? ignore = null;

        while (time < MaxFlightSeconds)
        {
            // The engine integrates in half-tick steps. MEASURED 2026-09-04
            // (diverge --offsets, 3,753 paired floor bounces): a contact in
            // the first half of a tick leaves at 0.45 x the half-step
            // velocity and then takes the second half-step's gravity (3.6 u/s
            // in total below the full-tick reflection), a contact in the
            // second half leaves at 0.45 x the full-tick velocity and takes
            // nothing more. Free flight integrates identically either way;
            // only the bounce tick differs. Spending the remainder of the
            // tick at the reflected velocity with the remainder's gravity
            // taken off (the old model) made every hop after a second-half
            // contact about a tick shorter than the game's.
            var stepDt = TimeStep / PhysicsSubsteps;
            var stepG = gravityStep / PhysicsSubsteps;
            for (var step = 0; step < PhysicsSubsteps; step++)
            {
                velocity = ClampVelocity(velocity);
                var vzOld = velocity.Z;
                velocity.Z -= stepG;
                var move = new Vector3(velocity.X, velocity.Y, (vzOld + velocity.Z) * 0.5f) * stepDt;
                var next = position + move;

                if (collider.FirstHitHullIndexed(position, next, HullHalfExtents, ignore: ignore) is not { } hit)
                {
                    position = next;
                    continue;
                }
                var contact = Vector3.Lerp(position, next, Math.Max(0f, hit.T - 1e-3f));
                position = contact;
                firstTouch ??= contact;
                bounces++;

                if (k.GlassPassFactor > 0f && collider.IsBreakable(hit.Triangle))
                {
                    // Through the glass: same heading, less speed, and the
                    // pane stops existing for this flight.
                    broken ??= [];
                    broken.Add(contact);
                    var panes = broken;
                    ignore = t => collider.IsBreakable(t) && panes.Any(b => Vector3.DistanceSquared(b, collider.Centroid(t)) < BrokenPaneReach * BrokenPaneReach);
                    velocity *= k.GlassPassFactor;
                    trace?.Add($"t={time:F2} glass at ({contact.X:F0},{contact.Y:F0},{contact.Z:F0}) broken, speed x{k.GlassPassFactor:F2}");
                    bounceTrace?.Add(new BounceRecord(tick, contact, hit.Normal, hit.Triangle, velocity / k.GlassPassFactor, velocity));
                    var through = position + velocity * ((1f - hit.T) * stepDt);
                    position = collider.FirstHitHullIndexed(position, through, HullHalfExtents, ignore: ignore) is { } behind
                        ? Vector3.Lerp(position, through, Math.Max(0f, behind.T - 1e-3f))
                        : through;
                    continue;
                }

                var w = velocity;
                var vAfter = Bounce(w, hit.Normal, k);
                trace?.Add($"t={time:F2} contact ({contact.X:F0},{contact.Y:F0},{contact.Z:F0}) normal ({hit.Normal.X:F2},{hit.Normal.Y:F2},{hit.Normal.Z:F2}) v after ({vAfter.X:F0},{vAfter.Y:F0},{vAfter.Z:F0})");
                bounceTrace?.Add(new BounceRecord(tick, contact, hit.Normal, hit.Triangle, w, vAfter));

                // At rest only when slow AND on something that can hold the
                // grenade: a floor-facing contact, or floor within 2u under
                // the hull. A slow contact with a wall and nothing beneath is
                // an ordinary bounce (below). The sim used to slide along the
                // wall there with the sideways velocity removed; the rig's
                // tick captures show the real grenade reflecting instead
                // (dust2 top_door, 41 u/s into the door frame, out at 18 u/s
                // with the full sideways component, then over the ledge the
                // sliding sim never reached - 214u apart, 2026-09-04).
                // An edge-axis contact (a box corner on a rim) holds a
                // grenade just like a face does: requiring a face here was
                // tried on the corpus (2026-09-04) and broke 17 dust2
                // throws, 8 on mirage and 15 on ancient while fixing none.
                // Nor does a grenade need floor under its centre: requiring
                // a point of support under the hull centre (2026-09-04)
                // broke 31 dust2 throws and fixed 4. Real grenades park on
                // rims with the centre hanging past the edge.
                if (vAfter.Length() < k.StopSpeed
                    && (hit.Normal.Z > FloorNormalZ || collider.FirstHitHull(position, position + new Vector3(0f, 0f, -2f), HullHalfExtents, minNormalZ: FloorNormalZ) is not null))
                {
                    if (!k.EdgeTipping || EdgeTip(collider, position, w) is not { } tip)
                    {
                        return new TrajectoryResult(position, bounces, time + TimeStep, Lost: false, firstTouch);
                    }
                    trace?.Add($"t={time:F2} balanced on an edge at ({position.X:F0},{position.Y:F0},{position.Z:F0}), tipping ({tip.X:F2},{tip.Y:F2})");
                    velocity = tip * (k.StopSpeed * 0.3f);
                    velocity.Z -= stepG * (1f - hit.T);
                    position += velocity * ((1f - hit.T) * stepDt);
                    continue;
                }

                velocity = vAfter;
                var remainder = 1f - hit.T;
                // The rest of the step at the reflected velocity, with no
                // more gravity in this step (measured, see above). This
                // applies to wall bounces too: freezing the hull at the
                // contact point let it settle straddling thin ridges,
                // ping-ponging between their two opposing slopes.
                var next2 = position + vAfter * (remainder * stepDt);
                for (var sub = 1; ; sub++)
                {
                    if (collider.FirstHitHullIndexed(position, next2, HullHalfExtents, ignore: ignore) is not { } hit2)
                    {
                        position = next2;
                        break;
                    }
                    position = Vector3.Lerp(position, next2, Math.Max(0f, hit2.T - 1e-3f));
                    remainder *= 1f - hit2.T;
                    if (sub >= k.BouncesPerTick || remainder <= 1e-4f)
                    {
                        break;
                    }
                    // A second surface inside the same step (the wall right
                    // after the floor at a corner): reflect again and spend
                    // what is left of the step on that velocity.
                    bounces++;
                    var w2 = velocity;
                    velocity = Bounce(w2, hit2.Normal, k);
                    trace?.Add($"t={time:F2} contact ({position.X:F0},{position.Y:F0},{position.Z:F0}) normal ({hit2.Normal.X:F2},{hit2.Normal.Y:F2},{hit2.Normal.Z:F2}) v after ({velocity.X:F0},{velocity.Y:F0},{velocity.Z:F0}) (same tick)");
                    bounceTrace?.Add(new BounceRecord(tick, position, hit2.Normal, hit2.Triangle, w2, velocity));
                    next2 = position + velocity * (remainder * stepDt);
                }
            }
            time += TimeStep;
            tick++;
            tickTrace?.Add((position, velocity));
        }
        return new TrajectoryResult(position, bounces, time, Lost: true, firstTouch);
    }

    static Vector3 ClampVelocity(Vector3 v) => new(
        Math.Clamp(v.X, -MaxVelocityPerAxis, MaxVelocityPerAxis),
        Math.Clamp(v.Y, -MaxVelocityPerAxis, MaxVelocityPerAxis),
        Math.Clamp(v.Z, -MaxVelocityPerAxis, MaxVelocityPerAxis));

    static Vector3 SnapStopEpsilon(Vector3 v) => new(
        MathF.Abs(v.X) < StopEpsilon ? 0f : v.X,
        MathF.Abs(v.Y) < StopEpsilon ? 0f : v.Y,
        MathF.Abs(v.Z) < StopEpsilon ? 0f : v.Z);

    /// <summary>
    /// Backtracks from a step that ended inside solid to the boundary it crossed,
    /// returning the contact point and the axis of the crossed face (0=x, 1=y, 2=z).
    /// </summary>
    static (Vector3 Contact, int Axis) FindContact(VoxelGrid grid, Vector3 free, Vector3 solid)
    {
        var lo = 0f;
        var hi = 1f;
        for (var i = 0; i < 8; i++)
        {
            var mid = (lo + hi) / 2;
            var p = Vector3.Lerp(free, solid, mid);
            var (x, y, z) = grid.CellOf(p);
            if (grid.InBounds(x, y, z) && grid.IsSolid(grid.Index(x, y, z)))
            {
                hi = mid;
            }
            else
            {
                lo = mid;
            }
        }
        var contact = Vector3.Lerp(free, solid, lo);
        var (fx, fy, _) = grid.CellOf(contact);
        var (sx, sy, _) = grid.CellOf(Vector3.Lerp(free, solid, hi));
        if (sx != fx)
        {
            return (contact, 0);
        }
        if (sy != fy)
        {
            return (contact, 1);
        }
        return (contact, 2);
    }

    static bool HasGroundBelow(VoxelGrid grid, Vector3 p)
    {
        var (x, y, z) = grid.CellOf(p);
        return grid.InBounds(x, y, z - 1) && grid.IsSolid(grid.Index(x, y, z - 1));
    }
}
