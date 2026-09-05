using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class SimulateExactTests
{
    static TriangleCollider OpenFloor(float size = 4096)
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, size, 0)]);
        return new TriangleCollider(mesh, new Vector3(0, 0, -64), new Vector3(size, size, 1200));
    }

    [Fact]
    public void RawThrowOnFlatFloorComesToRestOnTheFloor()
    {
        var collider = OpenFloor();
        var result = GrenadeTrajectory.SimulateExactRaw(collider, new Vector3(500, 2048, 64), new Vector3(400, 0, 120));

        Assert.False(result.Lost);
        Assert.True(result.Bounces >= 1, $"expected at least one bounce, got {result.Bounces}");
        Assert.True(result.FlightTime < GrenadeTrajectory.MaxFlightSeconds,
            $"flight time {result.FlightTime} should end before the cap");
        // The hull rests with its center one grenade radius above the plane
        // (plus the contact backoff), matching the measured 2.03u in telemetry.
        Assert.InRange(result.RestPoint.Z, GrenadeTrajectory.GrenadeRadius - 0.5f, GrenadeTrajectory.GrenadeRadius + 1.5f);
        Assert.NotNull(result.FirstTouch);
    }

    [Fact]
    public void HigherElasticityRestsLaterAndTravelsFurther()
    {
        var collider = OpenFloor();
        var position = new Vector3(500, 2048, 64);
        var velocity = new Vector3(400, 0, 120);
        var dead = GrenadeTrajectory.SimulateExactRaw(collider, position, velocity,
            ThrowConstants.Default with { Elasticity = 0.05f });
        var bouncy = GrenadeTrajectory.SimulateExactRaw(collider, position, velocity,
            ThrowConstants.Default with { Elasticity = 0.60f });

        Assert.False(dead.Lost);
        Assert.False(bouncy.Lost);
        Assert.True(bouncy.FlightTime > dead.FlightTime,
            $"elastic rest at {bouncy.FlightTime}s should be later than dead at {dead.FlightTime}s");
        Assert.True(bouncy.RestPoint.X > dead.RestPoint.X,
            $"elastic rest x={bouncy.RestPoint.X} should outrange dead x={dead.RestPoint.X}");
    }

    [Fact]
    public void ThrowOverEmptySpaceTerminatesAtTheFlightCapAsLost()
    {
        // The only geometry is a 64u pad nowhere near the flight path, so the
        // grenade falls forever; the integrator must cap out, not spin or NaN.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 64, 0)]);
        var collider = new TriangleCollider(mesh, new Vector3(0, 0, -64), new Vector3(4096, 4096, 1200));
        var result = GrenadeTrajectory.SimulateExactRaw(collider, new Vector3(2000, 2000, 500), new Vector3(200, 0, 50));

        Assert.True(result.Lost);
        Assert.Equal(0, result.Bounces);
        Assert.True(result.FlightTime >= GrenadeTrajectory.MaxFlightSeconds - 0.05f,
            $"expected the flight cap, got {result.FlightTime}s");
        Assert.True(float.IsFinite(result.RestPoint.X) && float.IsFinite(result.RestPoint.Y) && float.IsFinite(result.RestPoint.Z),
            $"non-finite terminal position {result.RestPoint}");
    }

    [Fact]
    public void DeriveInitialAppliesTenDegreeUpwardPitchBiasAtLevelAim()
    {
        // Aiming level (pitch 0) launches at an effective -10 degrees, so the
        // velocity must have the corresponding upward z component.
        var spec = new ThrowSpec(new Vector3(0, 0, 64), YawDeg: 0, PitchDeg: 0, ThrowType.Stand);
        var (position, velocity) = GrenadeTrajectory.DeriveInitial(spec);

        var bias = 10f * MathF.PI / 180f;
        Assert.Equal(GrenadeTrajectory.ThrowSpeed * MathF.Sin(bias), velocity.Z, 2);
        Assert.Equal(GrenadeTrajectory.ThrowSpeed * MathF.Cos(bias), velocity.X, 2);
        Assert.Equal(0f, velocity.Y, 3);
        // Release point sits 16u along the (biased) aim direction from the eye.
        Assert.Equal(64f + 16f * MathF.Sin(bias), position.Z, 3);
    }

    [Fact]
    public void DeriveInitialAddsJumpVelocityAndRunSpeed()
    {
        var k = ThrowConstants.Default;
        var eye = new Vector3(100, 200, 64);
        const float yaw = 30f;
        const float pitch = -20f;
        var (standPos, stand) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, ThrowType.Stand));
        var (jumpPos, jump) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, ThrowType.JumpThrow));
        var (_, crouchJump) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, ThrowType.CrouchJumpThrow));
        var (runPos, runJump) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, ThrowType.RunJumpThrow));

        var jumpBoost = jump - stand;
        Assert.Equal(0f, jumpBoost.X, 3);
        Assert.Equal(0f, jumpBoost.Y, 3);
        Assert.Equal(k.JumpVelocity, jumpBoost.Z, 3);
        // Crouch jump carries its own measured vertical, distinct from a stand jump.
        Assert.Equal(k.CrouchJumpVelocity, (crouchJump - stand).Z, 3);

        // A jump throw is released above the standing eye by the click's rise
        // (left click here); a grounded throw is not raised at all.
        Assert.Equal(k.ReleaseRise(1f), jumpPos.Z - standPos.Z, 3);

        var runBoost = runJump - jump;
        var yawRad = yaw * MathF.PI / 180f;
        Assert.Equal(k.RunSpeed * MathF.Cos(yawRad), runBoost.X, 2);
        Assert.Equal(k.RunSpeed * MathF.Sin(yawRad), runBoost.Y, 2);
        Assert.Equal(0f, runBoost.Z, 3);
    }

    [Fact]
    public void DeriveInitialRotatesRunVelocityWithTheMovementKey()
    {
        var k = ThrowConstants.Default;
        var eye = new Vector3(100, 200, 64);
        const float yaw = 30f;
        const float pitch = -20f;
        var (_, jump) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, ThrowType.JumpThrow));

        // Strafing left (A) carries the same speed rotated +90 degrees from the
        // facing; the diagonal (W+D) sits -45. The aim direction is unchanged -
        // only the carried player velocity moves with the key.
        foreach (var offset in (float[])[90f, -45f])
        {
            var (_, run) = GrenadeTrajectory.DeriveInitial(
                new ThrowSpec(eye, yaw, pitch, ThrowType.RunJumpThrow, RunYawOffsetDeg: offset));
            var boost = run - jump;
            var runYaw = (yaw + offset) * MathF.PI / 180f;
            Assert.Equal(k.RunSpeed * MathF.Cos(runYaw), boost.X, 2);
            Assert.Equal(k.RunSpeed * MathF.Sin(runYaw), boost.Y, 2);
            Assert.Equal(0f, boost.Z, 3);
        }
    }

    [Fact]
    public void VoxelAndExactSimulatorsAgreeOnOpenGround()
    {
        // Different integrator stages against different collision (inflated
        // voxels vs exact triangles), so agreement is loose by design. Half
        // strength keeps the impact below DampGateSpeed: the voxel model has no
        // angle-damp gate, so faster steeper impacts diverge beyond 100u.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 4096, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(4096, 4096, 1200));
        var collider = new TriangleCollider(mesh, new Vector3(0, 0, -64), new Vector3(4096, 4096, 1200));
        var spec = new ThrowSpec(new Vector3(200, 2048, 64), YawDeg: 0, PitchDeg: -35, ThrowType.Stand, Strength: 0.5f);

        var voxel = GrenadeTrajectory.Simulate(grid, spec);
        var exact = GrenadeTrajectory.SimulateExact(collider, spec);

        Assert.False(voxel.Lost);
        Assert.False(exact.Lost);
        var distance = Vector3.Distance(voxel.RestPoint, exact.RestPoint);
        Assert.True(distance <= 48f,
            $"voxel rest {voxel.RestPoint} and exact rest {exact.RestPoint} disagree by {distance:F1}u");
    }
}

/// <summary>
/// A grenade that stops balanced on the edge of a ledge stays there unless
/// tipping is switched on.
/// </summary>
// Three replays at the beam over de_dust2's mid doors suggested the engine
// tips a corner-balanced grenade off; the full corpus said otherwise (pole
// tops, crate rims and that same beam hold balanced grenades 50 times for
// every 13 they tip), so tipping is opt-in and off by default.
/// <summary>
/// A grenade that meets the floor and then the wall of a corner inside one
/// tick reflects off both in that tick; the game's telemetry at such corners
/// shows both reflections landing together, and resolving only the floor left
/// the hull parked against the wall until the next tick.
/// </summary>
public class CornerContactTests
{
    static TriangleCollider Corner() => new(SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-256, 256, 0),
        SyntheticMeshes.WallX(100, -256, 256, 0, 128), // a wall at x=100 facing the thrower
    ]), new Vector3(-256, -256, -16), new Vector3(256, 256, 256));

    // Start 3u above the floor and 1u short of the wall, moving fast into
    // the corner: the floor is met first, the wall a fraction of a tick later.
    static readonly Vector3 Start = new(95f, 0f, 5f);
    static readonly Vector3 Into = new(400f, 0f, -400f);

    [Fact]
    public void FloorThenWallInsideOneTickReflectsOffBoth()
    {
        var trace = new List<string>();
        var r = GrenadeTrajectory.SimulateExactRaw(Corner(), Start, Into, ThrowConstants.Default, trace);

        Assert.Contains(trace, line => line.Contains("(same tick)"));
        Assert.True(r.RestPoint.X < 95f, $"the wall did not send it back: x={r.RestPoint.X:F1}");
    }

    [Fact]
    public void WithOneContactPerTickTheWallWaitsForTheNextTick()
    {
        var trace = new List<string>();
        GrenadeTrajectory.SimulateExactRaw(Corner(), Start, Into, ThrowConstants.Default with { BouncesPerTick = 1 }, trace);

        Assert.DoesNotContain(trace, line => line.Contains("(same tick)"));
    }
}

/// <summary>
/// The engine steps physics twice per tick. MEASURED 2026-09-04 on 3,753
/// paired floor bounces: a contact in the first half of a tick reflects the
/// half-step velocity and then takes the second half-step's gravity; a
/// contact in the second half reflects the full-tick velocity and takes no
/// more gravity that tick. Corpus: 56 -> 38 misses over 8u, every map at
/// 94.8% or better within 3u.
/// </summary>
public class SubstepBounceTests
{
    static TriangleCollider Floor() => new(SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(-256, 256, 0)]),
        new Vector3(-256, -256, -16), new Vector3(256, 256, 256));

    // Falling at 100 u/s the hull moves 1.5625u per tick; the start height
    // above the contact height (2u, the hull half-extent) picks which half of
    // the first tick the floor is met in.
    static float TickEndVzAfterDrop(float heightAboveContact)
    {
        var ticks = new List<(Vector3 Position, Vector3 Velocity)>();
        GrenadeTrajectory.SimulateExactRaw(Floor(), new Vector3(0f, 0f, 2f + heightAboveContact), new Vector3(0f, 0f, -100f), ThrowConstants.Default, tickTrace: ticks);
        return ticks[0].Velocity.Z;
    }

    [Fact]
    public void AContactInTheFirstHalfOfATickReflectsTheHalfStepVelocityAndTakesTheSecondHalfStepsGravity()
    {
        // 0.45 x (100 + 2.5) - 2.5
        Assert.Equal(43.6f, TickEndVzAfterDrop(0.4f), 0.2f);
    }

    [Fact]
    public void AContactInTheSecondHalfOfATickReflectsTheFullTickVelocityAndTakesNoMoreGravity()
    {
        // 0.45 x (100 + 5)
        Assert.Equal(47.25f, TickEndVzAfterDrop(1.2f), 0.2f);
    }
}

/// <summary>
/// The floor-damp gate (DampGateSpeed, 690 u/s) is judged on the velocity
/// with the whole tick's gravity in it, even when the contact falls in the
/// first half-step and the reflection uses the half-step velocity. MEASURED
/// 2026-09-04 on 590 steep floor bounces between 600 and 800 u/s: the
/// full-tick speed separates damped from undamped with no exceptions.
/// Corpus: 38 -> 33 misses over 8u.
/// </summary>
public class DampGateSpeedBasisTests
{
    static TriangleCollider Floor() => new(SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(-256, 256, 0)]),
        new Vector3(-256, -256, -16), new Vector3(256, 256, 256));

    [Fact]
    public void AFirstHalfStepContactJustUnderTheGateAtHalfStepSpeedStillDamps()
    {
        // 687 u/s down: 692 with the tick's gravity (over the gate), 689.5 at
        // the half-step (under it). 1.5u above contact puts the hit in the
        // first half of the tick.
        var ticks = new List<(Vector3 Position, Vector3 Velocity)>();
        GrenadeTrajectory.SimulateExactRaw(Floor(), new Vector3(0f, 0f, 3.5f), new Vector3(0f, 0f, -687f), ThrowConstants.Default, tickTrace: ticks);

        // Damped: 0.45 x 0.5 x 689.5 - 2.5 = 152.6. Undamped would be 307.8.
        Assert.InRange(ticks[0].Velocity.Z, 148f, 158f);
    }
}

/// <summary>
/// A slow contact with a wall and nothing under the hull is an ordinary
/// bounce. The sim used to slide along the wall there, dropping the
/// component of velocity the wall had just reversed; the rig's captures on
/// dust2 top_door (2026-09-04) show the real grenade leaving the door frame
/// at the reflected velocity and going on over a ledge the slide never
/// reached. Corpus replay: 59 -> 56 misses over 8u, no map worse.
/// </summary>
public class SlowWallContactTests
{
    static TriangleCollider WallInMidAir() => new(SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-256, 256, 0),
        SyntheticMeshes.WallX(100, -256, 256, 0, 128), // a wall at x=100 facing the thrower
    ]), new Vector3(-256, -256, -16), new Vector3(256, 256, 256));

    [Fact]
    public void ASlowGrenadeMeetingAWallHighAboveTheFloorReflectsOffIt()
    {
        // 60u up, a fraction of a unit short of the wall, drifting into it at
        // 20 u/s: the rebound is well under StopSpeed and there is no floor
        // within reach of the hull.
        var ticks = new List<(Vector3 Position, Vector3 Velocity)>();
        var r = GrenadeTrajectory.SimulateExactRaw(WallInMidAir(), new Vector3(97.6f, 0f, 60f), new Vector3(20f, 8f, 0f), ThrowConstants.Default, tickTrace: ticks);

        var afterContact = ticks.Take(3).FirstOrDefault(t => t.Velocity.X < 0f);
        Assert.True(afterContact != default, "the wall never reversed the grenade");
        Assert.True(afterContact.Velocity.X <= -8f, $"reflected too weakly: vx={afterContact.Velocity.X:F1}");
        // The sideways component is scaled by the bounce, not dropped: the
        // slide kept only the tangential part of the INCOMING velocity.
        Assert.True(afterContact.Velocity.Y > 2f, $"sideways velocity lost: vy={afterContact.Velocity.Y:F1}");
        Assert.False(r.Lost);
        Assert.True(r.RestPoint.X < 96f, $"stayed against the wall: x={r.RestPoint.X:F1}");
    }
}

/// <summary>
/// Intact breakable glass (window props, func_breakable) lets a grenade
/// through at 0.40 of its speed on the same heading, and is gone for the
/// rest of that flight. Measured on cs_office, 2026-09-04: five throws
/// through intact office windows, all exactly 0.40, direction unchanged.
/// </summary>
public class GlassPassThroughTests
{
    static TriangleCollider WindowAt(float x) => new(SyntheticMeshes.FromQuads(
        [
            (SyntheticMeshes.Ground(-512, 1024, 0).Item1, SyntheticMeshes.Ground(-512, 1024, 0).Item2, SyntheticMeshes.Ground(-512, 1024, 0).Item3, SyntheticMeshes.Ground(-512, 1024, 0).Item4, (byte)0),
            (new[] { x, -128f, 0f }, new[] { x, 128f, 0f }, new[] { x, 128f, 128f }, new[] { x, -128f, 128f }, (byte)1),   // a pane facing the thrower
            (new[] { x + 2f, -128f, 0f }, new[] { x + 2f, 128f, 0f }, new[] { x + 2f, 128f, 128f }, new[] { x + 2f, -128f, 128f }, (byte)1), // and its far face
        ],
        ["default", "EntityBreakable"],
        [[], []]), new Vector3(-512, -512, -16), new Vector3(1024, 512, 256));

    static readonly Vector3 Start = new(0f, 0f, 60f);
    static readonly Vector3 Flat = new(600f, 0f, 0f);

    [Fact]
    public void AGrenadeBreaksThroughGlassAtFortyPercentSpeedOnTheSameHeading()
    {
        var ticks = new List<(Vector3 Position, Vector3 Velocity)>();
        var r = GrenadeTrajectory.SimulateExactRaw(WindowAt(100f), Start, Flat, ThrowConstants.Default, tickTrace: ticks);

        var after = ticks.First(t => t.Position.X > 104f);
        Assert.True(r.RestPoint.X > 110f, $"stopped short of the pane at x={r.RestPoint.X:F1}");
        Assert.Equal(240f, after.Velocity.X, 2f);
        Assert.Equal(0f, after.Velocity.Y, 0.01f);
        Assert.Equal(1, r.GlassBreaks);
    }

    [Fact]
    public void WithGlassSolidTheSameThrowBouncesBack()
    {
        var r = GrenadeTrajectory.SimulateExactRaw(WindowAt(100f), Start, Flat, ThrowConstants.Default with { GlassPassFactor = 0f });

        Assert.True(r.RestPoint.X < 100f, $"went through solid glass: x={r.RestPoint.X:F1}");
        Assert.Equal(0, r.GlassBreaks);
    }
}

public class EdgeTipTests
{
    static TriangleCollider Ledge() => new(SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-256, 512, 0),      // the ground
        ([0f, -256f, 64f], [100f, -256f, 64f], [100f, 256f, 64f], [0f, 256f, 64f]), // a ledge 64u up, ending at x=100
        SyntheticMeshes.WallX(100, -256, 256, 0, 64),
    ]), new Vector3(-256, -256, -16), new Vector3(512, 256, 256));

    [Fact]
    public void AGrenadeDroppedOnTheEdgeStaysBalancedByDefault()
    {
        // Centre 1u past the edge: a corner of the box still touches the top.
        var r = GrenadeTrajectory.SimulateExactRaw(Ledge(), new Vector3(101f, 0f, 70f), new Vector3(0f, 0f, 0f));

        Assert.False(r.Lost);
        Assert.Equal(66f, r.RestPoint.Z, 1f);
    }

    [Fact]
    public void WithTippingOnAGrenadeDroppedOnTheEdgeLandsBelow()
    {
        var tipping = ThrowConstants.Default with { EdgeTipping = true };
        var r = GrenadeTrajectory.SimulateExactRaw(Ledge(), new Vector3(101f, 0f, 70f), new Vector3(0f, 0f, 0f), tipping);

        Assert.False(r.Lost);
        Assert.True(r.RestPoint.Z < 10f, $"rested at z={r.RestPoint.Z:F1}, still on the ledge");
        Assert.True(r.RestPoint.X > 100f, $"tipped the wrong way: x={r.RestPoint.X:F1}");
    }

    [Fact]
    public void AGrenadeDroppedWithTheLedgeUnderItsCentreStaysOnIt()
    {
        var r = GrenadeTrajectory.SimulateExactRaw(Ledge(), new Vector3(96f, 0f, 70f), new Vector3(0f, 0f, 0f));

        Assert.False(r.Lost);
        Assert.Equal(66f, r.RestPoint.Z, 1f);
    }
}
