using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class NavGroundZTests
{
    // Axis-aligned nav area corners as [x, y, z] triples, wound counter-clockwise.
    static float[][] Square(float minX, float minY, float maxX, float maxY, float z) =>
    [
        [minX, minY, z],
        [maxX, minY, z],
        [maxX, maxY, z],
        [minX, maxY, z],
    ];

    [Fact]
    public void PointInsideConvexAreaReturnsAreaZ()
    {
        var areas = new List<float[][]> { Square(0, 0, 100, 100, 10) };

        var z = LineupSolver.NavGroundZ(areas, 50, 50);

        Assert.Equal(10f, z);
    }

    [Fact]
    public void PointOutsideEveryAreaReturnsNull()
    {
        var areas = new List<float[][]> { Square(0, 0, 100, 100, 10) };

        Assert.Null(LineupSolver.NavGroundZ(areas, 200, 200));
    }

    [Fact]
    public void ConcaveAreaExcludesTheNotchButKeepsBothArms()
    {
        // L-shaped area: the bottom arm covers y in [0,40], the left arm covers
        // x in [0,40]; the square notch x in [40,100], y in [40,100] is outside.
        var lShape = new List<float[][]>
        {
            new float[][]
            {
                [0, 0, 5],
                [100, 0, 5],
                [100, 40, 5],
                [40, 40, 5],
                [40, 100, 5],
                [0, 100, 5],
            },
        };

        Assert.Null(LineupSolver.NavGroundZ(lShape, 80, 80));

        var leftArm = LineupSolver.NavGroundZ(lShape, 20, 80);
        Assert.Equal(5f, leftArm);

        var bottomArm = LineupSolver.NavGroundZ(lShape, 80, 20);
        Assert.Equal(5f, bottomArm);
    }

    [Fact]
    public void StackedAreasReturnTheLowestGround()
    {
        // A walkable floor and a walkway directly above it: a player standing in
        // this column is on the lower surface, so the lower z must win.
        var areas = new List<float[][]>
        {
            Square(0, 0, 100, 100, 64),
            Square(0, 0, 100, 100, 0),
        };

        var z = LineupSolver.NavGroundZ(areas, 50, 50);

        Assert.Equal(0f, z);
    }

    [Fact]
    public void AClickInTheSliverBetweenTwoAreasTakesTheirHeightNotTheRoof()
    {
        // Nav polygons do not tile the floor perfectly. A click landing in the
        // gap between two of them has no containing area, and falling through
        // to a top-down geometry scan finds the ROOF - which put de_dust2's
        // BombsiteA and BombsiteB ~900u in the air and returned zero lineups
        // for the two most-thrown-at spots on the map.
        var areas = new List<float[][]>
        {
            new float[][] { [0, 0, 10], [100, 0, 10], [100, 100, 10], [0, 100, 10] },
            new float[][] { [140, 0, 10], [240, 0, 10], [240, 100, 10], [140, 100, 10] },
        };

        Assert.Null(LineupSolver.NavGroundZ(areas, 120, 50));
        Assert.Equal(10f, LineupSolver.NavGroundZNearby(areas, 120, 50));
    }

    [Fact]
    public void AClickFarFromAnyNavAreaStillHasNoGroundHeight()
    {
        // The forgiving lookup must stay local: somewhere genuinely off the
        // mesh has no answer, rather than snapping to a distant area.
        var areas = new List<float[][]>
        {
            new float[][] { [0, 0, 10], [100, 0, 10], [100, 100, 10], [0, 100, 10] },
        };

        Assert.Null(LineupSolver.NavGroundZNearby(areas, 900, 900));
    }
}
