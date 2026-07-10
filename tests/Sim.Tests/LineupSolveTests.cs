using System.Collections.Concurrent;
using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class LineupSolveTests
{
    static readonly Vector3 SolveMin = new(0, 0, 0);
    static readonly Vector3 SolveMax = new(2048, 2048, 256);

    static VoxelGrid Ground2048()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 2048, 0)]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(2048, 2048, 256));
    }

    // A landing zone spanning an XY rectangle. Solve gates rest cells with no z
    // tolerance, so cover the ground cell and the two above it; a grenade rests
    // one to two voxels up depending on which face of the ground layer it settles
    // against. Crossings can vary by x band to give lineups distinct scores.
    static Dictionary<int, int> Zone(
        VoxelGrid grid, float minX, float maxX, float minY, float maxY, Func<float, int> crossings)
    {
        var zone = new Dictionary<int, int>();
        var (_, _, gz) = grid.CellOf(new Vector3(minX, minY, 0f));
        for (var x = minX; x <= maxX; x += grid.VoxelSize)
        {
            for (var y = minY; y <= maxY; y += grid.VoxelSize)
            {
                var (cx, cy, _) = grid.CellOf(new Vector3(x, y, 0f));
                var value = crossings(x);
                for (var dz = 0; dz <= 2; dz++)
                {
                    if (grid.InBounds(cx, cy, gz + dz))
                    {
                        zone[grid.Index(cx, cy, gz + dz)] = value;
                    }
                }
            }
        }
        return zone;
    }

    static Dictionary<int, int> Zone(VoxelGrid grid, float minX, float maxX, float minY, float maxY, int crossings) =>
        Zone(grid, minX, maxX, minY, maxY, _ => crossings);

    static (int X, int Y) Bucket(Vector3 feet) =>
        ((int)MathF.Floor(feet.X / 64f), (int)MathF.Floor(feet.Y / 64f));

    [Fact]
    public void EmptyZoneReturnsImmediatelyWithoutSimulating()
    {
        var grid = Ground2048();

        var result = LineupSolver.Solve(
            grid, new Dictionary<int, int>(), SolveMin, SolveMax, [ThrowType.Stand],
            origins: [new Vector3(256, 1024, 0)]);

        Assert.Empty(result);
    }

    [Fact]
    public void LineupsRestInsideTheZoneAndCarryTheirCellsCrossingCount()
    {
        var grid = Ground2048();
        var zone = Zone(grid, 400, 1100, 800, 1250, 3);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            origins: [new Vector3(256, 1024, 0)]);

        Assert.NotEmpty(result);
        foreach (var lineup in result)
        {
            var (cx, cy, cz) = grid.CellOf(lineup.RestPoint);
            Assert.True(zone.ContainsKey(grid.Index(cx, cy, cz)),
                $"lineup rest {lineup.RestPoint} is not a zone cell");
            Assert.Equal(3, lineup.RestCrossings);
        }
    }

    [Fact]
    public void ResultsAreOrderedByBouncesThenCrossingsThenFlightTime()
    {
        var grid = Ground2048();
        // Crossings climb with x so lineups from different origins earn different
        // scores, exercising the crossing tie-break in the ranking.
        var zone = Zone(grid, 1000, 1600, 800, 1250, x => x < 1200 ? 1 : x < 1400 ? 5 : 9);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            origins: [new Vector3(256, 1024, 0), new Vector3(512, 1024, 0), new Vector3(768, 1024, 0)]);

        Assert.True(result.Count >= 2, $"expected several lineups to rank, got {result.Count}");
        for (var i = 1; i < result.Count; i++)
        {
            var prev = result[i - 1];
            var cur = result[i];
            Assert.True(prev.Bounces <= cur.Bounces,
                $"bounces out of order at {i}: {prev.Bounces} then {cur.Bounces}");
            if (prev.Bounces == cur.Bounces)
            {
                Assert.True(prev.RestCrossings >= cur.RestCrossings,
                    $"crossings out of order at {i}: {prev.RestCrossings} then {cur.RestCrossings}");
                if (prev.RestCrossings == cur.RestCrossings)
                {
                    Assert.True(prev.FlightTime <= cur.FlightTime,
                        $"flight time out of order at {i}: {prev.FlightTime} then {cur.FlightTime}");
                }
            }
        }
    }

    [Fact]
    public void CoverageRecordsBothProductiveAndHopelessOrigins()
    {
        var grid = Ground2048();
        var zone = Zone(grid, 150, 450, 150, 450, 3);
        var near = new Vector3(700, 300, 0);
        // Farther than any Stand throw can carry (distance ~2100u > 2000u range),
        // so every angle is pruned yet the origin is still counted.
        var hopeless = new Vector3(1900, 1700, 0);
        var coverage = new ConcurrentDictionary<(int X, int Y), int>();

        LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            origins: [near, hopeless], coverage: coverage);

        Assert.True(coverage.TryGetValue((700, 300), out var nearHits) && nearHits > 0,
            "near origin should record a positive hit count");
        Assert.True(coverage.TryGetValue((1900, 1700), out var farHits) && farHits == 0,
            "hopeless origin should be recorded with zero hits");
    }

    [Fact]
    public void OriginBeyondEveryThrowRangeProducesNoLineups()
    {
        var grid = Ground2048();
        var zone = Zone(grid, 16, 112, 16, 160, 3);
        var near = new Vector3(564, 64, 0);
        // ~2780u from the zone: past even the JumpThrow reach of 2700u.
        var far = new Vector3(2040, 2040, 0);
        var coverage = new ConcurrentDictionary<(int X, int Y), int>();

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand, ThrowType.JumpThrow],
            origins: [near, far], coverage: coverage);

        Assert.NotEmpty(result);
        Assert.DoesNotContain(result, l => Bucket(l.Feet) == Bucket(far));
        Assert.True(coverage.TryGetValue((2040, 2040), out var farHits) && farHits == 0,
            "the out-of-range origin should record zero hits");
        Assert.True(coverage.TryGetValue((564, 64), out var nearHits) && nearHits > 0,
            "the in-range origin should record hits");
    }

    [Fact]
    public void OriginsInTheSameBucketCollapseToOneLineup()
    {
        var grid = Ground2048();
        var zone = Zone(grid, 400, 1100, 800, 1250, 3);
        // Both feet floor to bucket (4, 16), so the per-bucket winner map keeps
        // at most one of them.
        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            origins: [new Vector3(256, 1024, 0), new Vector3(280, 1024, 0)]);

        Assert.True(result.Count <= 1, $"same-bucket origins should not both survive, got {result.Count}");
        foreach (var lineup in result)
        {
            Assert.Equal((4, 16), Bucket(lineup.Feet));
        }
    }
}
