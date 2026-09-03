using System.Numerics;
using SmokeSolver.Cli;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The last-resort height for a 2D click the nav mesh cannot answer for.
/// </summary>
// This scan used to run down from the sky and return the first floor it met,
// which is the roof - the bug that put de_dust2's BombsiteA and BombsiteB ~900u
// in the air and returned zero lineups for the two most-thrown-at spots on the
// map. The 96u nav-gap bridging added afterwards keeps most clicks away from
// this fallback but never fixed the fallback, so these cases pin the fallback
// itself: it is reached by any click further than that gap from a nav polygon.
public class SnapTargetToGroundTests
{
    const float Voxel = 16f;

    // A room with a floor and a roof over the same column - the shape that made
    // a top-down scan answer with the roof.
    static VoxelGrid FloorAndRoof(float floorZ, float roofZ) =>
        VoxelGrid.Build(
            SyntheticMeshes.FromQuads([
                SyntheticMeshes.Ground(0, 256, floorZ),
                SyntheticMeshes.Ceiling(0, 256, roofZ),
            ]),
            Voxel,
            new Vector3(0, 0, floorZ - 64),
            new Vector3(256, 256, roofZ + 64));

    static (int X, int Y) Column(VoxelGrid grid, float x, float y)
    {
        var (cx, cy, _) = grid.CellOf(new Vector3(x, y, grid.Origin.Z + Voxel));
        return (cx, cy);
    }

    [Fact]
    public void PicksTheFloorNotTheRoof()
    {
        var grid = FloorAndRoof(0f, 512f);
        var (x, y) = Column(grid, 128, 128);

        var hit = TargetSolver.SnapTargetToGround(grid, x, y);

        Assert.NotNull(hit);
        Assert.True(hit!.Value.Z < 64f,
            $"expected the floor near z=0, got {hit.Value.Z:F1} (the roof sits at 512)");
    }

    [Fact]
    public void WithNoAnchorTheLowestStackedFloorWins()
    {
        // Two walkable slabs in one column, no roof: the lower is the one a
        // player stands on, matching NavGroundZ's stacked-areas rule.
        var grid = VoxelGrid.Build(
            SyntheticMeshes.FromQuads([
                SyntheticMeshes.Ground(0, 256, 0f),
                SyntheticMeshes.Ground(0, 256, 256f),
            ]),
            Voxel,
            new Vector3(0, 0, -64),
            new Vector3(256, 256, 384));
        var (x, y) = Column(grid, 128, 128);

        var hit = TargetSolver.SnapTargetToGround(grid, x, y);

        Assert.NotNull(hit);
        Assert.True(hit!.Value.Z < 64f, $"expected the lower slab near z=0, got {hit!.Value.Z:F1}");
    }

    [Theory]
    [InlineData(300f, 256f)]
    [InlineData(20f, 0f)]
    public void AnAnchorChoosesWhichStackedFloorTheClickMeant(float anchor, float expected)
    {
        var grid = VoxelGrid.Build(
            SyntheticMeshes.FromQuads([
                SyntheticMeshes.Ground(0, 256, 0f),
                SyntheticMeshes.Ground(0, 256, 256f),
            ]),
            Voxel,
            new Vector3(0, 0, -64),
            new Vector3(256, 256, 384));
        var (x, y) = Column(grid, 128, 128);

        var hit = TargetSolver.SnapTargetToGround(grid, x, y, anchor);

        Assert.NotNull(hit);
        Assert.True(MathF.Abs(hit!.Value.Z - expected) < Voxel * 2,
            $"anchor {anchor} should have chosen the slab at {expected}, got {hit!.Value.Z:F1}");
    }

    [Fact]
    public void AColumnWithNoGeometryHasNoGround()
    {
        var grid = FloorAndRoof(0f, 512f);
        // Outside the 0..256 quads but still inside the grid.
        var (x, y) = Column(grid, 250, 250);
        var empty = TargetSolver.SnapTargetToGround(grid, Math.Min(x + 4, grid.Nx - 1), y);

        // Either off the slab (null) or on it - what must never happen is a
        // height read off the roof.
        if (empty is { } hit)
        {
            Assert.True(hit.Z < 64f, $"got {hit.Z:F1}, which is the roof");
        }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(int.MaxValue, 0)]
    public void OutOfBoundsColumnsReturnNull(int x, int y)
    {
        var grid = FloorAndRoof(0f, 512f);
        Assert.Null(TargetSolver.SnapTargetToGround(grid, x, y));
    }
}

/// <summary>
/// A 3D-clicked target settles to where a grenade could rest: the floor
/// under the click, a grenade's width out from a wall it touched.
/// </summary>
public class SettleTargetTests
{
    static readonly Vector3 Min = new(-64, -64, -32);
    static readonly Vector3 Max = new(512, 512, 256);

    static readonly CollisionMesh RoomMesh = SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-64, 512, 0),
        SyntheticMeshes.WallY(-64, 512, 200, 0, 128),
        // A balcony slab over the far corner: underside at 96, top at 112.
        ([300f, 300f, 96f], [500f, 300f, 96f], [500f, 500f, 96f], [300f, 500f, 96f]),
        ([300f, 300f, 112f], [500f, 300f, 112f], [500f, 500f, 112f], [300f, 500f, 112f]),
    ]);
    static TriangleCollider Room() => new(RoomMesh, Min, Max);
    static VoxelGrid Grid() => VoxelGrid.Build(RoomMesh, 16f, Min, Max);

    [Fact]
    public void AClickHalfwayUpAWallGoesToItsFoot()
    {
        // Clicking the same corner twice and getting nothing: the click had
        // landed on the crate's side, forty units up, where no smoke rests.
        var settled = TargetSolver.SettleTarget(Room(), Grid(), new Vector3(100, 200, 40));

        Assert.Equal(0f, settled.Z, 1f);
        Assert.True(MathF.Abs(settled.Y - 200) >= 6f, $"still against the wall at y={settled.Y:F1}");
        Assert.Equal(100f, settled.X, 0.5f);
    }

    [Fact]
    public void AClickOnOpenFloorStaysWhereItIs()
    {
        var settled = TargetSolver.SettleTarget(Room(), Grid(), new Vector3(100, 100, 0));

        Assert.Equal(100f, settled.X, 0.01f);
        Assert.Equal(100f, settled.Y, 0.01f);
        Assert.Equal(0f, settled.Z, 0.01f);
    }

    [Fact]
    public void AClickOnTheUndersideOfABalconyGoesToTheGroundBeneathIt()
    {
        // The raycaster faces every normal toward the ray, so a probe fired
        // from inside the slab saw the underside as a floor and left the
        // target at ceiling height (review finding).
        var settled = TargetSolver.SettleTarget(Room(), Grid(), new Vector3(400, 400, 96));

        Assert.Equal(0f, settled.Z, 1f);
    }

    [Fact]
    public void AClickOnTopOfTheBalconyStaysOnIt()
    {
        var settled = TargetSolver.SettleTarget(Room(), Grid(), new Vector3(400, 400, 112));

        Assert.Equal(112f, settled.Z, 1f);
    }

    [Fact]
    public void AClickOnTheFloorAgainstAWallIsNudgedOut()
    {
        var settled = TargetSolver.SettleTarget(Room(), Grid(), new Vector3(100, 197, 0));

        Assert.True(settled.Y <= 192.5f, $"y={settled.Y:F1}");
        Assert.Equal(0f, settled.Z, 1f);
    }
}
