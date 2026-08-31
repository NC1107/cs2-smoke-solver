using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

// The aimTarget parameter is the path every production /api/lineup request
// takes (TargetSolver always passes the resolved target); the null path is the
// legacy CLI/tests behaviour. These pin the precision re-aim: given a window
// of stable in-zone aims, the one whose exact rest lands closest to the goal
// wins, instead of whatever aim the coarse sweep happened to nominate.
public class VerifyExactAimTargetTests
{
    static readonly Vector3 RegionMin = new(0, 0, -64);
    static readonly Vector3 RegionMax = new(4096, 4096, 1200);

    static readonly (VoxelGrid Grid, TriangleCollider Collider) FlatScene = BuildFlatScene();

    static (VoxelGrid Grid, TriangleCollider Collider) BuildFlatScene()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 4096, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, RegionMin, RegionMax);
        var collider = new TriangleCollider(mesh, RegionMin, RegionMax);
        return (grid, collider);
    }

    // Matches VerifyExact's re-aim lattice pitch.
    const float StepDeg = 0.6f;

    static Lineup Candidate(float yawDeg = 0f) =>
        new(new Vector3(500, 2048, 0), YawDeg: yawDeg, PitchDeg: -30f, ThrowType.Stand, Vector3.Zero,
            Bounces: 1, FlightTime: 1f, RestCrossings: 3, Strength: 1f);

    static Vector3 ExactRest(TriangleCollider collider, Lineup c) =>
        GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(
            c.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(c.Type)),
            c.YawDeg, c.PitchDeg, c.Type, c.Strength)).RestPoint;

    // A zone generous enough that the nominated aim, the goal aim, and every
    // stability probe around them all land inside it - so the ONLY thing that
    // decides the winner is distance to the goal.
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

    [Fact]
    public void ReAimsToTheWindowAimClosestToTheGoal()
    {
        var (grid, collider) = FlatScene;
        var nominated = Candidate();
        // The goal is exactly where a 2-lattice-step yaw nudge lands, so the
        // refinement has a strictly better aim available inside its window.
        var goalAim = Candidate(yawDeg: 2 * StepDeg);
        var goal = ExactRest(collider, goalAim);
        var zone = ZoneAround(grid, collider, [nominated, goalAim], radius: 4);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [nominated], aimTarget: goal);

        Assert.Single(result);
        Assert.Equal(goalAim.YawDeg, result[0].YawDeg, 3);
        var refinedMiss = new Vector2(result[0].RestPoint.X - goal.X, result[0].RestPoint.Y - goal.Y).Length();
        var nominatedMiss = new Vector2(ExactRest(collider, nominated).X - goal.X, ExactRest(collider, nominated).Y - goal.Y).Length();
        Assert.True(refinedMiss < nominatedMiss,
            $"re-aimed rest misses the goal by {refinedMiss:F1}u, the nominated aim by {nominatedMiss:F1}u");
    }

    [Fact]
    public void NullAimTargetKeepsTheNominatedAim()
    {
        var (grid, collider) = FlatScene;
        var nominated = Candidate();
        var goalAim = Candidate(yawDeg: 2 * StepDeg);
        var zone = ZoneAround(grid, collider, [nominated, goalAim], radius: 4);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [nominated]);

        Assert.Single(result);
        Assert.Equal(nominated.YawDeg, result[0].YawDeg, 3);
    }

    [Fact]
    public void RejectsWhenNoWindowAimIsStableEnough()
    {
        var (grid, collider) = FlatScene;
        var nominated = Candidate();
        var goal = ExactRest(collider, nominated);
        var zone = ZoneAround(grid, collider, [nominated], radius: 4);

        // Stability tops out at 1.0, so nothing in the window can clear this
        // floor and the aimTarget path must reject rather than fall back to an
        // unstable aim.
        var result = LineupSolver.VerifyExact(grid, collider, zone, [nominated], minStability: 1.1f, aimTarget: goal);

        Assert.Empty(result);
    }

    // --- the exact tolerance gate (aimTarget + tolerance) ---

    [Fact]
    public void ToleranceGateRejectsARestOutsideThePromisedRadius()
    {
        var (grid, collider) = FlatScene;
        var nominated = Candidate();
        var zone = ZoneAround(grid, collider, [nominated], radius: 6);
        // The +/-1.2 degree re-aim window shifts this throw's landing by up to
        // ~36u laterally, so a goal 60u sideways leaves every reachable aim at
        // least ~24u short of a 16u promise - the cell zone contains plenty of
        // those rests, but the gate must not.
        var goal = ExactRest(collider, nominated) + new Vector3(0, 60, 0);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [nominated], aimTarget: goal, tolerance: 16f);

        Assert.Empty(result);
    }

    [Fact]
    public void ToleranceGateKeepsARestInsideThePromisedRadius()
    {
        var (grid, collider) = FlatScene;
        var nominated = Candidate();
        var zone = ZoneAround(grid, collider, [nominated], radius: 4);
        var goal = ExactRest(collider, nominated) + new Vector3(0, 30, 0);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [nominated], aimTarget: goal, tolerance: 40f);

        Assert.Single(result);
        var miss = new Vector2(result[0].RestPoint.X - goal.X, result[0].RestPoint.Y - goal.Y).Length();
        Assert.True(miss <= 40f, $"kept lineup lands {miss:F1}u from the goal, past the 40u promise");
    }

    [Fact]
    public void EveryKeptLineupHonorsTheToleranceItWasSolvedWith()
    {
        var (grid, collider) = FlatScene;
        // A fan of nominated aims landing at spreading distances from one goal.
        var candidates = Enumerable.Range(0, 8).Select(i => Candidate(yawDeg: i * StepDeg)).ToArray();
        var goal = ExactRest(collider, candidates[0]);
        var zone = ZoneAround(grid, collider, candidates, radius: 5);

        const float Tolerance = 24f;
        var result = LineupSolver.VerifyExact(grid, collider, zone, candidates, aimTarget: goal, tolerance: Tolerance);

        Assert.NotEmpty(result);
        Assert.All(result, l =>
        {
            var miss = new Vector2(l.RestPoint.X - goal.X, l.RestPoint.Y - goal.Y).Length();
            Assert.True(miss <= Tolerance, $"lineup lands {miss:F1}u out, past the {Tolerance}u promise");
        });
    }

    [Fact]
    public void GoalOutsideTheZoneStillReturnsTheClosestInZoneAim()
    {
        var (grid, collider) = FlatScene;
        var nominated = Candidate();
        var zone = ZoneAround(grid, collider, [nominated], radius: 4);
        // A goal far outside the reachable window: every in-zone aim is a bad
        // miss, but the closest stable one must still be returned - the target
        // is a preference among survivors, not an extra rejection gate.
        var goal = ExactRest(collider, nominated) + new Vector3(300, 300, 0);

        var result = LineupSolver.VerifyExact(grid, collider, zone, [nominated], aimTarget: goal);

        Assert.Single(result);
    }
}
