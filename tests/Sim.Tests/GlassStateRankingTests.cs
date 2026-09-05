using System.Numerics;
using SmokeSolver.Cli;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using Xunit;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// A lineup that breaks a pane and lands somewhere else once the pane is gone
/// depends on round state; it ranks below every state-independent throw, even
/// an exposed one. A pane the smoke breaks without the landing moving is not
/// state-dependent at all.
/// </summary>
public class GlassStateRankingTests
{
    static Lineup At(float x, bool exposed = false, int glass = 0, Vector3? restIfBroken = null) =>
        new(new Vector3(x, 0f, 0f), 0f, -10f, ThrowType.Stand, new Vector3(500f, 0f, 0f), 2, 2f, 1,
            Stability: 1f, DirectLos: exposed, GlassBreaks: glass, RestIfBroken: restIfBroken);

    [Fact]
    public void ALandingThatMovesOnceTheGlassIsGoneIsStateDependent()
    {
        Assert.True(LineupApi.StateDependent(At(0f, glass: 1, restIfBroken: new Vector3(560f, 0f, 0f))));
        Assert.True(LineupApi.StateDependent(At(0f, glass: 1, restIfBroken: null)));
        Assert.False(LineupApi.StateDependent(At(0f, glass: 1, restIfBroken: new Vector3(503f, 0f, 0f))));
        Assert.False(LineupApi.StateDependent(At(0f)));
    }

    [Fact]
    public void StateDependentLineupsRankBelowExposedOnes()
    {
        var concealed = At(0f);
        var exposed = At(10f, exposed: true);
        var glass = At(20f, glass: 1, restIfBroken: new Vector3(600f, 0f, 0f));

        var ranked = LineupApi.Rank([glass, exposed, concealed], null, _ => 0f, _ => 0);

        Assert.Equal([concealed, exposed, glass], ranked);
    }

    [Fact]
    public void ABetterAimBandDoesNotLiftAStateDependentLineupAboveAStateIndependentOne()
    {
        var glass = At(0f, glass: 1, restIfBroken: new Vector3(600f, 0f, 0f));
        var plain = At(10f);

        var ranked = LineupApi.Rank([glass, plain], null, l => l == glass ? 2f : 20f, _ => 0);

        Assert.Equal([plain, glass], ranked);
    }
}
