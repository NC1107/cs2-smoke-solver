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
        var maxRadiusSq = p.MaxRadius * p.MaxRadius;
        var visited = new HashSet<int> { startIndex };
        var cells = new List<int> { startIndex };
        var queue = new Queue<int>();
        queue.Enqueue(startIndex);

        Span<(int dx, int dy, int dz)> neighbors = [(1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1)];

        while (queue.Count > 0 && cells.Count < p.CellBudget)
        {
            var current = queue.Dequeue();
            var (cx, cy, cz) = grid.Coords(current);
            foreach (var (dx, dy, dz) in neighbors)
            {
                if (cells.Count >= p.CellBudget)
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
                if (Vector3.DistanceSquared(grid.CellCenter(nx, ny, nz), startCenter) > maxRadiusSq)
                {
                    continue;
                }
                visited.Add(neighborIndex);
                cells.Add(neighborIndex);
                queue.Enqueue(neighborIndex);
            }
        }

        return new SmokeVolume
        {
            Grid = grid,
            RestPoint = restPoint,
            Cells = [.. cells],
            CellSet = [.. cells],
        };
    }

    static SmokeVolume Empty(VoxelGrid grid, Vector3 restPoint) => new()
    {
        Grid = grid,
        RestPoint = restPoint,
        Cells = [],
        CellSet = [],
    };
}
