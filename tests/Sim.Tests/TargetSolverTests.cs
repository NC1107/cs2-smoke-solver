using System.Numerics;
using SmokeSolver.Cli;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The one function every /api/lineup request runs through, end to end.
/// </summary>
// Nothing exercised this before. It is where the target-height resolution, the
// origin set, the spawns-only and exact-origin scopes, the crouch filter, the
// sightline flag and the pin classification are wired together - which is
// exactly the kind of multi-branch seam this project's silent-zero bugs have
// always lived in (dust2's bombsites resolving onto a roof, de_vertigo's
// Z-ordering, spawn origins releasing from mid-air). Every one of those shipped
// green because the pieces were unit-tested and the wiring was not.
public class TargetSolverTests
{
    // Big enough for a real throw to fly, small enough that a full sweep over it
    // stays inside a unit test's patience: the voxel grid is cubic in this, so
    // doubling the arena costs eight times the build.
    const float Extent = 700f;

    static CollisionMesh Arena() =>
        SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(-Extent, Extent, 0)]);

    // The arena with a hollow block standing in it: a shell of quads voxelizes
    // its SURFACES, so the space inside is free but sealed off - somewhere a
    // smoke could rest and no throw can get to.
    // And with a genuinely FILLED block - stacked floors a voxel apart, so every
    // cell inside really is solid rather than merely enclosed.
    static CollisionMesh ArenaWithSolidBlock(float lo, float hi, float top)
    {
        var quads = new List<(float[], float[], float[], float[])>
        {
            SyntheticMeshes.Ground(-Extent, Extent, 0),
        };
        for (var z = 0f; z <= top; z += 16f)
        {
            quads.Add(SyntheticMeshes.Ground(lo, hi, z));
        }
        return SyntheticMeshes.FromQuads(quads);
    }

    static CollisionMesh ArenaWithBlock(float lo, float hi, float top) =>
        SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(-Extent, Extent, 0),
            SyntheticMeshes.Ground(lo, hi, 0),
            SyntheticMeshes.Ceiling(lo, hi, top),
            SyntheticMeshes.WallX(lo, lo, hi, 0, top),
            SyntheticMeshes.WallX(hi, lo, hi, 0, top),
            SyntheticMeshes.WallY(lo, hi, lo, 0, top),
            SyntheticMeshes.WallY(lo, hi, hi, 0, top),
        ]);

    static List<NavAreaJson> ArenaNav() =>
    [
        new(1, [
            [-Extent, -Extent, 0],
            [Extent, -Extent, 0],
            [Extent, Extent, 0],
            [-Extent, Extent, 0],
        ]),
    ];

    static TargetSolve Solve(
        Vector3 target,
        Vector2? originClick,
        float originReach = 250f,
        bool spawnsOnly = false,
        bool exactOrigin = false,
        IReadOnlyList<Vector3>? spawnPoints = null,
        float tolerance = 128f) =>
        TargetSolver.SolveForTarget(
            Arena(), null, ArenaNav(), target, hasTargetZ: true,
            originClick, originReach, tolerance, ThrowConstants.Default,
            spawnPoints: spawnPoints, spawnsOnly: spawnsOnly, exactOrigin: exactOrigin);

    [Fact]
    public void APlainTwoPointSolveFindsThrowsAndSaysNothingIsWrong()
    {
        // The smoke test the suite never had: a perfectly ordinary query must
        // come back with throws, and with no complaint attached.
        var solve = Solve(new Vector3(400, 0, 0), new Vector2(-200, 0));

        Assert.True(solve.Lineups.Count > 0,
            $"an open arena should be throwable across; got 0 lineups ({solve.EmptyReason})");
        Assert.Null(solve.EmptyReason);
        Assert.True(solve.OriginCount > 0);
    }

    [Fact]
    public void EveryThrowStandsOnTheFloorRatherThanInOrAboveIt()
    {
        var solve = Solve(new Vector3(400, 0, 0), new Vector2(-200, 0));

        Assert.NotEmpty(solve.Lineups);
        foreach (var l in solve.Lineups)
        {
            Assert.True(MathF.Abs(l.Feet.Z) < 8f,
                $"feet at z={l.Feet.Z:F2} are not on the arena floor - a setpos from this lineup would " +
                "drop the player through the ground or start them in the air");
        }
    }

    [Fact]
    public void ATargetSealedAwayFromEveryThrowSpotExplainsItself()
    {
        // Reachable-looking but walled off. An empty array on its own has meant
        // a genuine physics negative and a solver bug at different times, and
        // from outside they were indistinguishable.
        var solve = TargetSolver.SolveForTarget(
            ArenaWithBlock(180, 380, 400), null, ArenaNav(),
            new Vector3(280, 280, 200), hasTargetZ: true,
            new Vector2(-200, 0), 250f, 48f, ThrowConstants.Default);

        Assert.Empty(solve.Lineups);
        Assert.False(string.IsNullOrWhiteSpace(solve.EmptyReason));
    }

    [Fact]
    public void ATargetInsideSolidGeometryBailsBeforeSweepingAnything()
    {
        // The expensive version of the same silent failure: with no cell a smoke
        // could ever rest in, the old code still swept every origin on the map -
        // minutes of CPU - to arrive at the empty list it already had, and said
        // why only in the server's own log.
        var solve = TargetSolver.SolveForTarget(
            ArenaWithSolidBlock(180, 380, 320), null, ArenaNav(),
            new Vector3(280, 280, 160), hasTargetZ: true,
            new Vector2(-200, 0), 250f, 24f, ThrowConstants.Default);

        Assert.Empty(solve.Lineups);
        Assert.Contains("solid", solve.EmptyReason ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, solve.OriginCount);
    }

    [Fact]
    public void ExactOriginSolvesFromTheOneSpotItWasGiven()
    {
        // "Solve from exactly here" must not substitute a tidier neighbour: the
        // whole point is that the answer works from where the player is
        // standing, which is what a pasted getpos means.
        var spot = new Vector2(-200, 0);
        var solve = Solve(new Vector3(400, 0, 0), spot, exactOrigin: true);

        Assert.Equal(1, solve.OriginCount);
        foreach (var l in solve.Lineups)
        {
            Assert.Equal(spot.X, l.Feet.X, 1f);
            Assert.Equal(spot.Y, l.Feet.Y, 1f);
        }
    }

    [Fact]
    public void SpawnsOnlyThrowsFromTheSpawnsAndSeatsThemOnTheFloor()
    {
        // Spawn entities are markers, not foot positions - de_dust2's T spawn
        // floats 55u over the ground. Solving from the raw marker puts every
        // lineup's feet in mid-air and its setpos inside the floor.
        var floating = new List<Vector3>
        {
            new(-200, 0, 55),
            new(-240, 40, 55),
        };
        var solve = Solve(new Vector3(400, 0, 0), null, spawnsOnly: true, spawnPoints: floating);

        Assert.True(solve.OriginCount > 0, "the spawns should have produced origins");
        Assert.True(solve.OriginCount <= floating.Count,
            $"spawns-only means the spawns themselves, not the ground around them; got {solve.OriginCount} origins for {floating.Count} spawns");
        foreach (var l in solve.Lineups)
        {
            Assert.True(MathF.Abs(l.Feet.Z) < 8f,
                $"a spawn-scoped lineup released from z={l.Feet.Z:F2}, not from the floor the player lands on");
        }
    }

    [Fact]
    public void TheResolvedTargetKeepsTheHeightItWasGiven()
    {
        // A caller that supplies a Z means it; re-deriving one would move the
        // landing point out from under the answer.
        var target = new Vector3(400, 0, 0);
        var solve = Solve(target, new Vector2(-200, 0));

        Assert.Equal(target.X, solve.Target.X, 0.01f);
        Assert.Equal(target.Y, solve.Target.Y, 0.01f);
        Assert.Equal(target.Z, solve.Target.Z, 0.01f);
    }

    // ---- the two facts the class comment promises and no test asserted ----

    [Fact]
    public void AThrowSpotWithAClearViewOfTheLandingIsFlaggedExposedAndOneBehindAWallIsNot()
    {
        // Open arena: every spot can see the landing.
        var open = Solve(new Vector3(400, 0, 0), new Vector2(-200, 0));
        Assert.NotEmpty(open.Lineups);
        Assert.All(open.Lineups, l => Assert.True(l.DirectLos, $"open ground at {l.Feet} should see the landing"));

        // A tall wall between the throw spot and the landing: nothing behind
        // it can see over, so every lineup from behind it is concealed.
        var walled = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(-Extent, Extent, 0),
            SyntheticMeshes.WallX(100, -Extent, Extent, 0, 300),
        ]);
        var behind = TargetSolver.SolveForTarget(
            walled, null, ArenaNav(), new Vector3(400, 0, 0), hasTargetZ: true,
            new Vector2(-200, 0), 150f, 128f, ThrowConstants.Default);
        Assert.NotEmpty(behind.Lineups);
        Assert.All(behind.Lineups, l => Assert.False(l.DirectLos, $"{l.Feet} is behind a 300u wall and cannot see the landing"));
    }

    [Fact]
    public void ARunFromBesideAWallProducesPinnedOriginsAndTheRankingPutsThemFirst()
    {
        // A wall next to the throw spot: the lattice sample beside it gains a
        // wall-pinned twin, and the API's order prefers it.
        var walled = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(-Extent, Extent, 0),
            SyntheticMeshes.WallY(-Extent, Extent, -232, 0, 128),
        ]);
        var solve = TargetSolver.SolveForTarget(
            walled, null, ArenaNav(), new Vector3(400, 0, 0), hasTargetZ: true,
            new Vector2(-200, -200), 64f, 128f, ThrowConstants.Default);
        Assert.NotEmpty(solve.Lineups);
        var pinned = solve.Lineups.Where(l => LineupSolver.PositionStance(solve.PlayerCollider, l.Feet).Pin > 0).ToList();
        Assert.NotEmpty(pinned);
        // Pressed against the wall from either side: a hull half-width off it.
        Assert.All(pinned, l => Assert.Equal(16f, MathF.Abs(l.Feet.Y + 232f), 1.5f));

        var ranked = LineupApi.Ranked(solve, new Vector2(-200, -200));
        Assert.True(LineupSolver.PositionStance(solve.PlayerCollider, ranked[0].Feet).Pin > 0,
            $"the first-ranked lineup stands at {ranked[0].Feet}, which is not against the wall");
    }
}
