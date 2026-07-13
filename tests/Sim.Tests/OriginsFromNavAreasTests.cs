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
        // Author the area 24u above true ground - inside SnapToGround's downward
        // scan. A working snap pulls every origin down onto the voxel ground (one
        // voxel, ~16u), well below the authored 24u; a no-op snap would leave them
        // stranded at 24u and fail the bound below.
        var area = Square(100, 100, 500, 500, 24);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [area], BoundsMin, BoundsMax, 32f);

        Assert.NotEmpty(origins);
        foreach (var o in origins)
        {
            Assert.True(o.X >= 100 && o.X <= 500 && o.Y >= 100 && o.Y <= 500,
                $"origin {o} falls outside the area footprint");
            Assert.True(o.Z <= grid.VoxelSize + 0.5f,
                $"origin z={o.Z} should snap onto the voxel ground, not stay at the authored 24u");
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
        // Inside the XY footprint but its plane sits below BoundsMin.Z, so the
        // avgZ < min.Z guard must reject it just like the too-high area.
        var belowMinZ = Square(100, 100, 500, 500, BoundsMin.Z - 10);

        var result = LineupSolver.OriginsFromNavAreas(
            grid, [beyondMaxX, aboveMaxZ, belowMinZ], BoundsMin, BoundsMax, 32f);

        Assert.Empty(result);
    }

    [Fact]
    public void FallbackCentroidOutsideBoundsContributesNothing()
    {
        var grid = FlatGround();
        // A tiny patch straddling BoundsMin.X=0: its AABB overlaps the region and
        // no 32u sample lands inside, so the code falls back to the centroid. But
        // the centroid x is -4, outside the bounds, so the fallback's own guard
        // must reject it - dropping the guard would emit an out-of-bounds origin.
        var straddling = Square(-12, 100, 4, 116, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [straddling], BoundsMin, BoundsMax, 32f);

        Assert.Empty(origins);
    }
}
