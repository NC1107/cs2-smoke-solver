using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// Closest-hit segment queries against the collision mesh, accelerated by a uniform
/// grid over triangles. Used to re-verify lineups with true surface normals: voxel
/// collision cannot deflect off slanted geometry (an angled ledge bounces sideways
/// in game but axis-aligned in the voxel model).
/// </summary>
public sealed partial class TriangleCollider
{
    readonly float[] _vertices;
    readonly int[] _indices;
    // CSR layout: triangles of cell i live in _cellTris[_cellStart[i].._cellStart[i+1]].
    // Flat arrays keep the innermost query loop on contiguous memory; per-cell
    // List<int> objects scattered indices across the heap.
    readonly int[] _cellStart;
    readonly int[] _cellTris;
    readonly Vector3 _origin;
    readonly float _cellSize;
    readonly int _nx;
    readonly int _ny;
    readonly int _nz;

    public TriangleCollider(CollisionMesh mesh, Vector3 regionMin, Vector3 regionMax, Func<byte, bool>? attributeFilter = null, float cellSize = 128f)
    {
        _vertices = mesh.Vertices;
        _indices = mesh.Indices;
        _cellSize = cellSize;
        _origin = regionMin;
        _nx = Math.Max(1, (int)MathF.Ceiling((regionMax.X - regionMin.X) / cellSize));
        _ny = Math.Max(1, (int)MathF.Ceiling((regionMax.Y - regionMin.Y) / cellSize));
        _nz = Math.Max(1, (int)MathF.Ceiling((regionMax.Z - regionMin.Z) / cellSize));
        var cellCount = _nx * _ny * _nz;
        _cellStart = new int[cellCount + 1];

        void ForEachCoveredCell(int t, Action<int> visit)
        {
            var (a, b, c) = (Vertex(_indices[t]), Vertex(_indices[t + 1]), Vertex(_indices[t + 2]));
            var triMin = Vector3.Min(a, Vector3.Min(b, c));
            var triMax = Vector3.Max(a, Vector3.Max(b, c));
            if (triMax.X < regionMin.X || triMin.X > regionMax.X ||
                triMax.Y < regionMin.Y || triMin.Y > regionMax.Y ||
                triMax.Z < regionMin.Z || triMin.Z > regionMax.Z)
            {
                return;
            }
            var (x0, y0, z0) = CellOf(triMin);
            var (x1, y1, z1) = CellOf(triMax);
            for (var z = Math.Max(z0, 0); z <= Math.Min(z1, _nz - 1); z++)
            {
                for (var y = Math.Max(y0, 0); y <= Math.Min(y1, _ny - 1); y++)
                {
                    for (var x = Math.Max(x0, 0); x <= Math.Min(x1, _nx - 1); x++)
                    {
                        visit((z * _ny + y) * _nx + x);
                    }
                }
            }
        }

        for (var t = 0; t < _indices.Length; t += 3)
        {
            if (attributeFilter != null && !attributeFilter(mesh.TriangleAttributes[t / 3]))
            {
                continue;
            }
            ForEachCoveredCell(t, index => _cellStart[index + 1]++);
        }
        for (var i = 0; i < cellCount; i++)
        {
            _cellStart[i + 1] += _cellStart[i];
        }
        _cellTris = new int[_cellStart[cellCount]];
        var fill = new int[cellCount];
        for (var t = 0; t < _indices.Length; t += 3)
        {
            if (attributeFilter != null && !attributeFilter(mesh.TriangleAttributes[t / 3]))
            {
                continue;
            }
            ForEachCoveredCell(t, index => _cellTris[_cellStart[index] + fill[index]++] = t);
        }
    }

    (int X, int Y, int Z) CellOf(Vector3 p) => (
        (int)MathF.Floor((p.X - _origin.X) / _cellSize),
        (int)MathF.Floor((p.Y - _origin.Y) / _cellSize),
        (int)MathF.Floor((p.Z - _origin.Z) / _cellSize));

    Vector3 Vertex(int index) => new(_vertices[index * 3], _vertices[index * 3 + 1], _vertices[index * 3 + 2]);

}
