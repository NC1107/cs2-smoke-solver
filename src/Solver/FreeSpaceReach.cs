using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// How far the landing zone is from each part of the map THROUGH OPEN AIR,
/// measured by flooding the free voxels outward from the zone.
/// </summary>
// A grenade is an object, not a raycast: whatever it does - arc, bounce,
// roll - it has to travel through space that is not solid. So the shortest
// free-space path from a throw spot to the zone is a hard lower bound on the
// distance that grenade must cover, and a spot whose shortest path exceeds
// what any throw can travel cannot reach the zone by any angle, click or
// bounce count.
//
// Straight-line distance does not capture this at all. Lower tunnels sit a few
// hundred units under B site on de_dust2, and inside de_nuke a spot in lobby is
// a short line but a very long journey from heaven. Those origins passed every
// range check and then paid for a full set of simulations to discover the
// obvious. This is what makes "no chance from in here" cheap to know.
public static class FreeSpaceReach
{
    // Distances are kept in voxels in a byte array: 254 voxels at the usual 16u
    // cell is 4064u, past the longest throw in the game, and one byte over a
    // multi-million cell grid is the difference between a rounding error and a
    // real allocation.
    const byte Unreached = 255;

    public sealed class Field
    {
        readonly byte[] _steps;
        readonly VoxelGrid _grid;

        internal Field(VoxelGrid grid, byte[] steps)
        {
            _grid = grid;
            _steps = steps;
        }

        /// <summary>
        /// Shortest free-space distance from this point to the zone, or null
        /// when no open path within the search budget exists.
        /// </summary>
        public float? DistanceFrom(Vector3 point)
        {
            var (x, y, z) = _grid.CellOf(point);
            if (!_grid.InBounds(x, y, z))
            {
                return null;
            }
            var steps = _steps[_grid.Index(x, y, z)];
            return steps == Unreached ? null : steps * _grid.VoxelSize;
        }
    }

    /// <summary>
    /// Floods open cells outward from the zone, stopping past
    /// <paramref name="maxDistance"/>.
    /// </summary>
    // Six-connected on purpose: a diagonal step through the gap between two
    // solid cells would claim a path a grenade cannot take, and this is only
    // useful if it never rules out something possible. Six-connectivity also
    // makes every step exactly one voxel, so the queue stays a plain FIFO and
    // the distances need no priority ordering.
    public static Field Build(VoxelGrid grid, IEnumerable<int> zoneCells, float maxDistance)
    {
        var steps = new byte[grid.Nx * grid.Ny * grid.Nz];
        Array.Fill(steps, Unreached);
        var maxSteps = (int)MathF.Min(MathF.Ceiling(maxDistance / grid.VoxelSize), Unreached - 1);

        var queue = new Queue<int>();
        foreach (var cell in zoneCells)
        {
            // A zone cell can be solid (the zone is built around a point, not
            // carved out of the geometry); seeding from it anyway would leak
            // the flood inside walls.
            if (grid.IsSolid(cell) || steps[cell] == 0)
            {
                continue;
            }
            steps[cell] = 0;
            queue.Enqueue(cell);
        }

        var layerXY = grid.Nx * grid.Ny;
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            var next = (byte)(steps[cell] + 1);
            if (next > maxSteps)
            {
                continue;
            }
            var z = cell / layerXY;
            var rem = cell - z * layerXY;
            var y = rem / grid.Nx;
            var x = rem - y * grid.Nx;

            void Visit(int nx, int ny, int nz)
            {
                if (nx < 0 || ny < 0 || nz < 0 || nx >= grid.Nx || ny >= grid.Ny || nz >= grid.Nz)
                {
                    return;
                }
                var index = grid.Index(nx, ny, nz);
                if (steps[index] != Unreached || grid.IsSolid(index))
                {
                    return;
                }
                steps[index] = next;
                queue.Enqueue(index);
            }

            Visit(x - 1, y, z);
            Visit(x + 1, y, z);
            Visit(x, y - 1, z);
            Visit(x, y + 1, z);
            Visit(x, y, z - 1);
            Visit(x, y, z + 1);
        }
        return new Field(grid, steps);
    }
}
