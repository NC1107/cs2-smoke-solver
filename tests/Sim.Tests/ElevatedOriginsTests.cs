using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

// Valve's nav mesh is authored for bot pathing and omits the crates and
// platforms players stand on, so origin generation adds reachable elevated
// surfaces itself. These pin the reachability rules that keep that addition
// from degenerating into "stand anywhere the geometry allows".
public class ElevatedOriginsTests
{
    static readonly Vector3 BoundsMin = new(0, 0, -16);
    static readonly Vector3 BoundsMax = new(1024, 1024, 512);

    static float[][] Square(float minX, float minY, float maxX, float maxY, float z) =>
    [
        [minX, minY, z],
        [maxX, minY, z],
        [maxX, maxY, z],
        [minX, maxY, z],
    ];

    // Ground plus a solid box of the given top height, walled on all four sides
    // so the voxelizer fills it as a solid volume rather than a floating lid.
    // Returns the grid AND a collider over the same mesh: elevated origins are
    // only admitted when a real player-standable surface can be confirmed under
    // the sample, so the collider is not optional here the way it is for
    // flat-ground nav sampling.
    static (VoxelGrid Grid, TriangleCollider Collider) GroundWithBox(
        float boxMinX, float boxMinY, float boxMaxX, float boxMaxY, float top)
    {
        var mesh = SyntheticMeshes.FromQuads(
        [
            SyntheticMeshes.Ground(0, 1024, 0),
            SyntheticMeshes.Ground(boxMinX, boxMaxX, top),
            SyntheticMeshes.WallY(boxMinX, boxMaxX, boxMinY, 0, top),
            SyntheticMeshes.WallY(boxMinX, boxMaxX, boxMaxY, 0, top),
            SyntheticMeshes.WallX(boxMinX, boxMinY, boxMaxY, 0, top),
            SyntheticMeshes.WallX(boxMaxX, boxMinY, boxMaxY, 0, top),
        ]);
        return (VoxelGrid.Build(mesh, 16f, BoundsMin, BoundsMax),
                new TriangleCollider(mesh, BoundsMin, BoundsMax));
    }

    [Fact]
    public void CrateTopWithinJumpHeightBecomesAnOrigin()
    {
        // A 48u crate beside walkable ground: reachable with a plain jump, and
        // exactly the case the nav mesh never marks walkable.
        var (grid, collider) = GroundWithBox(400, 400, 560, 560, 48);
        var walkable = Square(100, 100, 900, 900, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [walkable], BoundsMin, BoundsMax, 32f, collider);

        // Without a collider to snap against, the origin sits on the voxel
        // boundary above the crate rather than exactly on its 48u lid: a
        // surface lying on a boundary marks the cells on BOTH sides solid, so
        // the first free cell starts a voxel higher. What matters is that the
        // crate top produced an origin at all - the ground beneath it yields
        // z=16, so anything up here is the crate.
        Assert.Contains(origins, o =>
            o.X >= 400 && o.X <= 560 && o.Y >= 400 && o.Y <= 560 && o.Z >= 40 && o.Z <= 72);
    }

    [Fact]
    public void SurfaceTooHighToJumpOntoIsNotAnOrigin()
    {
        // 160u needs a teammate boost, which is not a lineup one player can
        // reproduce - the reachability gate must reject it.
        var (grid, collider) = GroundWithBox(400, 400, 560, 560, 160);
        var walkable = Square(100, 100, 900, 900, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [walkable], BoundsMin, BoundsMax, 32f, collider);

        Assert.DoesNotContain(origins, o =>
            o.X >= 400 && o.X <= 560 && o.Y >= 400 && o.Y <= 560 && o.Z > 100);
    }

    [Fact]
    public void SurfaceTooNarrowForThePlayerHullIsNotAnOrigin()
    {
        // A 16u post top at jumpable height: something is underfoot, but a
        // 32x32 hull cannot stand on it, and each bogus origin costs the sweep
        // a full set of throw simulations.
        var (grid, collider) = GroundWithBox(400, 400, 416, 416, 48);
        var walkable = Square(100, 100, 900, 900, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [walkable], BoundsMin, BoundsMax, 32f, collider);

        Assert.DoesNotContain(origins, o =>
            o.X >= 380 && o.X <= 436 && o.Y >= 380 && o.Y <= 436 && o.Z > 24);
    }

    [Fact]
    public void EveryElevatedOriginStandsOnRealGeometryRatherThanFloating()
    {
        // A voxel cell is 16u wide, so along a crate's EDGE the cell reads
        // solid (part of it covers the crate) while a sample inside it can hang
        // out over the drop. Trusting the voxel there put feet in mid air -
        // measured on de_dust2 [-2044,504]: z=80 with the nearest real floor at
        // z=22. Every origin must sit on an actual player-standable surface.
        var (grid, collider) = GroundWithBox(400, 400, 560, 560, 48);
        var walkable = Square(100, 100, 900, 900, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [walkable], BoundsMin, BoundsMax, 32f, collider);

        foreach (var o in origins.Where(o => o.Z > 24))
        {
            var hit = collider.FirstHit(o with { Z = o.Z + 20 }, o with { Z = o.Z - 20 });
            Assert.True(hit is { } h && h.Normal.Z >= 0.7f,
                $"origin {o} is not standing on any player-standable surface");
        }
    }

    [Fact]
    public void ElevatedSurfaceOutOfReachOfAnyNavAreaIsNotAnOrigin()
    {
        // The rooftop case the nav-only design exists to exclude: a jumpable-
        // height surface is still off limits when no walkable ground sits
        // within reach of it.
        var (grid, collider) = GroundWithBox(400, 400, 560, 560, 48);
        // Walkable ground confined to the far corner, well beyond the box.
        var walkable = Square(100, 100, 200, 200, 0);

        var origins = LineupSolver.OriginsFromNavAreas(grid, [walkable], BoundsMin, BoundsMax, 32f, collider);

        Assert.DoesNotContain(origins, o => o.Z > 24);
    }
}
