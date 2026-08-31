using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

// Origins added outside the precomputed stand-spot lattice (the exact click
// and its wall pins) must report when the player only fits crouched there, or
// the crouch-only filter in TargetSolver lets a standing throw ship from under
// a vent - released 18u above where the grenade would really leave the hand.
public class CrouchOnlyOriginsTests
{
    static readonly Vector3 RegionMin = new(0, 0, -32);
    static readonly Vector3 RegionMax = new(1024, 1024, 256);

    static (VoxelGrid Grid, TriangleCollider Collider) Scene(float? ceilingZ)
    {
        List<(float[], float[], float[], float[])> quads = [SyntheticMeshes.Ground(0, 1024, 0)];
        if (ceilingZ is { } z)
        {
            quads.Add(SyntheticMeshes.Ceiling(0, 1024, z));
        }
        var mesh = SyntheticMeshes.FromQuads(quads);
        var grid = VoxelGrid.Build(mesh, 16f, RegionMin, RegionMax);
        var collider = new TriangleCollider(mesh, RegionMin, RegionMax);
        return (grid, collider);
    }

    [Fact]
    public void AnExactClickUnderALowCeilingIsReportedCrouchOnly()
    {
        // Ceiling at 60u: above the 54u crouch hull, below the 72u standing one.
        var (grid, collider) = Scene(ceilingZ: 60f);
        var crouchOnly = new List<Vector3>();

        var origins = LineupSolver.ExactOriginWithPins(grid, collider, new Vector3(512, 512, 0), crouchOnly);

        Assert.NotEmpty(origins);
        Assert.Contains(origins[0], crouchOnly);
    }

    [Fact]
    public void AnExactClickInTheOpenIsNotCrouchOnly()
    {
        var (grid, collider) = Scene(ceilingZ: null);
        var crouchOnly = new List<Vector3>();

        var origins = LineupSolver.ExactOriginWithPins(grid, collider, new Vector3(512, 512, 0), crouchOnly);

        Assert.NotEmpty(origins);
        Assert.Empty(crouchOnly);
    }
}
