using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class OriginsFromNavAreasTests
{
    static readonly Vector3 BoundsMin = new(0, 0, -16);
    static readonly Vector3 BoundsMax = new(1024, 1024, 256);

    static VoxelGrid FlatGround()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 1024, 0)]);
        return VoxelGrid.Build(mesh, 16f, BoundsMin, BoundsMax);
    }

    // Axis-aligned nav area corners as [x, y, z] triples, wound counter-clockwise.
    static float[][] Square(float minX, float minY, float maxX, float maxY, float z) =>
    [
        [minX, minY, z],
        [maxX, minY, z],
        [maxX, maxY, z],
        [minX, maxY, z],
    ];

    [Fact]
    public void SquareAreaSamplesOriginsInsideBoundsSnappedToGround()
    {
        var grid = FlatGround();
        var area = Square(100, 100, 500, 500, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [area], BoundsMin, BoundsMax, 32f);

        Assert.NotEmpty(origins);
        foreach (var o in origins)
        {
            Assert.True(o.X >= 100 && o.X <= 500 && o.Y >= 100 && o.Y <= 500,
                $"origin {o} falls outside the area footprint");
            Assert.True(MathF.Abs(o.Z) <= grid.VoxelSize + 0.5f,
                $"origin z={o.Z} should snap within one voxel of the z=0 ground");
        }
    }

    [Fact]
    public void AreaTooSmallForTheSampleStepFallsBackToItsCentroid()
    {
        var grid = FlatGround();
        // A 12u-wide patch: the 32u sample lattice steps clean over it, so the
        // fallback centroid is the only origin this area can contribute.
        var area = Square(33, 33, 45, 45, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [area], BoundsMin, BoundsMax, 32f);

        Assert.Single(origins);
        Assert.Equal(39f, origins[0].X, 0);
        Assert.Equal(39f, origins[0].Y, 0);
    }

    [Fact]
    public void AreasOutsideHorizontalOrVerticalBoundsContributeNothing()
    {
        var grid = FlatGround();
        var beyondMaxX = Square(2000, 100, 2100, 500, 0);
        var aboveMaxZ = Square(100, 100, 500, 500, 500);

        var result = LineupSolver.OriginsFromNavAreas(
            grid, [beyondMaxX, aboveMaxZ], BoundsMin, BoundsMax, 32f);

        Assert.Empty(result);
    }
}
