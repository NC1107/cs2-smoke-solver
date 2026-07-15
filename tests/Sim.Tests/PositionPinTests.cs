using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

// The wall/corner pin logic is load-bearing twice over: it generates extra
// origins (the positions real lineups use) and it classifies every result the
// API returns. Synthetic room: ground plane with two perpendicular walls
// meeting at the origin corner, tall enough to cross the waist-height probe.
public class PositionPinTests
{
    static readonly Vector3 BoundsMin = new(-32, -32, -16);
    static readonly Vector3 BoundsMax = new(512, 512, 256);

    static CollisionMesh CornerRoom() => SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-32, 512, 0),
        SyntheticMeshes.WallX(0, -32, 512, 0, 128),
        SyntheticMeshes.WallY(-32, 512, 0, 0, 128),
    ]);

    static TriangleCollider Collider() =>
        new(CornerRoom(), BoundsMin, BoundsMax);

    [Fact]
    public void FeetWedgedIntoTheCornerClassifyAsCorner()
    {
        // Hull half-width is 16: feet at (16, 16) touch both walls.
        Assert.Equal(2, LineupSolver.PositionPin(Collider(), new Vector3(16f, 16f, 0f)));
    }

    [Fact]
    public void FeetAgainstOneWallClassifyAsWall()
    {
        Assert.Equal(1, LineupSolver.PositionPin(Collider(), new Vector3(16f, 300f, 0f)));
    }

    [Fact]
    public void FeetOnOpenGroundClassifyAsOpen()
    {
        Assert.Equal(0, LineupSolver.PositionPin(Collider(), new Vector3(300f, 300f, 0f)));
    }

    [Fact]
    public void ExactOriginIncludesTheSeedAndItsPinnedVariants()
    {
        var mesh = CornerRoom();
        var grid = VoxelGrid.Build(mesh, 16f, BoundsMin, BoundsMax);
        var collider = new TriangleCollider(mesh, BoundsMin, BoundsMax);

        // Seed on open ground within wall-probe range of both walls.
        var origins = LineupSolver.ExactOriginWithPins(grid, collider, new Vector3(48f, 48f, 8f));

        // The seed itself survives (ground-snapped, x/y untouched).
        Assert.Contains(origins, o => MathF.Abs(o.X - 48f) < 0.01f && MathF.Abs(o.Y - 48f) < 0.01f);
        // Wall-pressed variants sit one hull half-width off each wall plane.
        Assert.Contains(origins, o => MathF.Abs(o.X - 16f) < 0.5f && MathF.Abs(o.Y - 48f) < 0.5f);
        Assert.Contains(origins, o => MathF.Abs(o.Y - 16f) < 0.5f && MathF.Abs(o.X - 48f) < 0.5f);
        // And the corner wedge pins both axes at once.
        Assert.Contains(origins, o => MathF.Abs(o.X - 16f) < 0.5f && MathF.Abs(o.Y - 16f) < 0.5f);
    }
}
