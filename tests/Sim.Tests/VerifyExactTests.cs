using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class VerifyExactTests
{
    static readonly Vector3 RegionMin = new(0, 0, -64);
    static readonly Vector3 RegionMax = new(4096, 4096, 1200);

    // The flatground grid and collider are immutable after construction, so the
    // whole class shares one build rather than voxelizing a 258x258x81 grid per
    // test; xunit's per-class parallelism only reads them.
    static readonly (VoxelGrid Grid, TriangleCollider Collider) FlatScene = BuildFlatScene();

    static (VoxelGrid Grid, TriangleCollider Collider) BuildFlatScene()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 4096, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, RegionMin, RegionMax);
        var collider = new TriangleCollider(mesh, RegionMin, RegionMax);
        return (grid, collider);
    }

    // A grounded standing throw over open flatground: gentle downward pitch aimed
    // at empty terrain, so the exact sphere cast settles cleanly and small angle
    // perturbations land right next to each other.
    static Lineup Candidate() =>
        new(new Vector3(500, 2048, 0), YawDeg: 0f, PitchDeg: -30f, ThrowType.Stand, Vector3.Zero,
            Bounces: 1, FlightTime: 1f, RestCrossings: 3, Strength: 1f);

    static Vector3 ExactRest(TriangleCollider collider, Lineup c) =>
        GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(
            c.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(c.Type)),
            c.YawDeg, c.PitchDeg, c.Type, c.Strength)).RestPoint;

    // The zone the verifier is asked about: the exact rest cell plus its 8 XY
    // neighbours across z-1..z+1, so the reference throw and its small nudges all
    // count as landing inside.
    static Dictionary<int, int> ZoneFromExact(VoxelGrid grid, TriangleCollider collider, Lineup c)
    {
        var (x, y, z) = grid.CellOf(ExactRest(collider, c));
        var zone = new Dictionary<int, int>();
        for (var dx = -1; dx <= 1; dx++)
        {
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dz = -1; dz <= 1; dz++)
                {
                    if (grid.InBounds(x + dx, y + dy, z + dz))
                    {
                        zone[grid.Index(x + dx, y + dy, z + dz)] = 3;
                    }
                }
            }
        }
        return zone;
    }

    [Fact]
    public void NoCandidatesYieldsNoLineups()
    {
        var (grid, collider) = FlatScene;

        var result = LineupSolver.VerifyExact(grid, collider, new Dictionary<int, int>(), []);

        Assert.Empty(result);
    }

    [Fact]
    public void StableCandidateSurvivesWithAHighStabilityScore()
    {
        var (grid, collider) = FlatScene;
        var candidate = Candidate();
        var zone = ZoneFromExact(grid, collider, candidate);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [candidate]);

        Assert.Single(result);
        Assert.True(result[0].Stability >= 0.4f,
            $"expected the flatground throw to be stable, got {result[0].Stability}");

        // This fixture scores exactly 0.8 (4 of 5 nudges stay in-zone). The gate
        // is strict less-than, so a candidate whose stability equals the floor
        // must survive; a <= gate would drop it here.
        var atBoundary = LineupSolver.VerifyExact(grid, collider, zone, [candidate], minStability: 0.8f);
        Assert.Single(atBoundary);
    }

    [Fact]
    public void CandidateRestingOutsideTheZoneIsRejected()
    {
        var (grid, collider) = FlatScene;
        var candidate = Candidate();
        // A single cell nowhere near the actual landing (y=3500 vs the throw's
        // y~2048), so no perturbation reaches it.
        var (fx, fy, fz) = grid.CellOf(new Vector3(3500, 3500, 2));
        var farZone = new Dictionary<int, int>
        {
            [grid.Index(fx, fy, fz)] = 3,
        };

        var result = LineupSolver.VerifyExact(grid, collider, farZone, [candidate]);

        Assert.Empty(result);
    }

    [Fact]
    public void StabilityBelowTheThresholdIsRejected()
    {
        var (grid, collider) = FlatScene;
        var candidate = Candidate();
        var zone = ZoneFromExact(grid, collider, candidate);

        // Perfect stability tops out at 1.0, so a 1.1 floor rejects everything.
        var result = LineupSolver.VerifyExact(grid, collider, zone, [candidate], minStability: 1.1f);

        Assert.Empty(result);
    }

    [Fact]
    public void VerifiedRestPointIsResnappedFromTheExactSimulation()
    {
        var (grid, collider) = FlatScene;
        var candidate = Candidate();
        var zone = ZoneFromExact(grid, collider, candidate);
        var reference = ExactRest(collider, candidate);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [candidate]);

        Assert.Single(result);
        Assert.True(Vector3.Distance(result[0].RestPoint, reference) <= 1f,
            $"rest {result[0].RestPoint} should match the exact rest {reference}");
        Assert.True(Vector3.Distance(result[0].RestPoint, candidate.RestPoint) > 1f,
            "the placeholder rest point should have been replaced");
    }

    [Fact]
    public void EqualStabilityRanksTheFewerBounceLineupFirst()
    {
        var (grid, collider) = FlatScene;
        var candidate = Candidate();
        var zone = ZoneFromExact(grid, collider, candidate);
        // Two candidates from the SAME throw spec: identical exact simulation, so
        // identical stability. Only the pass-through Bounces differs, isolating
        // the ThenBy(Bounces) tie-break under a stability tie. Fed worst-first so
        // the result order can only come from the sort, not the input order.
        var manyBounces = candidate with { Bounces = 5 };
        var fewBounces = candidate with { Bounces = 1 };

        var result = LineupSolver.VerifyExact(grid, collider, zone, [manyBounces, fewBounces]);

        Assert.Equal(2, result.Count);
        Assert.Equal(result[1].Stability, result[0].Stability);
        Assert.True(result[0].Stability >= result[1].Stability);
        Assert.Equal(1, result[0].Bounces);
        Assert.Equal(5, result[1].Bounces);
    }
}
