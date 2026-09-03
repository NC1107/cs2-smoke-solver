using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class SmokeFloodFillTests
{
    static VoxelGrid OpenRoomWithWall()
    {
        // Ground at z=0 with a wall across y=256, an opening nowhere: smoke on one side must stay there.
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

    // A corridor 64u wide with a ceiling at 128u, running along x. Gas set
    // loose in it has one way to go: along.
    static VoxelGrid Corridor()
    {
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 2048, 0),
            SyntheticMeshes.Ceiling(0, 2048, 128),
            SyntheticMeshes.WallY(0, 2048, 224, 0, 128),
            SyntheticMeshes.WallY(0, 2048, 288, 0, 128),
        ]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(2048, 512, 256));
    }

    [Fact]
    public void ConfinedSmokeSpendsItsBudgetAlongTheCorridorInsteadOfStoppingAtTheSphere()
    {
        // The game's smoke is a fixed amount of gas: boxed into mid doors it
        // goes further along and higher than a ball of the same volume in
        // the open. A hard radius cap stopped the fill exactly where the
        // game keeps going; the stretch lets the budget be what runs out.
        var grid = Corridor();
        var at = new Vector3(1024, 256, 4);
        var capped = SmokeFloodFill.Fill(grid, at, new SmokeParams(SmokeParams.CoverageRadius, SmokeParams.GameCellBudget));
        var stretched = SmokeFloodFill.Fill(grid, at, SmokeParams.Coverage);

        var (cmin, cmax) = capped.ComputeBounds();
        var (smin, smax) = stretched.ComputeBounds();
        Assert.True(cmax.X - cmin.X <= 2 * SmokeParams.CoverageRadius + 16, $"capped ran {cmax.X - cmin.X}u along the corridor");
        Assert.True(smax.X - smin.X > cmax.X - cmin.X + 64, $"stretched fill ran {smax.X - smin.X}u, capped {cmax.X - cmin.X}u");
        Assert.True(stretched.Cells.Length > capped.Cells.Length, "the stretched fill should spend more of its budget");
        foreach (var cell in stretched.Cells)
        {
            Assert.False(grid.IsSolid(cell));
        }
    }

    [Fact]
    public void InTheOpenTheStretchChangesNothing()
    {
        // The budget is spent well inside the stretched sphere on open
        // ground, so the same smoke lands the same in the open.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 1024, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 1024, 512));
        var at = new Vector3(512, 512, 4);
        var capped = SmokeFloodFill.Fill(grid, at, new SmokeParams(SmokeParams.CoverageRadius, SmokeParams.GameCellBudget));
        var stretched = SmokeFloodFill.Fill(grid, at, SmokeParams.Coverage);

        Assert.Equal(capped.Cells.Length, stretched.Cells.Length);
        var (cmin, cmax) = capped.ComputeBounds();
        var (smin, smax) = stretched.ComputeBounds();
        Assert.Equal(cmax.X - cmin.X, smax.X - smin.X, 16f);
    }

    [Fact]
    public void SmokeSpillsOffALedge()
    {
        // Smoke settles. From a landing at the lip of a ledge the fill should
        // pour a good way down over the drop, not just hang in a ball around
        // the landing with a thin skirt below the edge.
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 512, 160),   // the ledge (cat)
            SyntheticMeshes.Ground(512, 1024, 0),  // the floor below (CT)
            SyntheticMeshes.WallX(512, 0, 512, 0, 160), // the ledge's face
            SyntheticMeshes.WallY(0, 1024, 0, 0, 600),
            SyntheticMeshes.WallY(0, 1024, 512, 0, 600),
        ]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 512, 800));
        var at = new Vector3(480, 256, 164);
        var smoke = SmokeFloodFill.Fill(grid, at, SmokeParams.Coverage);

        var (min, max) = smoke.ComputeBounds();
        var down = at.Z - min.Z;
        Assert.True(down >= 96f, $"reached only {down:F0}u down over the drop");
        Assert.Contains(smoke.Cells, c => grid.CellCenter(c).X > 540 && grid.CellCenter(c).Z < 80);
    }
}
