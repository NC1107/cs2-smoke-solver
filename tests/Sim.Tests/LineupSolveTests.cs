using System.Collections.Concurrent;
using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class LineupSolveTests
{
    static readonly Vector3 SolveMin = new(0, 0, 0);
    static readonly Vector3 SolveMax = new(2048, 2048, 256);

    // Immutable after voxelization, so every fact in this class reads one shared
    // ground grid instead of rebuilding it per test.
    static readonly VoxelGrid Ground2048 = BuildGround2048();

    static VoxelGrid BuildGround2048()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 2048, 0)]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(2048, 2048, 256));
    }

    // A landing zone spanning an XY rectangle. The z-cell band covers where a
    // grenade can settle above the ground layer without encoding the exact voxel
    // rest offset. Crossings can vary by x band to give lineups distinct scores.
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
                for (var dz = 0; dz <= 3; dz++)
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

    [Fact]
    public void EmptyZoneReturnsImmediatelyWithoutSimulating()
    {
        var grid = Ground2048;

        var result = LineupSolver.Solve(
            grid, new Dictionary<int, int>(), SolveMin, SolveMax, [ThrowType.Stand],
            origins: [new Vector3(256, 1024, 0)]);

        Assert.Empty(result);
    }

    [Fact]
    public void LineupsRestInsideTheZoneAndCarryTheirCellsCrossingCount()
    {
        var grid = Ground2048;
        var zone = Zone(grid, 400, 1100, 800, 1250, 3);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            yawStepDeg: 6f, pitchStepDeg: 6f, origins: [new Vector3(256, 1024, 0)]);

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
        var grid = Ground2048;
        // Crossings climb with x so lineups from different origins earn different
        // scores, exercising the crossing tie-break in the ranking.
        var zone = Zone(grid, 1000, 1600, 800, 1250, x => x < 1200 ? 1 : x < 1400 ? 5 : 9);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            yawStepDeg: 6f, pitchStepDeg: 6f,
            origins: [new Vector3(256, 1024, 0), new Vector3(512, 1024, 0), new Vector3(768, 1024, 0)]);

        Assert.True(result.Count >= 2, $"expected several lineups to rank, got {result.Count}");
        var keys = result.Select(l => (l.Bounces, -l.RestCrossings, l.FlightTime)).ToList();
        for (var i = 1; i < keys.Count; i++)
        {
            Assert.True(keys[i - 1].CompareTo(keys[i]) <= 0,
                $"sort order violated at {i}: {keys[i - 1]} then {keys[i]}");
        }
    }

    [Fact]
    public void CoverageRecordsBothProductiveAndHopelessOrigins()
    {
        var grid = Ground2048;
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
        var grid = Ground2048;
        var zone = Zone(grid, 16, 112, 16, 160, 3);
        var near = new Vector3(564, 64, 0);
        // The far origin is beyond any physically possible throw distance.
        var far = new Vector3(2040, 2040, 0);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand, ThrowType.JumpThrow],
            origins: [near, far]);

        Assert.NotEmpty(result);
        Assert.DoesNotContain(result, l => l.Feet == far);
    }

    [Fact]
    public void OriginsInTheSameBucketCollapseToOneLineup()
    {
        var grid = Ground2048;
        var zone = Zone(grid, 400, 1100, 800, 1250, 3);
        // Both origins collapse to one lineup via per-bucket winner selection.
        var origin1 = new Vector3(256, 1024, 0);
        var origin2 = new Vector3(280, 1024, 0);
        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            yawStepDeg: 6f, pitchStepDeg: 6f, origins: [origin1, origin2]);

        var lineup = Assert.Single(result);
        Assert.True(lineup.Feet == origin1 || lineup.Feet == origin2,
            $"lineup.Feet {lineup.Feet} should be one of the two origins");
    }

    [Fact]
    public void RefinementFindsAZoneTheCoarseAngleGridStepsOver()
    {
        // A wall lob: on the steep side of the arc the landing point moves
        // 100-200u per 6-degree coarse pitch step, so a small zone between two
        // coarse samples is invisible to the fixed grid. The zone sits at the
        // rest point of a pitch -62 throw, halfway between the -65 and -59
        // lattice pitches; only the near-miss refinement sweep can land in it.
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 2048, 0),
            SyntheticMeshes.WallX(1000, 0, 2048, 0, 160),
        ]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(2048, 2048, 512));
        var origin = new Vector3(256, 1024, 0);
        var eye = origin + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(ThrowType.Stand));
        var reference = GrenadeTrajectory.Simulate(grid, new ThrowSpec(eye, 0f, -62f, ThrowType.Stand, 1f));
        var zone = Zone(grid, reference.RestPoint.X, reference.RestPoint.X,
            reference.RestPoint.Y, reference.RestPoint.Y, 3);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, new Vector3(2048, 2048, 512), [ThrowType.Stand],
            yawStepDeg: 6f, pitchStepDeg: 6f, origins: [origin]);

        Assert.NotEmpty(result);
        var lineup = result[0];
        var (cx, cy, cz) = grid.CellOf(lineup.RestPoint);
        Assert.True(zone.ContainsKey(grid.Index(cx, cy, cz)),
            $"lineup rest {lineup.RestPoint} is not a zone cell");
        // The winning pitch cannot sit on the coarse lattice (-65 + 6k), or the
        // fixed grid would have found it and this test would prove nothing.
        var offLattice = MathF.Abs((lineup.PitchDeg + 65f) % 6f);
        Assert.True(offLattice > 1f && offLattice < 5f,
            $"pitch {lineup.PitchDeg} lies on the coarse lattice; refinement was not exercised");
    }

    [Fact]
    public void BucketWinnerKeepsFewestBouncesThenRichestReachableBand()
    {
        var grid = Ground2048;
        // A single origin: every lineup it produces collapses into one 64u bucket,
        // so the survivor is decided entirely by Better(). The zone bands crossings
        // by x (1 / 5 / 9), and the origin can reach band 9. Better() must keep the
        // fewest-bounce throw and, among those, the richest band. Inverting either
        // comparison changes the survivor here (bounces 4->5, crossings 9->5), so
        // the exact values pin both branches of the comparator.
        var zone = Zone(grid, 1000, 1600, 800, 1250, x => x < 1200 ? 1 : x < 1400 ? 5 : 9);

        var result = LineupSolver.Solve(
            grid, zone, SolveMin, SolveMax, [ThrowType.Stand],
            yawStepDeg: 6f, pitchStepDeg: 6f, origins: [new Vector3(256, 1024, 0)]);

        var lineup = Assert.Single(result);
        Assert.Equal(4, lineup.Bounces);
        Assert.Equal(9, lineup.RestCrossings);
    }
}
