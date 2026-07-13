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
}
