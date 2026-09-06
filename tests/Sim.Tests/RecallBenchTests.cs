using System.Numerics;
using SmokeSolver.Cli;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using Xunit;

namespace SmokeSolver.Sim.Tests;

public class RecallBenchTests
{
    static Lineup L(ThrowType type, float strength, int bounces, float run = 0f) =>
        new(new Vector3(256, 1024, 0), 0f, -10f, type, new Vector3(600, 1024, 0), bounces, 2f, 1, Stability: 1f, Strength: strength, RunYawOffsetDeg: run);

    [Fact]
    public void AnUnreliableRefereeLineupIsNotALandableKind()
    {
        // A referee route that only one of five neighbouring aims still lands
        // is chaos, not a lineup: it neither counts as a miss nor as a hit.
        var shaky = L(ThrowType.Crouch, 0.5f, 3) with { Stability = 0.2f };
        var c = RecallCommand.Compare([], [shaky, L(ThrowType.Stand, 1f, 0)]);
        Assert.Equal(new RecallCommand.Tally(0, 1, 0), c.Kinds);
        Assert.Equal(new RecallCommand.Tally(0, 1, 0), RecallCommand.Compare([], [shaky, L(ThrowType.Stand, 1f, 0)], minStability: 0.05f).Kinds with { ExactOnly = 1 });
        Assert.Equal(2, RecallCommand.Compare([], [shaky, L(ThrowType.Stand, 1f, 0)], minStability: 0.05f).Kinds.ExactOnly);
    }

    [Fact]
    public void CompareCountsKindsByStanceClickRunAndRoute()
    {
        // The sweep returned a standing left click direct and a jump throw off
        // one wall; the referee also lands the standing throw off a wall (a
        // second route) and a right-click crouch throw. Same kind twice in a
        // list counts once.
        var sweep = new[] { L(ThrowType.Stand, 1f, 0), L(ThrowType.Stand, 1f, 0), L(ThrowType.JumpThrow, 1f, 1) };
        var referee = new[] { L(ThrowType.Stand, 1f, 0), L(ThrowType.Stand, 1f, 1), L(ThrowType.Crouch, 0.5f, 0), L(ThrowType.JumpThrow, 1f, 1) };

        var c = RecallCommand.Compare(sweep, referee);

        // Kinds ignore the route: the standing left click is found, the
        // crouch right click is the one miss.
        Assert.Equal(new RecallCommand.Tally(2, 1, 0), c.Kinds);
        Assert.Equal(3, c.Kinds.ExactLandable);
        Assert.Equal(2.0 / 3.0, c.Kinds.Recall, 3);
        Assert.Equal([new RecallCommand.Kind(ThrowType.Crouch, 0.5f, 0f, 0)], c.ExactOnly);
        Assert.Empty(c.SweepOnly);
        // Routes count the wall bounce as its own miss.
        Assert.Equal(new RecallCommand.Tally(2, 2, 0), c.Routes);
        Assert.Contains(new RecallCommand.Kind(ThrowType.Stand, 1f, 0f, 1), c.ExactOnlyRoutes);
    }

    [Fact]
    public void RunDirectionIsPartOfTheKindAndSweepOnlyIsCounted()
    {
        var sweep = new[] { L(ThrowType.RunJumpThrow, 1f, 0, run: 45f) };
        var referee = new[] { L(ThrowType.RunJumpThrow, 1f, 0, run: -45f) };

        var c = RecallCommand.Compare(sweep, referee);

        Assert.Equal(new RecallCommand.Tally(0, 1, 1), c.Kinds);
        Assert.Single(c.ExactOnly);
        Assert.Single(c.SweepOnly);
        // Nothing the referee lands means nothing to recall: not a failure.
        Assert.Equal(1.0, RecallCommand.Compare(sweep, []).Kinds.Recall);
    }

    [Fact]
    public void TheExhaustiveSearchKeepsOneRoutePerBounceCountWhenAsked()
    {
        // Open ground: the default keeps the closest hit per kind; with
        // distinct routes the same search may keep a direct and a bounced
        // landing of one kind, and never fewer than the default.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 2048, 0)]);
        var collider = new TriangleCollider(mesh, new Vector3(0, 0, -16), new Vector3(2048, 2048, 256));
        var feet = new Vector3(256, 1024, 0);
        var target = new Vector3(656, 1024, 0);
        ThrowType[] types = [ThrowType.Stand, ThrowType.JumpThrow];

        var perKind = LineupSolver.ExhaustiveExactSpot(collider, feet, target, 32f, types, [1f, 0.5f], ThrowConstants.Default, stepDeg: 2f);
        var perRoute = LineupSolver.ExhaustiveExactSpot(collider, feet, target, 32f, types, [1f, 0.5f], ThrowConstants.Default, stepDeg: 2f, distinctBounces: true);

        Assert.NotEmpty(perKind);
        Assert.True(perRoute.Count >= perKind.Count);
        Assert.Equal(perRoute.Count, perRoute.Select(RecallCommand.RouteOf).Distinct().Count());
        Assert.Equal(perKind.Count, perKind.Select(l => (l.Type, l.Strength, l.RunYawOffsetDeg)).Distinct().Count());
        // The parallel column layout must not lose the kinds the old per-kind
        // loop found: every kind in the per-kind list is present per route.
        Assert.Subset(perRoute.Select(l => (l.Type, l.Strength, l.RunYawOffsetDeg)).ToHashSet(), perKind.Select(l => (l.Type, l.Strength, l.RunYawOffsetDeg)).ToHashSet());
    }
}
