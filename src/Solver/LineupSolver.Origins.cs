using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

// Origin generation: where a player can stand. Self-contained - nothing here
// depends on the sweep/refine/rank half of the solver except sharing the
// class, which is why it lives in its own file.
public static partial class LineupSolver
{
    /// <summary>
    /// Feet positions sampled from nav-mesh walkable areas: reachable by definition,
    /// unlike raw geometry scanning which happily stands players on rooftops.
    /// Sample z starts from the area plane and snaps to the voxelized ground.
    /// </summary>
    public static List<Vector3> OriginsFromNavAreas(
        VoxelGrid grid,
        IReadOnlyList<float[][]> areaCorners,
        Vector3 min,
        Vector3 max,
        float sampleStep = 32f,
        TriangleCollider? collider = null)
    {
        var origins = new List<Vector3>();
        foreach (var corners in areaCorners)
        {
            var minX = corners.Min(c => c[0]);
            var maxX = corners.Max(c => c[0]);
            var minY = corners.Min(c => c[1]);
            var maxY = corners.Max(c => c[1]);
            if (maxX < min.X || minX > max.X || maxY < min.Y || minY > max.Y)
            {
                continue;
            }
            var avgZ = corners.Average(c => c[2]);
            if (avgZ < min.Z || avgZ > max.Z)
            {
                continue;
            }
            var countBefore = origins.Count;
            for (var x = MathF.Ceiling(minX / sampleStep) * sampleStep; x <= maxX; x += sampleStep)
            {
                for (var y = MathF.Ceiling(minY / sampleStep) * sampleStep; y <= maxY; y += sampleStep)
                {
                    if (x < min.X || x > max.X || y < min.Y || y > max.Y || !PointInPolygon(corners, x, y))
                    {
                        continue;
                    }
                    origins.Add(SnapToGround(grid, collider, new Vector3(x, y, avgZ)));
                }
            }
            // Tiny areas can miss every grid sample; keep their centroid so narrow
            // ledges and stair areas still contribute origins.
            if (origins.Count == countBefore)
            {
                var cx = corners.Average(c => c[0]);
                var cy = corners.Average(c => c[1]);
                if (cx >= min.X && cx <= max.X && cy >= min.Y && cy <= max.Y)
                {
                    origins.Add(SnapToGround(grid, collider, new Vector3(cx, cy, avgZ)));
                }
            }
        }
        return origins;
    }

    /// <summary>
    /// Ground z for a 2D point from the nav mesh: the walkable surface a player
    /// (and therefore a smoke target) would be on. A top-down geometry scan would
    /// pick roofs and arches instead. With stacked walkable areas the lowest wins.
    /// </summary>
    public static float? NavGroundZ(IReadOnlyList<float[][]> areaCorners, float x, float y)
    {
        float? best = null;
        foreach (var corners in areaCorners)
        {
            if (PointInPolygon(corners, x, y))
            {
                var z = corners.Average(c => c[2]);
                if (best == null || z < best)
                {
                    best = z;
                }
            }
        }
        return best;
    }

    static bool PointInPolygon(float[][] corners, float x, float y)
    {
        var inside = false;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var (xi, yi) = (corners[i][0], corners[i][1]);
            var (xj, yj) = (corners[j][0], corners[j][1]);
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    static Vector3 SnapToGround(VoxelGrid grid, TriangleCollider? collider, Vector3 p)
    {
        var (x, y, z) = grid.CellOf(p + new Vector3(0, 0, 40));
        if (!grid.InBounds(x, y, Math.Clamp(z, 1, grid.Nz - 1)))
        {
            return p;
        }
        z = Math.Clamp(z, 1, grid.Nz - 1);
        for (var k = z; k >= Math.Max(1, z - 8); k--)
        {
            if (!grid.IsSolid(grid.Index(x, y, k)) && grid.IsSolid(grid.Index(x, y, k - 1)))
            {
                var center = grid.CellCenter(x, y, k);
                return OnSurface(grid, collider, new Vector3(p.X, p.Y, center.Z - grid.VoxelSize / 2));
            }
        }
        return p;
    }

    // A player can stand on anything up to Source's 45.57 degree slope limit.
    // Same physical value as GrenadeTrajectory.FloorNormalZ (sv_standable_normal).
    const float StandableNormalZ = 0.7f;

    /// <summary>
    /// Drops a voxel-derived foot position onto the collision surface underneath it.
    /// </summary>
    // The grid only knows its own 16u cells, so the closest it can put a floor is
    // the cell boundary above it - up to a whole voxel too high. Everywhere else
    // that is close enough, but an origin is a promise: it goes out to the player
    // as a setpos, and the game then drops them onto the real floor. Simulating
    // the throw from the cell boundary therefore models a release up to 16u higher
    // than the one they can actually make, which carries the grenade further and
    // higher than it goes in game - measured 8u too high on a mirage lineup that
    // the sim landed and the player could not.
    static Vector3 OnSurface(VoxelGrid grid, TriangleCollider? collider, Vector3 feet)
    {
        if (collider == null)
        {
            return feet;
        }
        // The cell above the floor is empty by construction, so the first thing a
        // downward ray from its top can hit is the floor itself.
        var from = feet with { Z = feet.Z + grid.VoxelSize };
        var to = feet with { Z = feet.Z - grid.VoxelSize };
        return collider.FirstHit(from, to) is { } hit && hit.Normal.Z >= StandableNormalZ
            ? feet with { Z = float.Lerp(from.Z, to.Z, hit.T) }
            : feet;
    }

    /// <summary>
    /// Feet positions a player can stand at: a free cell over solid ground with
    /// head room, sampled every second column to keep the sweep tractable.
    /// </summary>
    static List<Vector3> FindStandableOrigins(VoxelGrid grid, Vector3 min, Vector3 max, TriangleCollider? collider = null)
    {
        var origins = new List<Vector3>();
        var (x0, y0, z0) = grid.CellOf(min);
        var (x1, y1, z1) = grid.CellOf(max);
        x0 = Math.Max(x0, 0);
        y0 = Math.Max(y0, 0);
        z0 = Math.Max(z0, 1);
        x1 = Math.Min(x1, grid.Nx - 1);
        y1 = Math.Min(y1, grid.Ny - 1);
        z1 = Math.Min(z1, grid.Nz - 6);

        for (var y = y0; y <= y1; y += 2)
        {
            for (var x = x0; x <= x1; x += 2)
            {
                for (var z = z0; z <= z1; z++)
                {
                    if (!grid.IsSolid(grid.Index(x, y, z - 1)) || grid.IsSolid(grid.Index(x, y, z)))
                    {
                        continue;
                    }
                    var headroom = true;
                    for (var h = 1; h <= 5; h++)
                    {
                        if (grid.IsSolid(grid.Index(x, y, z + h)))
                        {
                            headroom = false;
                            break;
                        }
                    }
                    if (!headroom)
                    {
                        continue;
                    }
                    var center = grid.CellCenter(x, y, z);
                    origins.Add(OnSurface(grid, collider, new Vector3(center.X, center.Y, center.Z - grid.VoxelSize / 2)));
                    z += 4;
                }
            }
        }
        return origins;
    }
}
