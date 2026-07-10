using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class SmokeFloodFillTests
{
    static VoxelGrid OpenRoomWithWall()
    {
        // Ground at z=0 with a wall across y=128, an opening nowhere: smoke on one side must stay there.
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 512, 0),
            SyntheticMeshes.WallY(0, 512, 256, 0, 256),
        ]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(512, 512, 256));
    }

    [Fact]
    public void SpreadsToRadiusInOpenSpace()
    {
        var grid = OpenRoomWithWall();
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(128, 128, 4), new SmokeParams(MaxRadius: 64f, CellBudget: int.MaxValue));

        Assert.NotEmpty(smoke.Cells);
        var (min, max) = smoke.ComputeBounds();
        Assert.True(max.X - min.X is > 96 and <= 160, $"x extent {max.X - min.X}");
        foreach (var cell in smoke.Cells)
        {
            Assert.False(grid.IsSolid(cell));
        }
    }

    [Fact]
    public void DoesNotCrossWall()
    {
        var grid = OpenRoomWithWall();
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(128, 240, 4), new SmokeParams(MaxRadius: 100f, CellBudget: int.MaxValue));

        Assert.NotEmpty(smoke.Cells);
        foreach (var cell in smoke.Cells)
        {
            var center = grid.CellCenter(cell);
            Assert.True(center.Y < 256, $"smoke leaked through wall to y={center.Y}");
        }
    }

    [Fact]
    public void RespectsCellBudget()
    {
        var grid = OpenRoomWithWall();
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(128, 128, 4), new SmokeParams(MaxRadius: 1000f, CellBudget: 50));

        Assert.Equal(50, smoke.Cells.Length);
    }

    [Fact]
    public void StaysUnderCeiling()
    {
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 512, 0),
            SyntheticMeshes.Ceiling(0, 512, 96),
        ]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(512, 512, 256));
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(256, 256, 4), new SmokeParams(MaxRadius: 100f, CellBudget: int.MaxValue));

        Assert.NotEmpty(smoke.Cells);
        foreach (var cell in smoke.Cells)
        {
            var center = grid.CellCenter(cell);
            Assert.True(center.Z < 96, $"smoke leaked above ceiling to z={center.Z}");
        }
    }

    [Fact]
    public void StartsAboveGroundWhenRestPointIsOnSolidCell()
    {
        var grid = OpenRoomWithWall();
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(128, 128, 0), new SmokeParams(MaxRadius: 64f, CellBudget: int.MaxValue));

        Assert.NotEmpty(smoke.Cells);
    }
}
