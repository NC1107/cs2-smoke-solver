using System.Numerics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SmokeSolver.Cli;
using SmokeSolver.Solver;
using static SmokeSolver.Cli.ServeCommand;

namespace SmokeSolver.Sim.Tests;

// Regression tests for the 2026-09-23 audit. Several fixes from the previous
// audit were recorded as shipped and were not in the code; each one here
// fails if its fix is reverted.

public class SolveCacheKeyTests
{
    static readonly CollisionMesh Mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 1024, 0)]);

    static MapEntry Entry(string dataETag) =>
        new(Mesh, null, null, new ThrowConstants(), [], [], "\"build-1\"") { DataETag = dataETag };

    static string Key(MapEntry entry)
    {
        using var doc = JsonDocument.Parse("""{"target":[100,100]}""");
        return SolveCacheKey(entry, new ThrowConstants(), doc.RootElement, "");
    }

    [Fact]
    public void RegeneratedStandSpotsRetireTheCachedAnswer() =>
        Assert.NotEqual(Key(Entry("stand-spots-v1")), Key(Entry("stand-spots-v2")));

    [Fact]
    public void UnchangedMapDataReplaysTheCachedAnswer() =>
        Assert.Equal(Key(Entry("stand-spots-v1")), Key(Entry("stand-spots-v1")));
}

public class VariantCollapseTests
{
    static Lineup Throw(float x, float y, ThrowType type = ThrowType.JumpThrow, int bounces = 4,
        float restX = 500, float restY = 500, bool exposed = false, float yaw = 160f) =>
        new(new Vector3(x, y, 0), yaw, -84f, type, new Vector3(restX, restY, 0), bounces, 3f, 1,
            Stability: 1f, Strength: 0.5f, DirectLos: exposed);

    static int SameBand(Lineup _) => 3;

    [Fact]
    public void NearIdenticalThrowsFoldIntoTheFirstWithACount()
    {
        // The shape that filled de_dust2 mid doors: one jump throw from spots
        // a few units apart, aim a few degrees apart, landing together.
        var best = Throw(0, 0);
        var ranked = new[] { best, Throw(12, 0, yaw: 163f), Throw(0, 22, yaw: 152f), Throw(-13, 5, yaw: 158f) };

        var shown = LineupApi.CollapseVariants(ranked, SameBand);

        var only = Assert.Single(shown);
        Assert.Same(best, only.Lineup);
        Assert.Equal(3, only.Similar);
    }

    [Fact]
    public void ALandingASmokeWidthAwayIsItsOwnRow() =>
        Assert.Equal(2, LineupApi.CollapseVariants([Throw(0, 0), Throw(5, 0, restX: 560)], SameBand).Count);

    [Fact]
    public void ExposedAndConcealedThrowsFromOneSpotStaySeparate()
    {
        // A player cares a great deal which one they are shown.
        var shown = LineupApi.CollapseVariants([Throw(0, 0, exposed: false), Throw(4, 0, exposed: true)], SameBand);

        Assert.Equal(2, shown.Count);
        Assert.Contains(shown, s => s.Lineup.DirectLos);
    }

    [Fact]
    public void ADifferentAimReferenceIsItsOwnRow()
    {
        var clear = Throw(0, 0);
        var blind = Throw(4, 0);

        var shown = LineupApi.CollapseVariants([clear, blind], l => ReferenceEquals(l, clear) ? 0 : 6);

        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void AnotherThrowKindOrBounceCountIsItsOwnRow()
    {
        var shown = LineupApi.CollapseVariants(
            [Throw(0, 0), Throw(2, 0, type: ThrowType.CrouchJumpThrow), Throw(3, 0, bounces: 5)], SameBand);

        Assert.Equal(3, shown.Count);
    }

    [Fact]
    public void SpotsFurtherApartThanAStrideStaySeparate() =>
        Assert.Equal(2, LineupApi.CollapseVariants([Throw(0, 0), Throw(40, 0)], SameBand).Count);

    [Fact]
    public void RankOrderSurvivesTheCollapse()
    {
        var first = Throw(0, 0);
        var second = Throw(300, 300, restX: 900);
        var foldsIntoFirst = Throw(5, 5);

        var shown = LineupApi.CollapseVariants([first, second, foldsIntoFirst], SameBand);

        Assert.Equal([first, second], shown.Select(s => s.Lineup));
    }
}

public class SolveRankingDeterminismTests
{
    static Lineup Tied(float x) =>
        new(new Vector3(x, 0, 0), 90f, -30f, ThrowType.Stand, new Vector3(500, 500, 0), 2, 2f, 1, Stability: 1f);

    [Fact]
    public void TiedLineupsRankTheSameWhateverOrderTheSweepFoundThem()
    {
        // Everything Rank sorts on is banded, so these tie on all of it; the
        // parallel sweep can hand them over in either order.
        var a = Tied(100);
        var b = Tied(200);
        var c = Tied(300);

        var forward = LineupApi.Rank([a, b, c], null, _ => 0f, _ => 0);
        var backward = LineupApi.Rank([c, b, a], null, _ => 0f, _ => 0);

        Assert.Equal(forward, backward);
    }
}

public class ClientSolveLeaseTests
{
    static HttpContext From(string ip)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["CF-Connecting-IP"] = ip;
        return context;
    }

    [Fact]
    public void AClientCannotHoldMoreThanTwoSolvesAtOnce()
    {
        // One client used to be able to fill the whole shared queue.
        using var first = TryLeaseClientSolve(From("203.0.113.40"));
        using var second = TryLeaseClientSolve(From("203.0.113.40"));
        var third = TryLeaseClientSolve(From("203.0.113.40"));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(third);
    }

    [Fact]
    public void AnotherClientIsUnaffected()
    {
        using var a = TryLeaseClientSolve(From("203.0.113.41"));
        using var b = TryLeaseClientSolve(From("203.0.113.41"));
        using var other = TryLeaseClientSolve(From("198.51.100.41"));

        Assert.NotNull(other);
    }

    [Fact]
    public void FinishingASolveGivesThePlaceBack()
    {
        var a = TryLeaseClientSolve(From("203.0.113.42"));
        using var b = TryLeaseClientSolve(From("203.0.113.42"));
        a!.Dispose();
        a.Dispose(); // twice is still once

        using var again = TryLeaseClientSolve(From("203.0.113.42"));
        var tooMany = TryLeaseClientSolve(From("203.0.113.42"));

        Assert.NotNull(again);
        Assert.Null(tooMany);
    }
}

public class ThrowTypeParsingTests
{
    [Theory]
    [InlineData("999")]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Lob")]
    public void OnlyThrowTypeNamesParse(string? name) => Assert.False(TryParseThrowType(name, out _));

    [Theory]
    [InlineData("stand", ThrowType.Stand)]
    [InlineData("RunJumpThrow", ThrowType.RunJumpThrow)]
    public void NamesParseInAnyCase(string name, ThrowType expected)
    {
        Assert.True(TryParseThrowType(name, out var type));
        Assert.Equal(expected, type);
    }
}

public class BounceErrorTests
{
    // Calibrated 2026-09-23 on 18,353 rig throws: the simulator agrees with
    // the game to within a unit or so up to seven bounces, and not from eight.
    [Fact]
    public void FlatToSevenBounces()
    {
        Assert.Equal(0f, HumanError.BounceError(4));
        Assert.True(HumanError.BounceError(7) <= 1f);
    }

    [Fact]
    public void ACliffFromEightBounces()
    {
        Assert.True(HumanError.BounceError(8) >= 8f, "eight bounces should cost at least one 8u ranking band");
        Assert.True(HumanError.BounceError(9) > HumanError.BounceError(8));
        Assert.Equal(HumanError.BounceError(9), HumanError.BounceError(20));
    }

    [Fact]
    public void ALineupsBouncesReachItsEstimate()
    {
        var calm = new Lineup(new Vector3(0, 0, 0), 0f, -30f, ThrowType.Stand, new Vector3(400, 0, 0), 4, 2f, 1, Stability: 1f);
        var chaotic = calm with { Bounces = 9 };

        Assert.Equal(HumanError.BounceError(9), HumanError.Estimate(chaotic, 2, 0) - HumanError.Estimate(calm, 2, 0), 3);
    }
}

public class ShutdownDrainTests
{
    [Fact]
    public async Task AStoppingServerGivesInFlightSolvesTimeToFinish()
    {
        // A cold solve on the prod host takes 40-100 s; the default window cut
        // them off mid-solve on every deploy.
        var root = Path.Combine(Path.GetTempPath(), "smokesolver-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "viewer"));
        File.WriteAllText(Path.Combine(root, "viewer", "index.html"), "<!doctype html><title>t</title>");
        try
        {
            await using var app = Build(new Dictionary<string, string> { ["root"] = root, ["port"] = "0", ["attrs"] = "default" });
            var options = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.Extensions.Hosting.HostOptions>>().Value;

            Assert.True(options.ShutdownTimeout >= TimeSpan.FromSeconds(90), $"shutdown drain is {options.ShutdownTimeout}");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }
}
