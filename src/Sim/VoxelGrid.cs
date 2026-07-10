using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// Solid-occupancy voxel grid built from collision triangles.
/// The origin is snapped to multiples of the voxel size so cell boundaries are stable
/// across runs and across meshes of the same map.
/// </summary>
public sealed class VoxelGrid
{
    public float VoxelSize { get; }
    public Vector3 Origin { get; }
    public int Nx { get; }
    public int Ny { get; }
    public int Nz { get; }

    readonly ulong[] _solid;

    VoxelGrid(float voxelSize, Vector3 origin, int nx, int ny, int nz)
    {
        VoxelSize = voxelSize;
        Origin = origin;
        Nx = nx;
        Ny = ny;
        Nz = nz;
        _solid = new ulong[(CellCount + 63) / 64];
    }

    public long CellCount => (long)Nx * Ny * Nz;

    public long SolidCount
    {
        get
        {
            long count = 0;
            foreach (var word in _solid)
            {
                count += (long)ulong.PopCount(word);
            }
            return count;
        }
    }

    public int Index(int x, int y, int z) => (z * Ny + y) * Nx + x;

    public (int X, int Y, int Z) Coords(int index)
    {
        var x = index % Nx;
        var rest = index / Nx;
        return (x, rest % Ny, rest / Ny);
    }

    public bool InBounds(int x, int y, int z) => x >= 0 && x < Nx && y >= 0 && y < Ny && z >= 0 && z < Nz;

    public bool IsSolid(int index) => (_solid[index >> 6] & (1UL << (index & 63))) != 0;

    void SetSolid(int index) => _solid[index >> 6] |= 1UL << (index & 63);

    public (int X, int Y, int Z) CellOf(Vector3 p) => (
        (int)MathF.Floor((p.X - Origin.X) / VoxelSize),
        (int)MathF.Floor((p.Y - Origin.Y) / VoxelSize),
        (int)MathF.Floor((p.Z - Origin.Z) / VoxelSize));

    public Vector3 CellCenter(int x, int y, int z) =>
        Origin + new Vector3((x + 0.5f) * VoxelSize, (y + 0.5f) * VoxelSize, (z + 0.5f) * VoxelSize);

    public Vector3 CellCenter(int index)
    {
        var (x, y, z) = Coords(index);
        return CellCenter(x, y, z);
    }

    public static VoxelGrid Build(CollisionMesh mesh, float voxelSize, Func<byte, bool>? attributeFilter = null)
    {
        var (min, max) = mesh.ComputeBounds();
        return Build(mesh, voxelSize, min, max, attributeFilter);
    }

    public static VoxelGrid Build(
        CollisionMesh mesh,
        float voxelSize,
        Vector3 boundsMin,
        Vector3 boundsMax,
        Func<byte, bool>? attributeFilter = null)
    {
        // One cell of padding so geometry exactly on the boundary still voxelizes.
        var origin = new Vector3(
            (MathF.Floor(boundsMin.X / voxelSize) - 1) * voxelSize,
            (MathF.Floor(boundsMin.Y / voxelSize) - 1) * voxelSize,
            (MathF.Floor(boundsMin.Z / voxelSize) - 1) * voxelSize);
        var nx = (int)MathF.Ceiling((boundsMax.X - origin.X) / voxelSize) + 1;
        var ny = (int)MathF.Ceiling((boundsMax.Y - origin.Y) / voxelSize) + 1;
        var nz = (int)MathF.Ceiling((boundsMax.Z - origin.Z) / voxelSize) + 1;
        var grid = new VoxelGrid(voxelSize, origin, nx, ny, nz);

        var verts = mesh.Vertices;
        var indices = mesh.Indices;
        var halfSize = new Vector3(voxelSize / 2);
        for (var t = 0; t < indices.Length; t += 3)
        {
            if (attributeFilter != null && !attributeFilter(mesh.TriangleAttributes[t / 3]))
            {
                continue;
            }
            var v0 = new Vector3(verts[indices[t] * 3], verts[indices[t] * 3 + 1], verts[indices[t] * 3 + 2]);
            var v1 = new Vector3(verts[indices[t + 1] * 3], verts[indices[t + 1] * 3 + 1], verts[indices[t + 1] * 3 + 2]);
            var v2 = new Vector3(verts[indices[t + 2] * 3], verts[indices[t + 2] * 3 + 1], verts[indices[t + 2] * 3 + 2]);

            var triMin = Vector3.Min(v0, Vector3.Min(v1, v2));
            var triMax = Vector3.Max(v0, Vector3.Max(v1, v2));
            var (cx0, cy0, cz0) = grid.CellOf(triMin);
            var (cx1, cy1, cz1) = grid.CellOf(triMax);
            cx0 = Math.Max(cx0, 0);
            cy0 = Math.Max(cy0, 0);
            cz0 = Math.Max(cz0, 0);
            cx1 = Math.Min(cx1, nx - 1);
            cy1 = Math.Min(cy1, ny - 1);
            cz1 = Math.Min(cz1, nz - 1);

            for (var z = cz0; z <= cz1; z++)
            {
                for (var y = cy0; y <= cy1; y++)
                {
                    for (var x = cx0; x <= cx1; x++)
                    {
                        var index = grid.Index(x, y, z);
                        if (grid.IsSolid(index))
                        {
                            continue;
                        }
                        var center = grid.CellCenter(x, y, z);
                        if (TriBoxOverlap.Test(center, halfSize, v0, v1, v2))
                        {
                            grid.SetSolid(index);
                        }
                    }
                }
            }
        }
        return grid;
    }
}
