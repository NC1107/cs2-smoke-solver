using System.Collections.Concurrent;
using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

public sealed record Lineup(
    Vector3 Feet,
    float YawDeg,
    float PitchDeg,
    ThrowType Type,
    Vector3 RestPoint,
    int Bounces,
    float FlightTime,
    int RestCrossings,
    float Stability = 0f,
    float Strength = 1f);

/// <summary>
/// Stage 2 of the inverse solver: sweep standable origins and view angles, keep
/// throws whose grenade comes to rest inside the stage 1 landing zone.
/// Fewer bounces rank higher: bounce-free lineups tolerate constant error best.
/// </summary>
public static class LineupSolver
{
    const float YawSpreadDeg = 30f;
    static readonly float[] Strengths = [1f, 0.5f, 0f];

    // Loose upper bounds used only to prune hopeless origins; a real measured
    // jumpthrow covers 2286u, so err generously.
    static float MaxRange(ThrowType type) => type switch
    {
        ThrowType.Stand or ThrowType.Crouch => 2000f,
        ThrowType.JumpThrow or ThrowType.CrouchJumpThrow => 2700f,
        _ => 3100f,
    };

    public static List<Lineup> Solve(
        VoxelGrid grid,
        IReadOnlyDictionary<int, int> zoneCrossings,
        Vector3 originMin,
        Vector3 originMax,
        IReadOnlyList<ThrowType> types,
        float yawStepDeg = 2f,
        float pitchStepDeg = 2f,
        IReadOnlyList<Vector3>? origins = null,
        ThrowConstants? constants = null,
        ConcurrentDictionary<(int X, int Y), int>? coverage = null)
    {
        if (zoneCrossings.Count == 0)
        {
            // A NaN centroid from the division below defeats every distance
            // prune (NaN comparisons are false), so the solver would simulate
            // every angle from every origin and still return nothing.
            return [];
        }
        var zoneCentroid = Vector3.Zero;
        foreach (var cell in zoneCrossings.Keys)
        {
            zoneCentroid += grid.CellCenter(cell);
        }
        zoneCentroid /= zoneCrossings.Count;

        origins ??= FindStandableOrigins(grid, originMin, originMax);
        var best = new ConcurrentDictionary<(int, int), Lineup>();

        Parallel.ForEach(origins, feet =>
        {
            var toZone = zoneCentroid - feet;
            var distance = new Vector2(toZone.X, toZone.Y).Length();
            var yawCenter = MathF.Atan2(toZone.Y, toZone.X) * 180f / MathF.PI;
            var hits = 0;

            foreach (var type in types)
            {
                var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
                foreach (var strength in Strengths)
                {
                    // Range scales with the square of throw speed.
                    var speedFactor = (constants ?? ThrowConstants.Default).SpeedScale(strength);
                    if (distance > MaxRange(type) * speedFactor * speedFactor)
                    {
                        continue;
                    }
                    for (var yaw = yawCenter - YawSpreadDeg; yaw <= yawCenter + YawSpreadDeg; yaw += yawStepDeg)
                    {
                        // Steeper than -65 degrees is an impractical sky-lob; players cannot
                        // line it up reliably and it telegraphs for seconds.
                        for (var pitch = -65f; pitch <= 0f; pitch += pitchStepDeg)
                        {
                            var result = GrenadeTrajectory.Simulate(grid, new ThrowSpec(eye, yaw, pitch, type, strength), constants);
                            if (result.Lost || result.FlightTime >= GrenadeTrajectory.MaxFlightSeconds - 0.01f)
                            {
                                continue;
                            }
                            var (cx, cy, cz) = grid.CellOf(result.RestPoint);
                            if (!grid.InBounds(cx, cy, cz) || !zoneCrossings.TryGetValue(grid.Index(cx, cy, cz), out var crossings))
                            {
                                continue;
                            }
                            hits++;
                            var lineup = new Lineup(feet, Normalize(yaw), pitch, type, result.RestPoint, result.Bounces, result.FlightTime, crossings, Strength: strength);
                            var key = ((int)MathF.Floor(feet.X / 64f), (int)MathF.Floor(feet.Y / 64f));
                            best.AddOrUpdate(key, lineup, (_, current) => Better(lineup, current) ? lineup : current);
                        }
                    }
                }
            }
            // Per-origin option count, including zeroes: the heat map view uses
            // "evaluated but impossible" cells to expose sim or geometry gaps.
            coverage?[((int)MathF.Round(feet.X), (int)MathF.Round(feet.Y))] = hits;
        });

        return [.. best.Values.OrderBy(l => l.Bounces).ThenByDescending(l => l.RestCrossings).ThenBy(l => l.FlightTime)];
    }

    /// <summary>
    /// Gates lineups on a stability score: the fraction of slightly perturbed
    /// sphere-cast exact simulations still resting in the zone. The sphere cast
    /// rolls over thin trim (unlike a point trace) and has no voxel inflation, the
    /// two failure modes real throws exposed, so it is trusted as the referee.
    /// </summary>
    public static List<Lineup> VerifyExact(
        VoxelGrid grid,
        TriangleCollider collider,
        IReadOnlyDictionary<int, int> zoneCrossings,
        IEnumerable<Lineup> candidates,
        float minStability = 0.4f,
        ThrowConstants? constants = null)
    {
        (float DYaw, float DPitch)[] offsets = [(0, 0), (-0.6f, 0), (0.6f, 0), (0, -0.6f), (0, 0.6f)];

        var verified = new ConcurrentBag<Lineup>();
        Parallel.ForEach(candidates, lineup =>
        {
            var eye = lineup.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(lineup.Type));
            var hits = 0;
            TrajectoryResult? baseResult = null;
            foreach (var (dYaw, dPitch) in offsets)
            {
                var result = GrenadeTrajectory.SimulateExact(
                    collider, new ThrowSpec(eye, lineup.YawDeg + dYaw, lineup.PitchDeg + dPitch, lineup.Type, lineup.Strength), constants);
                baseResult ??= result;
                if (result.Lost || result.FlightTime >= GrenadeTrajectory.MaxFlightSeconds - 0.01f)
                {
                    continue;
                }
                if (InZone(grid, zoneCrossings, result.RestPoint))
                {
                    hits++;
                }
            }
            var stability = (float)hits / offsets.Length;
            if (stability < minStability)
            {
                return;
            }
            var best = baseResult!.Value;
            verified.Add(lineup with
            {
                RestPoint = InZone(grid, zoneCrossings, best.RestPoint) ? best.RestPoint : lineup.RestPoint,
                Stability = stability,
            });
        });
        return
        [
            .. verified
                .OrderByDescending(l => l.Stability)
                .ThenBy(l => l.Bounces)
                .ThenByDescending(l => l.RestCrossings)
                .ThenBy(l => l.FlightTime),
        ];
    }

    static bool InZone(VoxelGrid grid, IReadOnlyDictionary<int, int> zoneCrossings, Vector3 restPoint)
    {
        var (x, y, z) = grid.CellOf(restPoint);
        for (var dz = 0; dz <= 1; dz++)
        {
            if (grid.InBounds(x, y, z + dz) && zoneCrossings.ContainsKey(grid.Index(x, y, z + dz)))
            {
                return true;
            }
        }
        return false;
    }

    static bool Better(Lineup a, Lineup b)
    {
        if (a.Bounces != b.Bounces)
        {
            return a.Bounces < b.Bounces;
        }
        if (a.RestCrossings != b.RestCrossings)
        {
            return a.RestCrossings > b.RestCrossings;
        }
        return a.FlightTime < b.FlightTime;
    }

    static float Normalize(float yaw)
    {
        while (yaw > 180f)
        {
            yaw -= 360f;
        }
        while (yaw < -180f)
        {
            yaw += 360f;
        }
        return yaw;
    }

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
        float sampleStep = 32f)
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
                    origins.Add(SnapToGround(grid, new Vector3(x, y, avgZ)));
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
                    origins.Add(SnapToGround(grid, new Vector3(cx, cy, avgZ)));
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

    static Vector3 SnapToGround(VoxelGrid grid, Vector3 p)
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
                return new Vector3(p.X, p.Y, center.Z - grid.VoxelSize / 2);
            }
        }
        return p;
    }

    /// <summary>
    /// Feet positions a player can stand at: a free cell over solid ground with
    /// head room, sampled every second column to keep the sweep tractable.
    /// </summary>
    static List<Vector3> FindStandableOrigins(VoxelGrid grid, Vector3 min, Vector3 max)
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
                    origins.Add(new Vector3(center.X, center.Y, center.Z - grid.VoxelSize / 2));
                    z += 4;
                }
            }
        }
        return origins;
    }
}
