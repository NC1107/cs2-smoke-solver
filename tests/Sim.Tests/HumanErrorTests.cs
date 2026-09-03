using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The landing error a person adds, which the ranking leads with and the
/// viewer's default filter keeps under a threshold.
/// </summary>
public class HumanErrorTests
{
    [Fact]
    public void ACornerLobIntoASiteBeatsAPinpointAimFromOpenGroundFarAway()
    {
        // Nick's A-site test bench: the throws people use are blind lobs from
        // the site's corners. Two units of foot error and five degrees of sky
        // at 150u is a small miss; open ground alone is a bigger one, before
        // half a degree over 1500u is added.
        var cornerLob = HumanError.Estimate(pin: 2, band: 6, horizontalDistance: 150f, ThrowType.Stand, restScatter: 0f);
        var openPinpoint = HumanError.Estimate(pin: 0, band: 0, horizontalDistance: 1500f, ThrowType.Stand, restScatter: 0f);

        Assert.True(cornerLob < 20f, $"corner lob {cornerLob:F1}u");
        Assert.True(openPinpoint > 32f, $"open pinpoint {openPinpoint:F1}u");
        Assert.True(cornerLob < openPinpoint);
    }

    [Fact]
    public void AimErrorScalesWithHowFarTheGrenadeFlies()
    {
        var near = HumanError.Estimate(1, 4, 200f, ThrowType.Stand, 0f);
        var far = HumanError.Estimate(1, 4, 1200f, ThrowType.Stand, 0f);

        Assert.True(far > near + 30f, $"near {near:F1} far {far:F1}");
    }

    [Fact]
    public void MovementAndChaosAddToTheMiss()
    {
        var stand = HumanError.Estimate(2, 0, 300f, ThrowType.Stand, 0f);
        var runJump = HumanError.Estimate(2, 0, 300f, ThrowType.RunJumpThrow, 0f);
        var chaotic = HumanError.Estimate(2, 0, 300f, ThrowType.Stand, 40f);

        Assert.Equal(stand + 16f, runJump, 0.01f);
        Assert.Equal(stand + 40f, chaotic, 0.01f);
        Assert.Equal(stand, HumanError.Estimate(2, 0, 300f, ThrowType.Stand, 10f), 0.01f);
    }
}
