using System.Numerics;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The id that lets a favourite, a vote or a shared set outlive the solve that
/// produced the lineup.
/// </summary>
// Two properties, pulling against each other. Stable: the same throw must hash
// the same across re-solves whose floats differ below the precision a player
// can act on. Distinct: two throws a player would treat as different must
// never share an id, or a vote for one lands on the other.
public class LineupIdentityTests
{
    static readonly Vector3 Feet = new(-1408f, 1904f, 7.2f);

    static string Id(ThrowType type = ThrowType.Stand, float strength = 1f, float run = 0f,
        Vector3? feet = null, float yaw = 64.8f, float pitch = -76.4f) =>
        LineupIdentity.Id(type, strength, run, feet ?? Feet, yaw, pitch);

    [Fact]
    public void TheSameThrowAlwaysGetsTheSameId()
    {
        Assert.Equal(Id(), Id());
        Assert.Equal(16, Id().Length);
    }

    [Fact]
    public void JitterBelowWhatAPlayerCanTypeDoesNotChangeTheId()
    {
        // A solver version bump re-aims by a fraction of a degree and a mesh
        // re-extraction moves the floor by a fraction of a unit. Neither is a
        // different throw: the console command comes out identical.
        var a = Id(feet: new Vector3(-1408.2f, 1904.3f, 7.2f), yaw: 64.81f, pitch: -76.42f);
        var b = Id(feet: new Vector3(-1407.8f, 1903.7f, 7.4f), yaw: 64.84f, pitch: -76.38f);

        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData(ThrowType.Crouch, 1f, 0f)]
    [InlineData(ThrowType.JumpThrow, 1f, 0f)]
    [InlineData(ThrowType.Stand, 0.5f, 0f)]
    [InlineData(ThrowType.Stand, 0f, 0f)]
    public void ADifferentStanceOrClickIsADifferentThrow(ThrowType type, float strength, float run)
    {
        Assert.NotEqual(Id(), Id(type: type, strength: strength, run: run));
    }

    [Fact]
    public void ARunJumpInADifferentDirectionIsADifferentThrow()
    {
        // The viewer's old favourite key dropped this, so a left run-jump and a
        // right one from the same spot at the same aim were one favourite.
        var left = Id(type: ThrowType.RunJumpThrow, run: 90f);
        var right = Id(type: ThrowType.RunJumpThrow, run: -90f);

        Assert.NotEqual(left, right);
    }

    [Fact]
    public void AdjacentStandSpotsAreDistinct()
    {
        // Origins sit 16u apart on the grid; rounding to whole units must never
        // merge two of them.
        Assert.NotEqual(Id(), Id(feet: Feet + new Vector3(16f, 0f, 0f)));
        // And a single unit of difference is the smallest a setpos can express.
        Assert.NotEqual(Id(), Id(feet: Feet + new Vector3(1f, 0f, 0f)));
    }

    [Fact]
    public void ATenthOfADegreeIsTheSmallestAimDifferenceThatCounts()
    {
        Assert.NotEqual(Id(), Id(yaw: 64.9f));
        Assert.NotEqual(Id(), Id(pitch: -76.5f));
    }

    [Fact]
    public void YawWrapsSoTheSameAimNeverHashesTwice()
    {
        // 179.95 and -180.05 are the same direction; so are 359.99 and 0.
        Assert.Equal(Id(yaw: 179.97f), Id(yaw: -180.03f));
        Assert.Equal(Id(yaw: 359.99f), Id(yaw: 0f));
        Assert.Equal(Id(yaw: 64.8f), Id(yaw: 64.8f + 360f));
    }

    [Fact]
    public void TheIdDoesNotDependOnTheMeshAtAll()
    {
        // By construction - there is no mesh input - but pin it: a map
        // re-extraction that leaves the throw unchanged must leave the id
        // unchanged, and this is the property every stored vote relies on.
        var canonical = LineupIdentity.Canonical(ThrowType.Stand, 1f, 0f, Feet, 64.8f, -76.4f);

        Assert.Equal("Stand|1.00|0|-1408|1904|7|64.8|-76.4", canonical);
    }
}
