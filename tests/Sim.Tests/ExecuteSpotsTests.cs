using System.Text.Json;
using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Finding one place to stand that throws every smoke of an execute.
/// </summary>
// Four throws from four corners of the map is not an execute, it is four
// lineups. What makes it an execute is that one player can do all of it from
// one stance, and that is a spatial intersection of the per-target answers -
// which is easy to get subtly wrong in ways that still return plausible-looking
// spots.
public class ExecuteSpotsTests
{
    static JsonDocument? held;

    static List<JsonElement> Throws(params (float X, float Y, int Band, float Scatter)[] rows)
    {
        var json = "[" + string.Join(",", rows.Select(r => string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{{\"feet\":[{r.X},{r.Y},0],\"scatter\":{r.Scatter},\"aimRef\":{{\"band\":{r.Band}}}}}"))) + "]";
        // Kept alive for the test's lifetime: JsonElement is a view into its
        // document, and letting it be collected invalidates every element.
        held = JsonDocument.Parse(json);
        return [.. held.RootElement.EnumerateArray()];
    }

    static JsonDocument Find(IReadOnlyList<List<JsonElement>> perTarget, float within = 96f, int keep = 12) =>
        JsonDocument.Parse(ExecuteSpots.Find(perTarget, within, keep));

    [Fact]
    public void OnlySpotsThatCanThrowEveryTargetSurvive()
    {
        var a = Throws((0, 0, 0, 0));
        var b = Throws((32, 0, 0, 0));      // near the first: a real shared spot
        var c = Throws((5000, 5000, 0, 0)); // nowhere near it

        using var reachable = Find([a, b]);
        using var not = Find([a, c]);

        Assert.Single(reachable.RootElement.GetProperty("spots").EnumerateArray());
        Assert.Empty(not.RootElement.GetProperty("spots").EnumerateArray());
    }

    [Fact]
    public void AnUnthrowableTargetIsNamedRatherThanSilentlyProducingNothing()
    {
        // "This execute is impossible" and "the second smoke is the problem"
        // are different answers, and only the second tells you what to change.
        var a = Throws((0, 0, 0, 0));
        var empty = new List<JsonElement>();

        using var result = Find([a, empty]);

        Assert.Empty(result.RootElement.GetProperty("spots").EnumerateArray());
        var impossible = result.RootElement.GetProperty("impossibleTargets").EnumerateArray()
            .Select(e => e.GetInt32()).ToList();
        Assert.Equal([1], impossible);
    }

    [Fact]
    public void OneStanceIsReportedOnceNotOncePerThrowFromIt()
    {
        // A solve returns many throws from the same spot with different aims.
        // Without collapsing them the answer is a dozen restatements of one
        // place to stand.
        var a = Throws((0, 0, 0, 0), (1, 1, 1, 0), (2, 2, 2, 0), (3, 3, 1, 0));
        var b = Throws((10, 10, 0, 0));

        using var result = Find([a, b]);

        Assert.Single(result.RootElement.GetProperty("spots").EnumerateArray());
    }

    [Fact]
    public void ASpotIsRankedByItsWorstSmokeNotItsAverage()
    {
        // An execute with one throw nobody can reproduce is not an execute,
        // however easy the others are - so a spot whose smokes are all decent
        // must beat one that is perfect three times and hopeless once.
        var a = Throws((0, 0, 0, 0), (500, 0, 2, 0));
        var b = Throws((10, 0, 6, 0), (505, 0, 2, 0));

        using var result = Find([a, b]);
        var spots = result.RootElement.GetProperty("spots").EnumerateArray().ToList();

        Assert.Equal(2, spots.Count);
        // The consistently-mediocre spot (worst 2) leads the one carrying a
        // no-landmark throw (worst 6), despite the latter having a perfect one.
        Assert.Equal(2, spots[0].GetProperty("worst").GetInt32());
        Assert.Equal(6, spots[1].GetProperty("worst").GetInt32());
    }

    [Fact]
    public void ThePickForEachTargetIsItsMostReproducibleThrowFromThatSpot()
    {
        // Not merely the first one within range, or the answer would depend on
        // solve order rather than on which throw is actually best.
        var a = Throws((0, 0, 0, 0));
        var b = Throws((20, 0, 5, 0), (25, 0, 1, 0), (30, 0, 4, 0));

        using var result = Find([a, b]);
        var smokes = result.RootElement.GetProperty("spots")[0].GetProperty("smokes");

        Assert.Equal(1, smokes[1].GetProperty("aimRef").GetProperty("band").GetInt32());
    }

    [Fact]
    public void PositionChaosCountsAgainstASpotLikeAWeakReference()
    {
        // A throw whose landing jumps when the feet move a tick is not
        // reproducible either, whatever it has to aim at.
        var a = Throws((0, 0, 0, 0), (500, 0, 0, 0));
        var b = Throws((10, 0, 0, 64), (505, 0, 0, 0));

        using var result = Find([a, b]);
        var spots = result.RootElement.GetProperty("spots").EnumerateArray().ToList();

        Assert.Equal(0, spots[0].GetProperty("worst").GetInt32());
        Assert.True(spots[1].GetProperty("worst").GetInt32() >= 3,
            "a chaotic throw should sink its spot the way a missing landmark does");
    }

    [Fact]
    public void TheWithinDistanceDecidesWhatCountsAsTheSameStance()
    {
        var a = Throws((0, 0, 0, 0));
        var b = Throws((120, 0, 0, 0));

        using var tight = Find([a, b], within: 64f);
        using var loose = Find([a, b], within: 160f);

        Assert.Empty(tight.RootElement.GetProperty("spots").EnumerateArray());
        Assert.Single(loose.RootElement.GetProperty("spots").EnumerateArray());
    }

    [Fact]
    public void NearbyStancesCollapseIntoOneAnswerRatherThanRepeatingAPlace()
    {
        // Measured on de_dust2 before this was fixed: two spots 16u apart came
        // back as separate rows, because rounding feet into cells splits
        // neighbours across a boundary. Twelve results described about five
        // actual places to stand.
        var a = Throws((1104, 2928, 0, 0), (1104, 2944, 0, 0), (1120, 2930, 0, 0));
        var b = Throws((1100, 2930, 0, 0));

        using var result = Find([a, b], within: 96f);
        var spots = result.RootElement.GetProperty("spots").EnumerateArray().ToList();

        Assert.Single(spots);
    }

    [Fact]
    public void GenuinelyDifferentPlacesAreStillReportedSeparately()
    {
        var a = Throws((0, 0, 0, 0), (600, 0, 0, 0));
        var b = Throws((10, 0, 0, 0), (610, 0, 0, 0));

        using var result = Find([a, b], within: 96f);

        Assert.Equal(2, result.RootElement.GetProperty("spots").EnumerateArray().Count());
    }
}
