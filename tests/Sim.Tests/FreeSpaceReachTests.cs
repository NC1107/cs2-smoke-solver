using System.Numerics;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The free-space flood that prunes throw spots before the sweep ever runs.
/// </summary>
// This is a lower bound on how far a grenade must travel, and the sweep drops
// any origin whose bound exceeds what a throw can cover. That makes it the one
// place where a bug deletes lineups with no error of any kind - the failure
// this project keeps hitting. The cases below pin the property the prune rests
// on: the flood measures distance THROUGH OPEN AIR, so it must refuse to cross
// a wall even where the straight line is short, and must report the walked
// length rather than the shortcut.
public class FreeSpaceReachTests
{
    const float Voxel = 16f;
    const float Room = 256f;

    // A flat floor with headroom, optionally divided by a wall at y=128 that
    // runs the full width and the full height of the grid.
    static VoxelGrid Room256(bool walled)
    {
        var quads = new List<(float[], float[], float[], float[])> { SyntheticMeshes.Ground(0, Room, 0) };
        if (walled)
        {
            // Deliberately wider and taller than the grid: a wall that stopped
            // exactly at the boundary left an edge cell the flood escaped
            // through, which measured a path around rather than none at all.
            quads.Add(SyntheticMeshes.WallY(-Voxel * 4, Room + Voxel * 4, Room / 2, -Voxel * 4, 512));
        }
        return VoxelGrid.Build(
            SyntheticMeshes.FromQuads(quads),
            Voxel,
            new Vector3(0, 0, 0),
            // Capped well below the wall's top so nothing can route over it.
            new Vector3(Room, Room, 96));
    }

    static int CellAt(VoxelGrid grid, float x, float y, float z)
    {
        var (cx, cy, cz) = grid.CellOf(new Vector3(x, y, z));
        return grid.Index(cx, cy, cz);
    }

    [Fact]
    public void OpenGroundMeasuresTheWalkedDistance()
    {
        var grid = Room256(walled: false);
        var field = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 32, 48)], 4000f);

        var near = field.DistanceFrom(new Vector3(128, 64, 48));
        var far = field.DistanceFrom(new Vector3(128, 224, 48));

        Assert.NotNull(near);
        Assert.NotNull(far);
        Assert.True(far > near, "further away must measure further");
        // Six-connected steps of one voxel each, so the distance tracks the
        // real separation rather than being an arbitrary count.
        Assert.InRange(far!.Value, 224f - 32f - Voxel * 2, 224f - 32f + Voxel * 2);
    }

    [Fact]
    public void AWallIsNotCrossedEvenThoughTheStraightLineIsShort()
    {
        // The whole point of the class: 96u apart in a straight line, no open
        // path at all. A straight-line prune would happily keep this origin.
        var grid = Room256(walled: true);
        var field = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 80, 48)], 4000f);

        var sameSide = field.DistanceFrom(new Vector3(128, 48, 48));
        var throughWall = field.DistanceFrom(new Vector3(128, 176, 48));

        Assert.NotNull(sameSide);
        Assert.Null(throughWall);
    }

    [Fact]
    public void PointsBeyondTheBudgetAreUnreachable()
    {
        var grid = Room256(walled: false);
        // A budget far shorter than the room: the far end is reachable in
        // principle and must still be refused, or the prune is a no-op.
        var field = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 32, 48)], 48f);

        Assert.NotNull(field.DistanceFrom(new Vector3(128, 48, 48)));
        Assert.Null(field.DistanceFrom(new Vector3(128, 240, 48)));
    }

    [Fact]
    public void TwoSeedsBothActAsSourcesAndTheNearerWins()
    {
        var grid = Room256(walled: false);
        var one = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 32, 48)], 4000f);
        var both = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 32, 48), CellAt(grid, 128, 224, 48)], 4000f);

        var probe = new Vector3(128, 208, 48);
        Assert.NotNull(one.DistanceFrom(probe));
        Assert.NotNull(both.DistanceFrom(probe));
        Assert.True(both.DistanceFrom(probe) < one.DistanceFrom(probe),
            "a second seed near the probe must shorten its distance, not be ignored");
    }

    [Fact]
    public void OutOfBoundsPointsHaveNoDistance()
    {
        var grid = Room256(walled: false);
        var field = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 32, 48)], 4000f);

        Assert.Null(field.DistanceFrom(new Vector3(-5000, 128, 48)));
        Assert.Null(field.DistanceFrom(new Vector3(128, 128, 100000)));
    }

    [Fact]
    public void ASolidSeedCellDoesNotLeakTheFloodIntoGeometry()
    {
        // Zones are built around a point, not carved out of the map, so a seed
        // can land inside a wall. Flooding from there would hand out distances
        // for cells on the far side that no grenade can travel to.
        var grid = Room256(walled: true);
        var insideWall = CellAt(grid, 128, Room / 2, 48);
        Assert.True(grid.IsSolid(insideWall), "test setup: the seed should be inside the wall");

        var field = FreeSpaceReach.Build(grid, [insideWall], 4000f);

        Assert.Null(field.DistanceFrom(new Vector3(128, 48, 48)));
        Assert.Null(field.DistanceFrom(new Vector3(128, 208, 48)));
    }

    [Fact]
    public void SolidCellsThemselvesAreNeverReachable()
    {
        var grid = Room256(walled: true);
        var field = FreeSpaceReach.Build(grid, [CellAt(grid, 128, 80, 48)], 4000f);

        Assert.Null(field.DistanceFrom(new Vector3(128, Room / 2, 48)));
    }
}
