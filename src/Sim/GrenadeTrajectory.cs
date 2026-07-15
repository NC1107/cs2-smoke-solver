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
/// </summary>
// Struct: allocated once per simulated throw, and solves run millions of
// simulations inside Parallel.ForEach - heap records were pure gen0 churn.
public readonly record struct ThrowSpec(Vector3 EyePosition, float YawDeg, float PitchDeg, ThrowType Type, float Strength = 1f);

public readonly record struct TrajectoryResult(Vector3 RestPoint, int Bounces, float FlightTime, bool Lost, Vector3? FirstTouch = null);

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
    // (684.1, 696.6]; 689 = 17.5 m/s. Not in the SDK-era code; measured in CS2.
    float DampGateSpeed = 689f,
    // Vertical velocity a jump adds to the throw. MEASURED at 273.6 (12 live
    // jump throws, spread 0.2), not the 300 a naive "release with full jump
    // velocity" model assumes: the grenade leaves the hand several ticks into
    // the jump, by which point the rise has bled the vertical speed. A crouch
    // jump releases a touch higher up its own arc, so it carries 277.5.
    float JumpVelocity = 273.6f,
    float CrouchJumpVelocity = 277.5f,
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
    const float BaseGravity = 800f;

    // A contact normal with z at or above this is "floor" for bounce and rest
    // decisions; below it the surface is a wall/ramp.
    const float FloorNormalZ = 0.7f;

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

        var gravity = BaseGravity * k.GravityScale;
        var bounces = 0;
        var time = 0f;
        Vector3? firstTouch = null;

        while (time < MaxFlightSeconds)
        {
            var vzOld = velocity.Z;
            velocity.Z -= gravity * TimeStep;
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
    static readonly Vector3 HullHalfExtents = new(2f, 2f, 2f);

    /// <summary>
    /// Initial projectile state for a throw spec: release position (eye plus
    /// 16u along the aim direction) and launch velocity (pitch-biased aim,
    /// per-click speed, jump/run additions). Shared by the simulators and by
    /// the live validation pipeline, which feeds these exact values to the
    /// real server so sim and game start from identical conditions.
    /// </summary>
    public static (Vector3 Position, Vector3 Velocity) DeriveInitial(ThrowSpec spec, ThrowConstants? constants = null)
    {
        var k = constants ?? ThrowConstants.Default;
        var effectivePitch = spec.PitchDeg - (90f - MathF.Abs(spec.PitchDeg)) / 90f * 10f;
        var pitch = effectivePitch * MathF.PI / 180f;
        var yaw = spec.YawDeg * MathF.PI / 180f;
        var forward = new Vector3(
            MathF.Cos(pitch) * MathF.Cos(yaw),
            MathF.Cos(pitch) * MathF.Sin(yaw),
            -MathF.Sin(pitch));

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
            velocity += new Vector3(MathF.Cos(yaw), MathF.Sin(yaw), 0) * k.RunSpeed;
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
    public static TrajectoryResult SimulateExactRaw(TriangleCollider collider, Vector3 position, Vector3 velocity, ThrowConstants? constants = null, List<string>? trace = null, List<(Vector3 Position, Vector3 Velocity)>? tickTrace = null)
    {
        var k = constants ?? ThrowConstants.Default;
        var gravityStep = BaseGravity * k.GravityScale * TimeStep;
        var bounces = 0;
        var time = 0f;
        Vector3? firstTouch = null;

        while (time < MaxFlightSeconds)
        {
            velocity = ClampVelocity(velocity);
            var vzOld = velocity.Z;
            velocity.Z -= gravityStep;
            var move = new Vector3(velocity.X, velocity.Y, (vzOld + velocity.Z) * 0.5f) * TimeStep;
            var next = position + move;

            if (collider.FirstHitHull(position, next, HullHalfExtents) is { } hit)
            {
                var contact = Vector3.Lerp(position, next, Math.Max(0f, hit.T - 1e-3f));
                position = contact;
                firstTouch ??= contact;
                bounces++;

                var w = velocity;
                var speed = w.Length();
                var reflected = SnapStopEpsilon(w - 2f * Vector3.Dot(w, hit.Normal) * hit.Normal);
                var u = speed > 1e-6f ? MathF.Abs(Vector3.Dot(w, hit.Normal)) / speed : 0f;
                var damp = FloorImpactDamp(speed, u, isFloor: hit.Normal.Z > FloorNormalZ, k);
                var vAfter = reflected * (k.Elasticity * damp);
                trace?.Add($"t={time:F2} contact ({contact.X:F0},{contact.Y:F0},{contact.Z:F0}) normal ({hit.Normal.X:F2},{hit.Normal.Y:F2},{hit.Normal.Z:F2}) v after ({vAfter.X:F0},{vAfter.Y:F0},{vAfter.Z:F0})");

                if (vAfter.Length() < k.StopSpeed)
                {
                    if (hit.Normal.Z > FloorNormalZ)
                    {
                        return new TrajectoryResult(position, bounces, time + TimeStep, Lost: false, firstTouch);
                    }
                    // A wall or edge contact at negligible speed can still be a
                    // grenade at rest ON the floor (wedged against a tilted
                    // wall, or settling onto a triangle edge); probe straight
                    // down considering only floor-like surfaces before treating
                    // it as a wall slide.
                    if (collider.FirstHitHull(position, position + new Vector3(0f, 0f, -2f), HullHalfExtents, minNormalZ: FloorNormalZ) is not null)
                    {
                        return new TrajectoryResult(position, bounces, time + TimeStep, Lost: false, firstTouch);
                    }
                    // Slow wall contact with no floor beneath: slide along the
                    // surface instead of stopping dead. Zeroing the velocity
                    // here froze the hull permanently against near-vertical
                    // walls (each subsequent fall tick re-grazed the wall at
                    // the contact point); real grenades slide down to the
                    // ground in this situation.
                    velocity = w - Vector3.Dot(w, hit.Normal) * hit.Normal;
                    var slideRemainder = 1f - hit.T;
                    velocity.Z -= gravityStep * slideRemainder;
                    var slideNext = position + velocity * (slideRemainder * TimeStep);
                    position = collider.FirstHitHull(position, slideNext, HullHalfExtents) is { } slideHit
                        ? Vector3.Lerp(position, slideNext, Math.Max(0f, slideHit.T - 1e-3f))
                        : slideNext;
                }
                else
                {
                    velocity = vAfter;
                    var remainder = 1f - hit.T;
                    velocity.Z -= gravityStep * remainder;
                    // Consume the remainder of the tick with the bounced
                    // velocity (no second bounce resolution within the tick).
                    // This applies to wall bounces too: CS2 telemetry shows the
                    // projectile keeps moving through the bounce tick, and
                    // freezing it at the contact point let the hull settle
                    // straddling thin ridges, ping-ponging between their two
                    // opposing slopes for hundreds of fake bounces.
                    var next2 = position + vAfter * (remainder * TimeStep);
                    position = collider.FirstHitHull(position, next2, HullHalfExtents) is { } hit2
                        ? Vector3.Lerp(position, next2, Math.Max(0f, hit2.T - 1e-3f))
                        : next2;
                }
            }
            else
            {
                position = next;
            }
            time += TimeStep;
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
        var (fx, fy, fz) = grid.CellOf(contact);
        var (sx, sy, sz) = grid.CellOf(Vector3.Lerp(free, solid, hi));
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
