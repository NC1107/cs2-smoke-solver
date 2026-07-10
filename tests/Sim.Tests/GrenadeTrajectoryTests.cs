using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class GrenadeTrajectoryTests
{
    static VoxelGrid OpenGround(float size = 4096)
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, size, 0)]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(size, size, 1200));
    }

    [Fact]
    public void ForwardThrowLandsDownRangeAndRests()
    {
        var grid = OpenGround();
        var spec = new ThrowSpec(new Vector3(200, 2048, 64), YawDeg: 0, PitchDeg: -35, ThrowType.Stand);
        var result = GrenadeTrajectory.Simulate(grid, spec);

        Assert.False(result.Lost);
        Assert.True(result.RestPoint.X > 800, $"landed at x={result.RestPoint.X}, expected a long flight");
        Assert.True(result.RestPoint.Z < 32, $"rest z={result.RestPoint.Z}, expected on the ground");
        Assert.True(result.Bounces >= 1);
    }

    [Fact]
    public void HigherPitchFliesFurtherUpToFortyFiveDegrees()
    {
        var grid = OpenGround();
        var flat = GrenadeTrajectory.Simulate(grid, new ThrowSpec(new Vector3(200, 2048, 64), 0, -5, ThrowType.Stand));
        var lofted = GrenadeTrajectory.Simulate(grid, new ThrowSpec(new Vector3(200, 2048, 64), 0, -40, ThrowType.Stand));

        Assert.True(lofted.RestPoint.X > flat.RestPoint.X,
            $"lofted {lofted.RestPoint.X} should outrange flat {flat.RestPoint.X}");
    }

    [Fact]
    public void JumpThrowOutrangesStandingThrow()
    {
        var grid = OpenGround();
        var stand = GrenadeTrajectory.Simulate(grid, new ThrowSpec(new Vector3(200, 2048, 64), 0, -35, ThrowType.Stand));
        var jump = GrenadeTrajectory.Simulate(grid, new ThrowSpec(new Vector3(200, 2048, 64), 0, -35, ThrowType.JumpThrow));

        Assert.True(jump.RestPoint.X > stand.RestPoint.X,
            $"jumpthrow {jump.RestPoint.X} should outrange stand {stand.RestPoint.X}");
    }

    [Fact]
    public void WallStopsTheGrenadeInFrontOfIt()
    {
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 4096, 0),
            SyntheticMeshes.WallX(2600, 0, 4096, 0, 800),
        ]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(4096, 4096, 1200));
        var result = GrenadeTrajectory.Simulate(grid, new ThrowSpec(new Vector3(2000, 2048, 64), 0, -20, ThrowType.Stand));

        Assert.False(result.Lost);
        Assert.True(result.RestPoint.X < 2600, $"rest x={result.RestPoint.X}, expected stopped before the wall");
    }
}
