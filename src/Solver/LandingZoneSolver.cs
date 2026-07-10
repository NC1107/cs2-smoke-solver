using System.Collections.Concurrent;
using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// A target sightline. Eye jitter samples parallel rays around both endpoints so a
/// zone cell must cover the whole lane, not just the exact center ray.
/// </summary>
public sealed record SightlineSpec(Vector3 EyeA, Vector3 EyeB, float EyeJitter = 16f)
{
    public List<(Vector3 A, Vector3 B)> BuildEyePairs()
    {
        var direction = EyeB - EyeA;
        var horizontal = Vector3.Cross(direction, Vector3.UnitZ);
        if (horizontal.LengthSquared() < 1e-6f)
        {
            horizontal = Vector3.UnitX;
        }
        horizontal = Vector3.Normalize(horizontal);

        var pairs = new List<(Vector3, Vector3)>();
        Span<float> offsets = [-EyeJitter, 0f, EyeJitter];
        foreach (var offsetA in offsets)
        {
            foreach (var offsetB in offsets)
            {
                pairs.Add((EyeA + horizontal * offsetA, EyeB + horizontal * offsetB));
            }
        }
        return pairs;
    }
}

public sealed record LandingCell(Vector3 Center, int MinCrossings);

public sealed record SolveResult(List<LandingCell> Zone, int ClearPairs, int TotalPairs);

/// <summary>
/// Stage 1 of the inverse solver (DESIGN.md): the set of grenade landing cells whose
/// smoke seals every given sightline at once (a choke is sealed when all clear rays
/// through it are blocked). Stage 2 matches throws against this zone.
/// </summary>
public static class LandingZoneSolver
{
    public static SolveResult Solve(
        VoxelGrid grid,
        TriangleRaycaster raycaster,
        IReadOnlyList<SightlineSpec> sightlines,
        SmokeParams p,
        int minSmokeCells = 3)
    {
        // A ray the geometry already blocks is not a sightline; smoke must not get
        // credit for it. Clearance uses exact triangles: sub-voxel openings (the mid-doors
        // gap) are real sightlines that the voxel grid would falsely seal.
        var allPairs = sightlines.SelectMany(s => s.BuildEyePairs()).ToList();
        var pairs = allPairs
            .Where(pair => !raycaster.Blocked(pair.A, pair.B))
            .ToList();
        if (pairs.Count == 0)
        {
            throw new InvalidOperationException("every jittered ray of every sightline is blocked by geometry; the sightline definitions are wrong");
        }
        var candidates = FindCandidateRestCells(grid, sightlines, p);

        var zone = new ConcurrentBag<LandingCell>();
        Parallel.ForEach(candidates, cell =>
        {
            var smoke = SmokeFloodFill.Fill(grid, grid.CellCenter(cell), p);
            if (smoke.Cells.Length == 0)
            {
                return;
            }
            var minCrossings = int.MaxValue;
            foreach (var (a, b) in pairs)
            {
                var result = Occlusion.Test(smoke, a, b);
                minCrossings = Math.Min(minCrossings, result.SmokeCellsCrossed);
                if (!result.SmokeBlocked(minSmokeCells))
                {
                    return;
                }
            }
            zone.Add(new LandingCell(grid.CellCenter(cell), minCrossings));
        });

        return new SolveResult([.. zone.OrderByDescending(c => c.MinCrossings)], pairs.Count, allPairs.Count);
    }

    /// <summary>
    /// A grenade can only rest on top of solid geometry, and its smoke can only reach
    /// a sightline if the rest cell is within the fill radius of one of the segments.
    /// </summary>
    static List<int> FindCandidateRestCells(VoxelGrid grid, IReadOnlyList<SightlineSpec> sightlines, SmokeParams p)
    {
        var halfDiagonal = grid.VoxelSize * 0.87f;
        var maxDistance = p.MaxRadius + halfDiagonal;
        var candidates = new List<int>();
        for (var z = 1; z < grid.Nz; z++)
        {
            for (var y = 0; y < grid.Ny; y++)
            {
                for (var x = 0; x < grid.Nx; x++)
                {
                    var index = grid.Index(x, y, z);
                    if (grid.IsSolid(index) || !grid.IsSolid(grid.Index(x, y, z - 1)))
                    {
                        continue;
                    }
                    var center = grid.CellCenter(x, y, z);
                    foreach (var s in sightlines)
                    {
                        if (DistanceToSegment(center, s.EyeA, s.EyeB) <= maxDistance)
                        {
                            candidates.Add(index);
                            break;
                        }
                    }
                }
            }
        }
        return candidates;
    }

    static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var t = Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0f, 1f);
        return Vector3.Distance(p, a + ab * t);
    }
}
