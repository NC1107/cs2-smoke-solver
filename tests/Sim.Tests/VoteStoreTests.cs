using System.Numerics;
using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// One vote per account per lineup per spot, whatever the callers do.
/// </summary>
public class VoteStoreTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "smokesolver-votes-" + Guid.NewGuid().ToString("N"));
    readonly VoteStore store;

    public VoteStoreTests()
    {
        store = new VoteStore(Path.Combine(dir, "votes.db"));
    }

    public void Dispose()
    {
        store.Dispose();
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        GC.SuppressFinalize(this);
    }

    const string Map = "de_dust2";
    const string Spot = "target:a1b2c3d4";
    const string Lineup = "dec0f1e08c93318d";
    const string Alice = "76561198000000001";
    const string Bob = "76561198000000002";

    [Fact]
    public async Task VotesTallyPerLineup()
    {
        await store.CastAsync(Map, Spot, Lineup, Alice, 1, default);
        await store.CastAsync(Map, Spot, Lineup, Bob, 1, default);
        await store.CastAsync(Map, Spot, "other", Bob, -1, default);

        var (tallies, _) = await store.AtSpotAsync(Map, Spot, null, default);

        Assert.Equal(2, tallies[Lineup].Up);
        Assert.Equal(0, tallies[Lineup].Down);
        Assert.Equal(2, tallies[Lineup].Score);
        Assert.Equal(-1, tallies["other"].Score);
    }

    [Fact]
    public async Task OneAccountVotingTwiceCountsOnce()
    {
        // The whole reason this is a database and not a JSON file: reload and
        // click again, open three tabs and click in each - still one vote.
        for (var i = 0; i < 5; i++)
        {
            await store.CastAsync(Map, Spot, Lineup, Alice, 1, default);
        }

        var (tallies, _) = await store.AtSpotAsync(Map, Spot, null, default);

        Assert.Equal(1, tallies[Lineup].Up);
    }

    [Fact]
    public async Task ChangingYourMindReplacesTheVoteRatherThanAddingOne()
    {
        await store.CastAsync(Map, Spot, Lineup, Alice, 1, default);
        await store.CastAsync(Map, Spot, Lineup, Alice, -1, default);

        var (tallies, mine) = await store.AtSpotAsync(Map, Spot, Alice, default);

        Assert.Equal(new VoteStore.Tally(0, 1), tallies[Lineup]);
        Assert.Equal(-1, mine[Lineup]);
    }

    [Fact]
    public async Task WithdrawingLeavesNothingBehind()
    {
        await store.CastAsync(Map, Spot, Lineup, Alice, 1, default);
        await store.CastAsync(Map, Spot, Lineup, Alice, 0, default);

        var (tallies, mine) = await store.AtSpotAsync(Map, Spot, Alice, default);

        Assert.Empty(tallies);
        Assert.Empty(mine);
    }

    [Fact]
    public async Task ConcurrentVotersOnOneLineupAreAllCounted()
    {
        // Twenty accounts voting at once on the same record - the race that
        // read-modify-write on a file would lose votes to.
        var voters = Enumerable.Range(0, 20).Select(i => $"7656119800000{i:D4}").ToList();
        await Task.WhenAll(voters.Select(v => store.CastAsync(Map, Spot, Lineup, v, 1, default)));

        var (tallies, _) = await store.AtSpotAsync(Map, Spot, null, default);

        Assert.Equal(20, tallies[Lineup].Up);
    }

    [Fact]
    public async Task SpotsAndMapsDoNotBleedIntoEachOther()
    {
        await store.CastAsync(Map, Spot, Lineup, Alice, 1, default);
        await store.CastAsync(Map, "target:ffffffff", Lineup, Alice, 1, default);
        await store.CastAsync("de_mirage", Spot, Lineup, Alice, 1, default);

        var (here, _) = await store.AtSpotAsync(Map, Spot, null, default);

        Assert.Equal(1, here[Lineup].Up);
    }

    [Fact]
    public void ATargetNearANamedSpotVotesUnderItsName()
    {
        // Keyed on the id, never the name: the name is provisional until a
        // person confirms it, and renaming must not orphan every vote.
        var named = new List<(string, string, Vector3)> { ("a1b2c3d4", "near BDoors", new Vector3(-1269, 2257, 6)) };

        Assert.Equal("target:a1b2c3d4", VoteStore.TargetKey(new Vector3(-1250, 2240, 6), named, 64f));
        var renamed = new List<(string, string, Vector3)> { ("a1b2c3d4", "B doors", new Vector3(-1269, 2257, 6)) };
        Assert.Equal(VoteStore.TargetKey(new Vector3(-1250, 2240, 6), named, 64f),
                     VoteStore.TargetKey(new Vector3(-1250, 2240, 6), renamed, 64f));
        // And far from any, a 16u cell - coarser than the solve cache on
        // purpose, so nearby clicks pool rather than fragment.
        Assert.Equal("cell:-79,141", VoteStore.TargetKey(new Vector3(-1269, 2257, 6), [], 64f));
        Assert.Equal(
            VoteStore.TargetKey(new Vector3(-1269, 2257, 6), [], 64f),
            VoteStore.TargetKey(new Vector3(-1263, 2251, 6), [], 64f));
    }
}
