using System.Text.Json;
using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

// ValidateLineupQuery is the only gate between a public /api/lineup body and a
// minutes-long map-wide solve (plus a fresh cache file per distinct body), and
// QueryCacheKey decides which of those solves get replayed as cached answers.
// Both are pure functions; every boundary here is one a regression would turn
// into either a 500 mid-solve or a silently wrong cached result.
public class LineupApiTests
{
    // Flat 0..4096 ground: the validator only reads the mesh's XY bounds.
    static readonly CollisionMesh Mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 4096, 0)]);

    static string? Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return LineupApi.ValidateLineupQuery(doc.RootElement, Mesh);
    }

    [Fact]
    public void AMinimalTwoCoordinateQueryPasses() =>
        Assert.Null(Validate("""{"target":[100,100]}"""));

    [Fact]
    public void AFullyPopulatedValidQueryPasses() =>
        Assert.Null(Validate("""
            {"target":[100,100,32],"origin":[200,200],"originReach":16,"tolerance":512,
             "minStability":0.05,"fineScan":true,"types":["Stand","JumpThrow"],"strengths":[0,0.5,1]}
            """));

    [Theory]
    [InlineData("""[1,2]""", "body must be a JSON object")]
    [InlineData("""{}""", "target must be [x,y] or [x,y,z]")]
    [InlineData("""{"target":42}""", "target must be [x,y] or [x,y,z]")]
    [InlineData("""{"target":[1]}""", "target must be [x,y] or [x,y,z]")]
    [InlineData("""{"target":[1,2,3,4]}""", "target must be [x,y] or [x,y,z]")]
    [InlineData("""{"target":["a",2]}""", "target coordinates must be finite numbers")]
    [InlineData("""{"target":[1e39,2]}""", "target coordinates must be finite numbers")]
    public void MalformedTargetsAreRejected(string body, string expected) =>
        Assert.Equal(expected, Validate(body));

    [Fact]
    public void ATargetJustInsideTheBoundsMarginPasses() =>
        Assert.Null(Validate("""{"target":[4608,4608]}"""));

    [Theory]
    [InlineData("""{"target":[4609,100]}""")]
    [InlineData("""{"target":[100,-513]}""")]
    public void ATargetPastTheBoundsMarginIsRejected(string body) =>
        Assert.Equal("target is outside the map bounds", Validate(body));

    [Theory]
    [InlineData("""{"target":[100,100],"origin":42}""")]
    [InlineData("""{"target":[100,100],"origin":[1]}""")]
    [InlineData("""{"target":[100,100],"origin":[1,1e39]}""")]
    public void MalformedOriginsAreRejected(string body) =>
        Assert.Equal("origin must be [x,y] with finite numbers", Validate(body));

    // The reach/tolerance/stability windows: the value at each end of the range
    // passes, one step past it fails, and a non-numeric value fails.
    [Theory]
    [InlineData("""{"target":[100,100],"originReach":15.9}""", "originReach must be between 16 and 4000")]
    [InlineData("""{"target":[100,100],"originReach":4001}""", "originReach must be between 16 and 4000")]
    [InlineData("""{"target":[100,100],"originReach":"far"}""", "originReach must be between 16 and 4000")]
    [InlineData("""{"target":[100,100],"tolerance":0.5}""", "tolerance must be between 1 and 512")]
    [InlineData("""{"target":[100,100],"tolerance":513}""", "tolerance must be between 1 and 512")]
    [InlineData("""{"target":[100,100],"minStability":0.04}""", "minStability must be between 0.05 and 1")]
    [InlineData("""{"target":[100,100],"minStability":1.01}""", "minStability must be between 0.05 and 1")]
    [InlineData("""{"target":[100,100],"fineScan":"yes"}""", "fineScan must be a boolean")]
    public void OutOfRangeKnobsAreRejected(string body, string expected) =>
        Assert.Equal(expected, Validate(body));

    [Theory]
    [InlineData("""{"target":[100,100],"originReach":16}""")]
    [InlineData("""{"target":[100,100],"originReach":4000}""")]
    [InlineData("""{"target":[100,100],"tolerance":1}""")]
    [InlineData("""{"target":[100,100],"tolerance":512}""")]
    [InlineData("""{"target":[100,100],"minStability":1}""")]
    public void KnobsAtTheirExactBoundsPass(string body) =>
        Assert.Null(Validate(body));

    [Theory]
    [InlineData("""{"target":[100,100],"types":[]}""")]
    [InlineData("""{"target":[100,100],"types":["Stand","Crouch","JumpThrow","CrouchJumpThrow","RunJumpThrow","Stand"]}""")]
    [InlineData("""{"target":[100,100],"types":["Lob"]}""")]
    [InlineData("""{"target":[100,100],"types":[1]}""")]
    public void BadTypeArraysAreRejected(string body) =>
        Assert.Equal("types must be a non-empty array of throw type names", Validate(body));

    [Theory]
    [InlineData("""{"target":[100,100],"strengths":[]}""")]
    [InlineData("""{"target":[100,100],"strengths":[0.25]}""")]
    [InlineData("""{"target":[100,100],"strengths":[0,0.5,1,1]}""")]
    public void BadStrengthArraysAreRejected(string body) =>
        Assert.Equal("strengths must be a non-empty array drawn from 0, 0.5, 1", Validate(body));

    // --- QueryCacheKey ---

    static readonly ThrowConstants Constants = new();

    static string Key(string json, string meshVersion = "build-1", string attrs = "")
    {
        using var doc = JsonDocument.Parse(json);
        return LineupApi.QueryCacheKey(Mesh, meshVersion, Constants, doc.RootElement, attrs);
    }

    [Fact]
    public void AnIdenticalQueryReplaysTheSameKey() =>
        Assert.Equal(Key("""{"target":[100,100]}"""), Key("""{"target":[100.0,100.0]}"""));

    // Unbucketed on purpose: the old 16u buckets replayed one click's cached
    // re-aim for a different click up to ~22u away.
    [Fact]
    public void ANearbyButDifferentTargetGetsItsOwnKey() =>
        Assert.NotEqual(Key("""{"target":[100,100]}"""), Key("""{"target":[104,100]}"""));

    [Fact]
    public void ANearbyButDifferentOriginGetsItsOwnKey() =>
        Assert.NotEqual(Key("""{"target":[100,100],"origin":[500,500]}"""),
                        Key("""{"target":[100,100],"origin":[510,500]}"""));

    [Fact]
    public void AThreeCoordinateTargetKeysDifferentlyFromTwo() =>
        Assert.NotEqual(Key("""{"target":[100,100]}"""), Key("""{"target":[100,100,0]}"""));

    // Every input that changes the answer must change the key, or two queries
    // differing only in that input replay each other's cached results.
    [Theory]
    [InlineData("""{"target":[100,100],"origin":[500,500]}""")]
    [InlineData("""{"target":[100,100],"originReach":200}""")]
    [InlineData("""{"target":[100,100],"tolerance":40}""")]
    [InlineData("""{"target":[100,100],"minStability":0.8}""")]
    [InlineData("""{"target":[100,100],"fineScan":true}""")]
    [InlineData("""{"target":[100,100],"types":["Stand"]}""")]
    [InlineData("""{"target":[100,100],"strengths":[0.5]}""")]
    public void EveryAnswerChangingInputChangesTheKey(string body) =>
        Assert.NotEqual(Key("""{"target":[100,100]}"""), Key(body));

    [Fact]
    public void AChangedMeshVersionChangesTheKey() =>
        Assert.NotEqual(Key("""{"target":[100,100]}""", meshVersion: "build-1"),
                        Key("""{"target":[100,100]}""", meshVersion: "build-2"));

    [Fact]
    public void ChangedAttrsChangeTheKey() =>
        Assert.NotEqual(Key("""{"target":[100,100]}""", attrs: ""),
                        Key("""{"target":[100,100]}""", attrs: "Default"));

    [Fact]
    public void ChangedConstantsChangeTheKey()
    {
        using var doc = JsonDocument.Parse("""{"target":[100,100]}""");
        var a = LineupApi.QueryCacheKey(Mesh, "build-1", Constants, doc.RootElement, "");
        var b = LineupApi.QueryCacheKey(Mesh, "build-1", Constants with { GravityScale = 0.41f }, doc.RootElement, "");
        Assert.NotEqual(a, b);
    }
}
