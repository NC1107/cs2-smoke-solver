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

    static ThrowSpec SpecOf(Lineup c) =>
        new(c.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(c.Type)),
            c.YawDeg, c.PitchDeg, c.Type, c.Strength);

    static Vector3 ExactRest(TriangleCollider collider, Lineup c) =>
        GrenadeTrajectory.SimulateExact(collider, SpecOf(c)).RestPoint;

    // A zone wide enough to hold several different throws' landing spots at once,
    // for the cases that need two genuinely different throws to both verify.
    static Dictionary<int, int> ZoneAround(VoxelGrid grid, TriangleCollider collider, Lineup[] cs, int radius)
    {
        var zone = new Dictionary<int, int>();
        foreach (var c in cs)
        {
            var (x, y, z) = grid.CellOf(ExactRest(collider, c));
            for (var dx = -radius; dx <= radius; dx++)
            {
                for (var dy = -radius; dy <= radius; dy++)
                {
                    for (var dz = -radius; dz <= radius; dz++)
                    {
                        if (grid.InBounds(x + dx, y + dy, z + dz))
                        {
                            zone[grid.Index(x + dx, y + dy, z + dz)] = 3;
                        }
                    }
                }
            }
        }
        return zone;
    }

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
    public void MisAimedCandidateWithinReachIsReAimedBackIntoTheZone()
    {
        var (grid, collider) = FlatScene;
        var reference = Candidate();
        var zone = ZoneFromExact(grid, collider, reference);
        // 1.2 degrees of yaw moves the landing ~37u laterally at this range,
        // outside the 3x3-cell zone, so the plain stability test scores 1/5 and
        // rejects. The re-aim search reaches exactly 1.2 degrees; it must walk
        // the candidate back into the zone instead of dropping it.
        var misAimed = reference with { YawDeg = 1.2f };

        var result = LineupSolver.VerifyExact(grid, collider, zone, [misAimed]);

        var rescued = Assert.Single(result);
        Assert.True(MathF.Abs(rescued.YawDeg) < 0.05f,
            $"expected the re-aim to restore yaw ~0, got {rescued.YawDeg}");
        Assert.True(rescued.Stability >= 0.8f,
            $"re-aimed center should be stable, got {rescued.Stability}");
        // The published rest must be the exact simulation of the published
        // angles, inside the zone: what the lineup claims is what it does.
        var republished = ExactRest(collider, rescued);
        Assert.True(Vector3.Distance(rescued.RestPoint, republished) <= 1f,
            $"rest {rescued.RestPoint} does not match the exact rest {republished} of the published angles");
        var (rx, ry, rz) = grid.CellOf(rescued.RestPoint);
        Assert.True(zone.ContainsKey(grid.Index(rx, ry, rz)),
            $"re-aimed rest {rescued.RestPoint} is not a zone cell");
    }

    [Fact]
    public void MisAimBeyondTheReAimReachStaysRejected()
    {
        var (grid, collider) = FlatScene;
        var reference = Candidate();
        var zone = ZoneFromExact(grid, collider, reference);
        // 3 degrees off: the maximum 1.2-degree correction still leaves the rest
        // ~55u outside the zone. The rescue is bounded, not an open-ended search
        // that would relabel a different throw as this lineup.
        var farOff = reference with { YawDeg = 3f };

        var result = LineupSolver.VerifyExact(grid, collider, zone, [farOff]);

        Assert.Empty(result);
    }

    [Fact]
    public void EqualStabilityRanksTheFewerBounceLineupFirst()
    {
        var (grid, collider) = FlatScene;
        // Bounces is now read back off the exact simulation rather than passed
        // through from the caller, so the tie-break can no longer be isolated by
        // injecting two bounce counts onto one throw - the two throws have to
        // genuinely differ. Over flatground a steep lob settles in one bounce
        // fewer than a shallower one.
        var fewBounces = Candidate() with { PitchDeg = -70f };
        var manyBounces = Candidate() with { PitchDeg = -60f };
        // Wide enough to hold both landing spots, so both verify and both stay
        // in-zone under the aim perturbations stability samples. That ties their
        // stability, leaving bounce count as the only thing the sort can separate
        // them by. Fed worst-first so the order can only come from the sort.
        var zone = ZoneAround(grid, collider, [fewBounces, manyBounces], radius: 6);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [manyBounces, fewBounces]);

        Assert.Equal(2, result.Count);
        Assert.Equal(result[1].Stability, result[0].Stability);
        Assert.Equal(4, result[0].Bounces);
        Assert.Equal(5, result[1].Bounces);
    }

    [Fact]
    public void BouncesAndFlightTimeAreTakenFromTheExactSimNotTheCoarseSweep()
    {
        var (grid, collider) = FlatScene;
        // Candidate() carries the placeholder Bounces/FlightTime a coarse voxel
        // sweep would have produced. The verified lineup describes the exact
        // simulation's throw - its aim and its landing spot both come from there -
        // so its bounce count and flight time have to describe that same throw,
        // not the approximation that merely nominated it. They used to pass
        // straight through, which both mislabelled the lineup and meant the bounce
        // and flight-time filters were sifting on the wrong numbers.
        var candidate = Candidate();
        var zone = ZoneFromExact(grid, collider, candidate);

        var lineup = Assert.Single(LineupSolver.VerifyExact(grid, collider, zone, [candidate]));

        var exact = GrenadeTrajectory.SimulateExact(collider, SpecOf(lineup));
        Assert.Equal(exact.Bounces, lineup.Bounces);
        Assert.Equal(exact.FlightTime, lineup.FlightTime, 3);
        Assert.NotEqual(candidate.Bounces, lineup.Bounces);
    }
}
