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
