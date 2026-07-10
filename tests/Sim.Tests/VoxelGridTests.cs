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
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 64, 0)]);
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
        }
        finally
        {
            File.Delete(path);
        }
    }
}
