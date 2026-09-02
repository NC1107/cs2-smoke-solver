using System.Text.Json;
using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Trimming one solve down to the few throws an execute actually shows.
/// </summary>
// An execute is "which throw do I use for this smoke", not a catalogue: six
// targets times four hundred lineups is a payload nobody reads and a panel
// nobody can scan. The count before trimming has to survive though, or the
// viewer cannot tell "these are the best 8 of 63" from "8 was all there was" -
// and this project's recurring bug is exactly that kind of ambiguity.
public class ExecutePayloadTests
{
    static string Solved(int lineups, string? emptyReason = null, int origins = 12)
    {
        var rows = string.Join(",", Enumerable.Range(0, lineups)
            .Select(i => $$"""{"feet":[{{i}},0,0],"type":"Stand"}"""));
        var reason = emptyReason is null ? "null" : JsonSerializer.Serialize(emptyReason);
        return $$"""
            {"target":[10,20,30],"origins":{{origins}},"emptyReason":{{reason}},"lineups":[{{rows}}]}
            """;
    }

    [Fact]
    public void KeepsOnlyTheBestFewButRemembersHowManyThereWere()
    {
        using var doc = JsonDocument.Parse(LineupApi.TrimToBest(Solved(63), keep: 8));
        var root = doc.RootElement;

        Assert.Equal(8, root.GetProperty("lineups").GetArrayLength());
        Assert.Equal(63, root.GetProperty("found").GetInt32());
    }

    [Fact]
    public void TheKeptThrowsAreTheFrontOfTheRankedList()
    {
        // The ranking has already put the most reproducible throw first, so
        // "best" is simply the front - not a re-sort that could disagree with
        // the order the rest of the app shows.
        using var doc = JsonDocument.Parse(LineupApi.TrimToBest(Solved(20), keep: 3));

        var kept = doc.RootElement.GetProperty("lineups").EnumerateArray()
            .Select(l => l.GetProperty("feet")[0].GetInt32())
            .ToList();

        Assert.Equal([0, 1, 2], kept);
    }

    [Fact]
    public void AShortListIsNotPaddedOrTruncated()
    {
        using var doc = JsonDocument.Parse(LineupApi.TrimToBest(Solved(2), keep: 8));

        Assert.Equal(2, doc.RootElement.GetProperty("lineups").GetArrayLength());
        Assert.Equal(2, doc.RootElement.GetProperty("found").GetInt32());
    }

    [Fact]
    public void AnEmptySmokeCarriesItsReasonThrough()
    {
        // Per smoke, not per execute: one unreachable target must not look the
        // same as the whole execute failing.
        using var doc = JsonDocument.Parse(
            LineupApi.TrimToBest(Solved(0, "none of the 204 stand spots in range can land a smoke there"), keep: 8));
        var root = doc.RootElement;

        Assert.Empty(root.GetProperty("lineups").EnumerateArray());
        Assert.Equal(0, root.GetProperty("found").GetInt32());
        Assert.Contains("stand spots", root.GetProperty("emptyReason").GetString()!);
    }

    [Fact]
    public void TheTargetAndOriginCountSurviveTrimming()
    {
        // The viewer labels each smoke by its target, so losing it here would
        // leave a list of throws nobody can attribute.
        using var doc = JsonDocument.Parse(LineupApi.TrimToBest(Solved(5, origins: 204), keep: 2));
        var root = doc.RootElement;

        Assert.Equal(10, root.GetProperty("target")[0].GetInt32());
        Assert.Equal(30, root.GetProperty("target")[2].GetInt32());
        Assert.Equal(204, root.GetProperty("origins").GetInt32());
    }
}
