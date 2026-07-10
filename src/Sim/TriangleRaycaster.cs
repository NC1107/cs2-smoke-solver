using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// Exact segment-vs-triangle occlusion against the collision mesh (Möller-Trumbore).
/// Sightline clearance must not be quantized to voxels: sub-voxel openings like the
/// Dust 2 mid-doors gap decide real sightlines. The voxel grid is only for smoke volume.
/// </summary>
public sealed class TriangleRaycaster
{
    readonly float[] _vertices;
    readonly int[] _indices;
    readonly List<int> _triangleOffsets;

    public TriangleRaycaster(CollisionMesh mesh, Vector3 regionMin, Vector3 regionMax, Func<byte, bool>? attributeFilter = null)
    {
        _vertices = mesh.Vertices;
        _indices = mesh.Indices;
        _triangleOffsets = [];
        for (var t = 0; t < _indices.Length; t += 3)
        {
            if (attributeFilter != null && !attributeFilter(mesh.TriangleAttributes[t / 3]))
            {
                continue;
            }
            var (a, b, c) = (Vertex(_indices[t]), Vertex(_indices[t + 1]), Vertex(_indices[t + 2]));
            var triMin = Vector3.Min(a, Vector3.Min(b, c));
            var triMax = Vector3.Max(a, Vector3.Max(b, c));
            if (triMax.X < regionMin.X || triMin.X > regionMax.X ||
                triMax.Y < regionMin.Y || triMin.Y > regionMax.Y ||
                triMax.Z < regionMin.Z || triMin.Z > regionMax.Z)
            {
                continue;
            }
            _triangleOffsets.Add(t);
        }
    }

    public int TriangleCount => _triangleOffsets.Count;

    public bool Blocked(Vector3 from, Vector3 to)
    {
        var direction = to - from;
        foreach (var t in _triangleOffsets)
        {
            if (SegmentHitsTriangle(from, direction, t))
            {
                return true;
            }
        }
        return false;
    }

    Vector3 Vertex(int index) => new(_vertices[index * 3], _vertices[index * 3 + 1], _vertices[index * 3 + 2]);

    bool SegmentHitsTriangle(Vector3 origin, Vector3 direction, int triangleOffset)
    {
        const float epsilon = 1e-7f;
        var a = Vertex(_indices[triangleOffset]);
        var b = Vertex(_indices[triangleOffset + 1]);
        var c = Vertex(_indices[triangleOffset + 2]);

        var edge1 = b - a;
        var edge2 = c - a;
        var h = Vector3.Cross(direction, edge2);
        var det = Vector3.Dot(edge1, h);
        if (MathF.Abs(det) < epsilon)
        {
            return false;
        }
        var invDet = 1f / det;
        var s = origin - a;
        var u = invDet * Vector3.Dot(s, h);
        if (u is < 0f or > 1f)
        {
            return false;
        }
        var q = Vector3.Cross(s, edge1);
        var v = invDet * Vector3.Dot(direction, q);
        if (v < 0f || u + v > 1f)
        {
            return false;
        }
        var t = invDet * Vector3.Dot(edge2, q);
        return t is > 1e-4f and < 1f - 1e-4f;
    }
}
