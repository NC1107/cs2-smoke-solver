using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

public class TriangleColliderTests
{
    static TriangleCollider WallScene(float cellSize = 128f)
    {
        // Single wall in the xz plane at y=128, spanning x 0..256 and z 0..256.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.WallY(0, 256, 128, 0, 256)]);
        return new TriangleCollider(mesh, new Vector3(0, 0, 0), new Vector3(256, 256, 256), cellSize: cellSize);
    }

    static TriangleCollider FloorScene(float cellSize = 128f)
    {
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 256, 0)]);
        return new TriangleCollider(mesh, new Vector3(0, 0, -64), new Vector3(256, 256, 256), cellSize: cellSize);
    }

    [Fact]
    public void FirstHitOnWallReportsCrossingTimeAndFacingNormal()
    {
        var collider = WallScene();
        var hit = collider.FirstHit(new Vector3(128, 32, 64), new Vector3(128, 224, 64));

        Assert.NotNull(hit);
        Assert.Equal((128f - 32f) / (224f - 32f), hit.Value.T, 4);
        // The normal faces back against the ray, not along the winding.
        Assert.Equal(-1f, hit.Value.Normal.Y, 4);
        Assert.Equal(0f, hit.Value.Normal.X, 4);
        Assert.Equal(0f, hit.Value.Normal.Z, 4);
    }

    [Fact]
    public void FirstHitMissesRayParallelToWall()
    {
        var collider = WallScene();
        Assert.Null(collider.FirstHit(new Vector3(32, 100, 64), new Vector3(224, 100, 64)));
    }

    [Fact]
    public void FirstHitFromOutsideTheGridRegionStillFindsTheWall()
    {
        // The start point lies 500u outside the grid; the DDA must clip its entry
        // to the region instead of walking from an out-of-range cell.
        var collider = WallScene();
        var hit = collider.FirstHit(new Vector3(128, -500, 64), new Vector3(128, 200, 64));

        Assert.NotNull(hit);
        Assert.Equal((128f + 500f) / (200f + 500f), hit.Value.T, 3);
        Assert.Equal(-1f, hit.Value.Normal.Y, 4);
    }

    [Fact]
    public void LongDiagonalDdaRayAgreesWithShortSegmentAabbPath()
    {
        // Same scene, two cell sizes: with 1024u cells the segment spans a single
        // cell (AABB walk); with 64u cells it spans >5 cells (DDA branch). Both
        // must find the same triangle hit.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 1024, 0)]);
        var coarse = new TriangleCollider(mesh, new Vector3(0, 0, -64), new Vector3(1024, 1024, 512), cellSize: 1024f);
        var fine = new TriangleCollider(mesh, new Vector3(0, 0, -64), new Vector3(1024, 1024, 512), cellSize: 64f);
        var from = new Vector3(100, 100, 300);
        var to = new Vector3(900, 900, -50);

        var aabbHit = coarse.FirstHit(from, to);
        var ddaHit = fine.FirstHit(from, to);

        Assert.NotNull(aabbHit);
        Assert.NotNull(ddaHit);
        Assert.Equal(300f / 350f, aabbHit.Value.T, 4);
        Assert.Equal(aabbHit.Value.T, ddaHit.Value.T, 4);
        Assert.Equal(1f, aabbHit.Value.Normal.Z, 4);
        Assert.Equal(1f, ddaHit.Value.Normal.Z, 4);
    }

    [Fact]
    public void HullSweepOntoFloorReportsImpactTimeAndUpNormal()
    {
        var collider = FloorScene();
        // Hull bottom face starts at z=48 and reaches the floor when the center
        // is at z=2, i.e. 48/60 of the way down.
        var hit = collider.FirstHitHull(new Vector3(128, 128, 50), new Vector3(128, 128, -10), new Vector3(2, 2, 2));

        Assert.NotNull(hit);
        Assert.True(hit.Value.T is > 0f and < 1f, $"expected impact inside the step, got t={hit.Value.T}");
        Assert.Equal(0.8f, hit.Value.T, 4);
        Assert.Equal(1f, hit.Value.Normal.Z, 4);
    }

    [Fact]
    public void HullRestingOnFloorSweepingAwayReportsNoHit()
    {
        // A hull whose face exactly touches the plane and moves away must not
        // ghost-collide the backface of the surface it just left.
        var collider = FloorScene();
        Assert.Null(collider.FirstHitHull(new Vector3(128, 128, 2), new Vector3(128, 128, 10), new Vector3(2, 2, 2)));
    }

    [Fact]
    public void HullRestingOnFloorSweepingIntoReportsTimeZeroWithSurfaceNormal()
    {
        var collider = FloorScene();
        var hit = collider.FirstHitHull(new Vector3(128, 128, 2), new Vector3(128, 128, -6), new Vector3(2, 2, 2));

        Assert.NotNull(hit);
        Assert.Equal(0f, hit.Value.T);
        Assert.Equal(1f, hit.Value.Normal.Z, 4);
    }

    [Fact]
    public void HullGrazeParallelToWallReportsNoHit()
    {
        // Hull face flush against the wall at y=128, falling straight down: the
        // direction is perpendicular to the wall normal, so it must fall freely.
        var collider = WallScene();
        Assert.Null(collider.FirstHitHull(new Vector3(128, 126, 100), new Vector3(128, 126, 40), new Vector3(2, 2, 2)));
    }

    [Fact]
    public void HullGrazeWithinToleranceReportsNoHitButRealIncidenceHits()
    {
        var collider = WallScene();
        var start = new Vector3(128, 126, 100);

        // Incidence dot ~0.005 into the wall: inside the graze tolerance, no hit.
        Assert.Null(collider.FirstHitHull(start, start + new Vector3(0, 0.3f, -60f), new Vector3(2, 2, 2)));

        // Incidence dot ~0.033: a real (if shallow) impact against the wall face.
        var hit = collider.FirstHitHull(start, start + new Vector3(0, 2f, -60f), new Vector3(2, 2, 2));
        Assert.NotNull(hit);
        Assert.Equal(0f, hit.Value.T);
        Assert.Equal(-1f, hit.Value.Normal.Y, 4);
    }

    [Fact]
    public void BoxIntersectsFindsFloorAndMissesOpenSpace()
    {
        var collider = FloorScene();
        Assert.True(collider.BoxIntersects(new Vector3(128, 128, 1), new Vector3(4, 4, 4)));
        Assert.False(collider.BoxIntersects(new Vector3(128, 128, 50), new Vector3(4, 4, 4)));
    }
}
