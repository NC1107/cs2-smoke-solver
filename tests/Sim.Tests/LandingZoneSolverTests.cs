using System.Numerics;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class LandingZoneSolverTests
{
    [Fact]
    public void OpenGroundZoneLiesUnderTheSightline()
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 1024, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 1024, 256));
        var sightline = new SightlineSpec(new Vector3(100, 512, 64), new Vector3(900, 512, 64));

        var raycaster = new TriangleRaycaster(mesh, grid.Origin, grid.Origin + new Vector3(grid.Nx, grid.Ny, grid.Nz) * grid.VoxelSize);
        var zone = LandingZoneSolver.Solve(grid, raycaster, [sightline], SmokeParams.Conservative).Zone;

        Assert.NotEmpty(zone);
        foreach (var cell in zone)
        {
            Assert.True(MathF.Abs(cell.Center.Y - 512) <= SmokeParams.Conservative.MaxRadius + 16,
                $"zone cell at y={cell.Center.Y} too far from the sightline");
        }
        Assert.Contains(zone, c => MathF.Abs(c.Center.X - 512) < 100 && MathF.Abs(c.Center.Y - 512) < 32);
    }

    [Fact]
    public void CellsBehindWallAreExcluded()
    {
        // Wall at y=400: smoke resting south of it cannot reach a sightline at y=512.
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 1024, 0),
            SyntheticMeshes.WallY(0, 1024, 400, 0, 256),
        ]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(1024, 1024, 256));
        var sightline = new SightlineSpec(new Vector3(100, 512, 64), new Vector3(900, 512, 64));

        var raycaster = new TriangleRaycaster(mesh, grid.Origin, grid.Origin + new Vector3(grid.Nx, grid.Ny, grid.Nz) * grid.VoxelSize);
        var zone = LandingZoneSolver.Solve(grid, raycaster, [sightline], SmokeParams.Conservative).Zone;

        Assert.NotEmpty(zone);
        foreach (var cell in zone)
        {
            Assert.True(cell.Center.Y > 400 - 16, $"zone cell at y={cell.Center.Y} is behind the wall");
        }
    }
}
