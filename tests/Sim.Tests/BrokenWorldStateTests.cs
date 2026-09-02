using System.Numerics;
using System.Text.Json;
using SmokeSolver.Cli;

namespace SmokeSolver.Sim.Tests;

// The "glass broken / doors open" world states. A query names them ("glass",
// "doors"); extraction put that geometry in its own collision groups; the
// solver has to drop exactly those groups from BOTH the voxel grid and the
// exact collider, or a lineup verified against intact glass ships as if the
// pane were gone (or the reverse).
public class BrokenWorldStateTests
{
    // A room whose only ceiling is "glass": the group name is what the world
    // state keys on, so the test mesh names its groups the same way extraction
    // does rather than relying on any real map file.
    static CollisionMesh RoomWithGlassRoof()
    {
        var solid = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 512, 0)]);
        var glass = SyntheticMeshes.FromQuads([SyntheticMeshes.Ceiling(0, 512, 128)]);
        var vertices = solid.Vertices.Concat(glass.Vertices).ToArray();
        var indices = solid.Indices
            .Concat(glass.Indices.Select(i => i + solid.Vertices.Length / 3))
            .ToArray();
        var attributes = solid.TriangleAttributes
            .Select(_ => (byte)0)
            .Concat(glass.TriangleAttributes.Select(_ => (byte)1))
            .ToArray();
        return new CollisionMesh
        {
            MapName = "test_glass",
            GameBuildId = "test",
            Vertices = vertices,
            Indices = indices,
            TriangleAttributes = attributes,
            AttributeNames = ["Default", "EntityBreakable"],
            AttributeInteractAs = [[], []],
        };
    }

    [Fact]
    public void GroupMaskFlagsExactlyTheNamedGroups()
    {
        var mesh = RoomWithGlassRoof();

        var mask = mesh.GroupMask(["EntityBreakable"]);

        Assert.False(mask[0]);
        Assert.True(mask[1]);
    }

    [Fact]
    public void GroupMaskIgnoresGroupsTheMapDoesNotHave()
    {
        var mesh = RoomWithGlassRoof();

        var mask = mesh.GroupMask(["EntityDoor"]);

        Assert.DoesNotContain(true, mask);
    }

    [Fact]
    public void AThrowStopsOnIntactGlassAndPassesThroughWhenBroken()
    {
        var mesh = RoomWithGlassRoof();
        var min = new Vector3(0, 0, -32);
        var max = new Vector3(512, 512, 512);
        // Straight up from the middle of the room, into the pane at z=128.
        var spec = new ThrowSpec(new Vector3(256, 256, 64), YawDeg: 0f, PitchDeg: -89f, ThrowType.Stand, Strength: 1f);

        var intact = GrenadeTrajectory.SimulateExact(MeshSetup.BuildGrenadeCollider(mesh, min, max), spec);
        var broken = GrenadeTrajectory.SimulateExact(
            MeshSetup.BuildGrenadeColliderExcluding(mesh, min, max, ["EntityBreakable"]), spec);

        // Intact: the pane is in the way, so the throw bounces off it.
        Assert.True(intact.Bounces > 0, "expected the intact pane to be hit");
        // Broken: nothing between the grenade and the sky, so it leaves the
        // region instead of coming to rest under the roof.
        Assert.True(broken.Lost || broken.Bounces < intact.Bounces,
            $"broken-glass throw should not bounce off the removed pane (bounces {broken.Bounces} vs {intact.Bounces})");
    }

    [Theory]
    [InlineData("""{"target":[1,1],"broken":["glass"]}""", new[] { "EntityBreakable" })]
    [InlineData("""{"target":[1,1],"broken":["doors"]}""", new[] { "EntityDoor" })]
    // Sorted and de-duplicated, so every spelling of one state shares a cache
    // entry and a collider.
    [InlineData("""{"target":[1,1],"broken":["glass","doors"]}""", new[] { "EntityBreakable", "EntityDoor" })]
    [InlineData("""{"target":[1,1],"broken":["doors","glass","doors"]}""", new[] { "EntityBreakable", "EntityDoor" })]
    [InlineData("""{"target":[1,1]}""", new string[0])]
    public void BrokenGroupsParsesAndCanonicalizesTheWorldState(string body, string[] expected)
    {
        using var doc = JsonDocument.Parse(body);

        Assert.Equal(expected, LineupApi.BrokenGroups(doc.RootElement));
    }

    [Fact]
    public void EachWorldStateGetsItsOwnCacheKey()
    {
        var mesh = RoomWithGlassRoof();
        var constants = new ThrowConstants();
        string Key(string body)
        {
            using var doc = JsonDocument.Parse(body);
            return LineupApi.QueryCacheKey(mesh, "build-1", constants, doc.RootElement, "");
        }

        var intact = Key("""{"target":[100,100]}""");
        var glass = Key("""{"target":[100,100],"broken":["glass"]}""");
        var doors = Key("""{"target":[100,100],"broken":["doors"]}""");
        var both = Key("""{"target":[100,100],"broken":["glass","doors"]}""");

        Assert.Equal(4, new HashSet<string> { intact, glass, doors, both }.Count);
        // Order must not fork the cache.
        Assert.Equal(both, Key("""{"target":[100,100],"broken":["doors","glass"]}"""));
    }

    // The sweep and the verifier see the world through two different objects: a
    // VoxelGrid built from `attributeFilter`, and an exact TriangleCollider
    // built from the interactAs grenade filter. Everything above tests only the
    // collider. If the grid ever stopped honouring the broken groups, the sweep
    // would treat the glass as solid and prune every candidate through it
    // BEFORE verification ran - a zero-lineup result specific to broken-state
    // queries, with the collider tests still green.
    [Fact]
    public void BreakingGlassAlsoOpensItInTheSweepsVoxelGrid()
    {
        var mesh = RoomWithGlassRoof();
        var min = new Vector3(0, 0, -32);
        var max = new Vector3(512, 512, 256);

        var intactGrid = VoxelGrid.Build(mesh, 16f, min, max, _ => true);
        // Exactly the filter RunTargetQuery composes for broken:["glass"].
        var excluded = mesh.GroupMask(["EntityBreakable"]);
        var brokenGrid = VoxelGrid.Build(mesh, 16f, min, max, a => !excluded[a]);

        var atGlass = new Vector3(256, 256, 128);
        var (gx, gy, gz) = intactGrid.CellOf(atGlass);

        Assert.True(intactGrid.IsSolid(intactGrid.Index(gx, gy, gz)),
            "the intact pane must read solid to the sweep");
        Assert.False(brokenGrid.IsSolid(brokenGrid.Index(gx, gy, gz)),
            "breaking the glass must open it for the SWEEP, not only for the exact collider");

        // And the floor is untouched either way - the filter must remove the
        // named group and nothing else.
        var (fx, fy, fz) = intactGrid.CellOf(new Vector3(256, 256, 0));
        Assert.True(intactGrid.IsSolid(intactGrid.Index(fx, fy, fz)));
        Assert.True(brokenGrid.IsSolid(brokenGrid.Index(fx, fy, fz)));
    }
}
