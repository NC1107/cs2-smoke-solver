using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class OcclusionTests
{
    static VoxelGrid OpenGround()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 1024, 0)]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 1024, 256));
    }

    [Fact]
    public void SmokeBetweenEyesBlocksSightline()
    {
        var grid = OpenGround();
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(512, 512, 4), SmokeParams.Conservative);

        var result = Occlusion.Test(smoke, new Vector3(100, 512, 64), new Vector3(900, 512, 64));
        Assert.False(result.GeometryBlocked);
        Assert.True(result.SmokeBlocked(minSmokeCells: 3), $"only {result.SmokeCellsCrossed} smoke cells crossed");
    }

    [Fact]
    public void OffsetSmokeDoesNotBlockSightline()
    {
        var grid = OpenGround();
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(512, 200, 4), SmokeParams.Conservative);

        var result = Occlusion.Test(smoke, new Vector3(100, 512, 64), new Vector3(900, 512, 64));
        Assert.False(result.GeometryBlocked);
        Assert.Equal(0, result.SmokeCellsCrossed);
    }

    [Fact]
    public void WallRegistersAsGeometryBlocked()
    {
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 1024, 0),
            SyntheticMeshes.WallY(0, 1024, 512, 0, 256),
        ]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 1024, 256));
        var smoke = SmokeFloodFill.Fill(grid, new Vector3(512, 200, 4), SmokeParams.Conservative);

        var result = Occlusion.Test(smoke, new Vector3(512, 100, 64), new Vector3(512, 900, 64));
        Assert.True(result.GeometryBlocked);
    }

    static VoxelGrid WalledGround()
    {
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 1024, 0),
            SyntheticMeshes.WallY(0, 1024, 512, 0, 256),
        ]);
        return VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 1024, 256));
    }

    [Fact]
    public void SightlineStartingOutsideTheGridStillDetectsTheWall()
    {
        var grid = WalledGround();
        var result = Occlusion.Test(SmokeVolume.CreateEmpty(grid), new Vector3(512, -200, 64), new Vector3(512, 900, 64));

        Assert.True(result.GeometryBlocked);
        Assert.NotNull(result.FirstSolidHit);
        Assert.True(MathF.Abs(result.FirstSolidHit!.Value.Y - 512) <= grid.VoxelSize,
            $"first solid hit at y={result.FirstSolidHit.Value.Y}, expected the wall at y=512");
        Assert.Equal(0, result.SmokeCellsCrossed);
    }

    [Fact]
    public void SightlineFullyOutsideTheGridReportsClear()
    {
        var grid = WalledGround();
        var result = Occlusion.Test(SmokeVolume.CreateEmpty(grid), new Vector3(512, 512, 2000), new Vector3(900, 900, 2400));

        Assert.False(result.GeometryBlocked);
        Assert.Null(result.FirstSolidHit);
        Assert.Equal(0, result.SmokeCellsCrossed);
    }

    [Fact]
    public void AxisAlignedSightlineAlongCellBoundaryTerminatesAndSeesTheWall()
    {
        // x=512 and z=64 sit exactly on 16u cell boundaries; the march along y
        // must neither wedge on the boundary nor miss the wall.
        var grid = WalledGround();
        var blocked = Occlusion.Test(SmokeVolume.CreateEmpty(grid), new Vector3(512, 100, 64), new Vector3(512, 900, 64));
        Assert.True(blocked.GeometryBlocked);

        var clear = Occlusion.Test(SmokeVolume.CreateEmpty(grid), new Vector3(512, 100, 64), new Vector3(512, 400, 64));
        Assert.False(clear.GeometryBlocked);
    }
}
