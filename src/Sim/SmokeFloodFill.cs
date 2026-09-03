using System.Numerics;

namespace SmokeSolver.Sim;

public sealed class SmokeVolume
{
    public required VoxelGrid Grid { get; init; }
    public required Vector3 RestPoint { get; init; }
    public required int[] Cells { get; init; }
    public required HashSet<int> CellSet { get; init; }

    /// <summary>An empty volume, used to probe pure geometry occlusion.</summary>
    public static SmokeVolume CreateEmpty(VoxelGrid grid) => new()
    {
        Grid = grid,
        RestPoint = default,
        Cells = [],
        CellSet = [],
    };

    public (Vector3 Min, Vector3 Max) ComputeBounds()
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var half = new Vector3(Grid.VoxelSize / 2);
        foreach (var cell in Cells)
        {
            var center = Grid.CellCenter(cell);
            min = Vector3.Min(min, center - half);
            max = Vector3.Max(max, center + half);
        }
        return (min, max);
    }
}

/// <summary>
/// CS2-style smoke expansion: breadth-first fill from the grenade rest cell into
/// non-solid neighbors, bounded by a radius and a total cell budget.
/// </summary>
public static class SmokeFloodFill
{
    public static SmokeVolume Fill(VoxelGrid grid, Vector3 restPoint, SmokeParams p)
    {
        var (sx, sy, sz) = grid.CellOf(restPoint);
        if (!grid.InBounds(sx, sy, sz))
        {
            throw new ArgumentOutOfRangeException(nameof(restPoint), $"rest point {restPoint} is outside the voxel grid");
        }

        // A grenade rests on the ground, so its cell can voxelize as solid; start from
        // the first free cell above, like the real bloom emerging from the ground.
        var liftLimit = Math.Min(sz + 4, grid.Nz - 1);
        while (sz <= liftLimit && grid.IsSolid(grid.Index(sx, sy, sz)))
        {
            sz++;
        }
        var startIndex = grid.Index(sx, sy, sz);
        if (grid.IsSolid(startIndex))
        {
            return Empty(grid, restPoint);
        }

        var startCenter = grid.CellCenter(sx, sy, sz);
        var reach = p.MaxRadius * p.ContainedStretch;
        var maxRadiusSq = reach * reach;
        // The amount of gas: what the radius holds in the open. In the open
        // the fill claims exactly the cells inside the radius, as it always
        // did. Boxed in, the cells the walls took away are spent further along
        // (up to the stretched reach) instead of being lost - a smoke in mid
        // doors goes further and higher than the same smoke in the open.
        var budget = Math.Min(p.CellBudget, OpenAirCells(grid.VoxelSize, p.MaxRadius));
        var visited = new HashSet<int> { startIndex };
        var cells = new List<int> { startIndex };
        // Nearest-first through free space rather than plain breadth-first: a
        // queue grows a Manhattan octahedron and only the radius cap made it
        // round, which is also what stopped it dead in a corridor. Ordered by
        // straight-line distance the fill is the sphere in the open on its
        // own, and in a confined space keeps going until the gas runs out.
        var frontier = new PriorityQueue<int, float>();
        frontier.Enqueue(startIndex, 0f);
        Span<(int dx, int dy, int dz)> neighbors = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];
        while (frontier.Count > 0 && cells.Count < budget)
        {
            var current = frontier.Dequeue();
            var (cx, cy, cz) = grid.Coords(current);
            foreach (var (dx, dy, dz) in neighbors)
            {
                if (cells.Count >= budget)
                {
                    break;
                }
                int nx = cx + dx, ny = cy + dy, nz = cz + dz;
                if (!grid.InBounds(nx, ny, nz))
                {
                    continue;
                }
                var neighborIndex = grid.Index(nx, ny, nz);
                if (visited.Contains(neighborIndex) || grid.IsSolid(neighborIndex))
                {
                    continue;
                }
                var offset = grid.CellCenter(nx, ny, nz) - startCenter;
                if (offset.LengthSquared() > maxRadiusSq)
                {
                    continue;
                }
                visited.Add(neighborIndex);
                cells.Add(neighborIndex);
                frontier.Enqueue(neighborIndex, SettlingDistance(offset));
            }
        }

        return new SmokeVolume
        {
            Grid = grid,
            RestPoint = restPoint,
            Cells = [.. cells],
            // visited already contains exactly the flooded cells; rebuilding a
            // second HashSet per fill doubled the allocation cost of stage 1.
            CellSet = visited,
        };
    }

    // Straight-line distance from the landing, with the drop below it
    // foreshortened: a cell under the smoke counts as nearer than one the
    // same distance beside it, so the fill spills down before it spreads.
    static float SettlingDistance(Vector3 offset)
    {
        var dz = offset.Z < 0 ? offset.Z * SmokeParams.DownwardPull : offset.Z;
        return MathF.Sqrt(offset.X * offset.X + offset.Y * offset.Y + dz * dz);
    }

    // How many cells a landing on flat open ground claims inside the radius:
    // the lattice points of the half-ball above the ground layer. Cached per
    // (voxel, radius) since every fill of a map asks the same question.
    static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, float), int> OpenAirCache = new();

    static int OpenAirCells(float voxel, float radius) => OpenAirCache.GetOrAdd((voxel, radius), key =>
    {
        var (v, r) = key;
        var n = (int)MathF.Ceiling(r / v);
        var count = 0;
        for (var dz = 0; dz <= n; dz++)
        {
            for (var dy = -n; dy <= n; dy++)
            {
                for (var dx = -n; dx <= n; dx++)
                {
                    if ((dx * dx + dy * dy + dz * dz) * v * v <= r * r)
                    {
                        count++;
                    }
                }
            }
        }
        return count;
    });

    static SmokeVolume Empty(VoxelGrid grid, Vector3 restPoint) => new()
    {
        Grid = grid,
        RestPoint = restPoint,
        Cells = [],
        CellSet = [],
    };
}
