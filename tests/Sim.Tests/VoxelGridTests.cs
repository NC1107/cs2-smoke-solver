using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class VoxelGridTests
{
    [Fact]
    public void GroundPlaneMarksOnlyItsLayerSolid()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 256, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -64), new Vector3(256, 256, 128));

        var (x, y, zOnPlane) = grid.CellOf(new Vector3(128, 128, 0));
        Assert.True(grid.IsSolid(grid.Index(x, y, zOnPlane)));

        var (_, _, zAbove) = grid.CellOf(new Vector3(128, 128, 40));
        Assert.False(grid.IsSolid(grid.Index(x, y, zAbove)));
    }

    [Fact]
    public void WallMarksVerticalColumnSolid()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.WallY(0, 256, 128, 0, 128)]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, 0), new Vector3(256, 256, 128));

        var (x, y, z) = grid.CellOf(new Vector3(128, 128, 64));
        Assert.True(grid.IsSolid(grid.Index(x, y, z)));

        var (x2, y2, z2) = grid.CellOf(new Vector3(128, 200, 64));
        Assert.False(grid.IsSolid(grid.Index(x2, y2, z2)));
    }

    [Fact]
    public void GridOriginIsSnappedToVoxelMultiples()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(-37, 213, -11)]);
        var grid = VoxelGrid.Build(mesh, 16f);

        Assert.Equal(0, grid.Origin.X % 16f, 3);
        Assert.Equal(0, grid.Origin.Y % 16f, 3);
        Assert.Equal(0, grid.Origin.Z % 16f, 3);
    }

    [Fact]
    public void RoundTripsThroughBinaryFormat()
    {
        var floor = SyntheticMeshes.Ground(0, 64, 0);
        var wall = SyntheticMeshes.WallY(0, 64, 32, 0, 64);
        var mesh = SyntheticMeshes.FromQuads(
            [(floor.Item1, floor.Item2, floor.Item3, floor.Item4, (byte)0),
             (wall.Item1, wall.Item2, wall.Item3, wall.Item4, (byte)1)],
            ["default", "ConditionallySolid"],
            [["passbullets"], ["playerclip", "csgo_grenadeclip"]]);
        var path = Path.Combine(Path.GetTempPath(), $"smokesolver-test-{Guid.NewGuid():N}.s2geo");
        try
        {
            mesh.Save(path);
            var loaded = CollisionMesh.Load(path);
            Assert.Equal(mesh.MapName, loaded.MapName);
            Assert.Equal(mesh.GameBuildId, loaded.GameBuildId);
            Assert.Equal(mesh.Vertices, loaded.Vertices);
            Assert.Equal(mesh.Indices, loaded.Indices);
            Assert.Equal(mesh.TriangleAttributes, loaded.TriangleAttributes);
            Assert.Equal(mesh.AttributeNames, loaded.AttributeNames);
            Assert.Equal(mesh.AttributeInteractAs.Length, loaded.AttributeInteractAs.Length);
            for (var i = 0; i < mesh.AttributeInteractAs.Length; i++)
            {
                Assert.Equal(mesh.AttributeInteractAs[i], loaded.AttributeInteractAs[i]);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GrenadeSolidFilterKeepsGrenadeClipAndDropsPlayerClipNpcClipAndSky()
    {
        // Group names are ambiguous ("ConditionallySolid" covers player clips
        // AND grenade clips); the interaction layers carry the semantics, and
        // the layer match is case-insensitive.
        var mesh = new CollisionMesh
        {
            MapName = "synthetic",
            GameBuildId = "test",
            Vertices = [],
            Indices = [],
            TriangleAttributes = [],
            AttributeNames = ["default", "ConditionallySolid", "ConditionallySolid", "skybox", "npc"],
            AttributeInteractAs =
            [
                [],
                ["playerclip"],
                ["csgo_grenadeclip", "passbullets"],
                ["Sky"],
                ["NPCClip", "debris"],
            ],
        };
        var filter = mesh.GrenadeSolidFilter();

        Assert.True(filter(0));
        Assert.False(filter(1));
        Assert.True(filter(2));
        Assert.False(filter(3));
        Assert.False(filter(4));
    }

    [Fact]
    public void GrenadeSolidFilterDropsPlayerClipGeometryFromTheVoxelGrid()
    {
        var floor = SyntheticMeshes.Ground(0, 256, 0);
        var clipWall = SyntheticMeshes.WallY(0, 256, 128, 0, 128);
        var mesh = SyntheticMeshes.FromQuads(
            [(floor.Item1, floor.Item2, floor.Item3, floor.Item4, (byte)0),
             (clipWall.Item1, clipWall.Item2, clipWall.Item3, clipWall.Item4, (byte)1)],
            ["default", "ConditionallySolid"],
            [[], ["playerclip"]]);
        var unfiltered = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(256, 256, 128));
        var filtered = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(256, 256, 128), mesh.GrenadeSolidFilter());

        var (wx, wy, wz) = filtered.CellOf(new Vector3(64, 128, 64));
        Assert.True(unfiltered.IsSolid(unfiltered.Index(wx, wy, wz)));
        Assert.False(filtered.IsSolid(filtered.Index(wx, wy, wz)));

        var (fx, fy, fz) = filtered.CellOf(new Vector3(64, 64, 0));
        Assert.True(filtered.IsSolid(filtered.Index(fx, fy, fz)));
    }
}
