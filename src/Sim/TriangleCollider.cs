using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// Closest-hit segment queries against the collision mesh, accelerated by a uniform
/// grid over triangles. Used to re-verify lineups with true surface normals: voxel
/// collision cannot deflect off slanted geometry (an angled ledge bounces sideways
/// in game but axis-aligned in the voxel model).
/// </summary>
public sealed class TriangleCollider
{
    readonly float[] _vertices;
    readonly int[] _indices;
    readonly List<int>[] _cells;
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
        _cells = new List<int>[(long)_nx * _ny * _nz];

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
            var (x0, y0, z0) = CellOf(triMin);
            var (x1, y1, z1) = CellOf(triMax);
            for (var z = Math.Max(z0, 0); z <= Math.Min(z1, _nz - 1); z++)
            {
                for (var y = Math.Max(y0, 0); y <= Math.Min(y1, _ny - 1); y++)
                {
                    for (var x = Math.Max(x0, 0); x <= Math.Min(x1, _nx - 1); x++)
                    {
                        var index = (z * _ny + y) * _nx + x;
                        (_cells[index] ??= []).Add(t);
                    }
                }
            }
        }
    }

    public (float T, Vector3 Normal)? FirstHit(Vector3 from, Vector3 to) => FirstHit(from, to, 0f);

    /// <summary>
    /// Approximate sphere cast: the center ray plus four rays offset perpendicular
    /// to the motion. A grenade is a sphere, not a point; a point trace snags on
    /// thin trim and threads gaps a real grenade cannot.
    /// </summary>
    public (float T, Vector3 Normal)? FirstHit(Vector3 from, Vector3 to, float radius)
    {
        if (radius <= 0f)
        {
            return RayHit(from, to);
        }
        var direction = Vector3.Normalize(to - from);
        var u = Vector3.Cross(direction, Vector3.UnitZ);
        if (u.LengthSquared() < 1e-6f)
        {
            u = Vector3.Cross(direction, Vector3.UnitX);
        }
        u = Vector3.Normalize(u);
        var v = Vector3.Normalize(Vector3.Cross(direction, u));

        (float T, Vector3 Normal)? best = null;
        Span<Vector3> offsets = [Vector3.Zero, u * radius, -u * radius, v * radius, -v * radius];
        foreach (var offset in offsets)
        {
            if (RayHit(from + offset, to + offset) is { } hit && (best == null || hit.T < best.Value.T))
            {
                best = hit;
            }
        }
        return best;
    }

    /// <summary>
    /// Exact swept-AABB query, matching the engine's box hull trace (grenades
    /// sweep a +-2 unit box, whose corners catch surface edges a sphere of the
    /// same radius misses). Separating-axis sweep per candidate triangle: 3 box
    /// axes, the triangle normal, and the 9 edge cross axes; each axis yields a
    /// linear-in-t projection overlap interval and the TOI is the latest entry.
    /// </summary>
    public (float T, Vector3 Normal)? FirstHitHull(Vector3 from, Vector3 to, Vector3 halfExtents, float minNormalZ = -2f)
    {
        var direction = to - from;
        var bestT = float.MaxValue;
        var bestNormal = Vector3.Zero;

        var lo = Vector3.Min(from, to) - halfExtents;
        var hi = Vector3.Max(from, to) + halfExtents;
        var (x0, y0, z0) = CellOf(lo);
        var (x1, y1, z1) = CellOf(hi);
        for (var z = Math.Max(z0, 0); z <= Math.Min(z1, _nz - 1); z++)
        {
            for (var y = Math.Max(y0, 0); y <= Math.Min(y1, _ny - 1); y++)
            {
                for (var x = Math.Max(x0, 0); x <= Math.Min(x1, _nx - 1); x++)
                {
                    var bucket = _cells[(z * _ny + y) * _nx + x];
                    if (bucket == null)
                    {
                        continue;
                    }
                    foreach (var t in bucket)
                    {
                        if (SweptBoxTriangle(from, direction, halfExtents, t) is { } hit
                            && hit.T < bestT
                            && hit.Normal.Z >= minNormalZ)
                        {
                            (bestT, bestNormal) = hit;
                        }
                    }
                }
            }
        }
        return bestT <= 1f ? (bestT, bestNormal) : null;
    }

    /// <summary>
    /// Static box-vs-mesh intersection: does any triangle touch the axis-aligned
    /// box? Orientation-free, unlike rays (a vertical ray is parallel to every
    /// vertical wall and can never hit one) - used by the radar renderer to ask
    /// "is there solid inside this z-window at this pixel".
    /// </summary>
    public bool BoxIntersects(Vector3 center, Vector3 halfExtents)
    {
        var (x0, y0, z0) = CellOf(center - halfExtents);
        var (x1, y1, z1) = CellOf(center + halfExtents);
        for (var z = Math.Max(z0, 0); z <= Math.Min(z1, _nz - 1); z++)
        {
            for (var y = Math.Max(y0, 0); y <= Math.Min(y1, _ny - 1); y++)
            {
                for (var x = Math.Max(x0, 0); x <= Math.Min(x1, _nx - 1); x++)
                {
                    var bucket = _cells[(z * _ny + y) * _nx + x];
                    if (bucket == null)
                    {
                        continue;
                    }
                    foreach (var t in bucket)
                    {
                        if (BoxTriangleOverlap(center, halfExtents, t))
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }

    bool BoxTriangleOverlap(Vector3 center, Vector3 h, int triangleOffset)
    {
        var a = Vertex(_indices[triangleOffset]) - center;
        var b = Vertex(_indices[triangleOffset + 1]) - center;
        var c = Vertex(_indices[triangleOffset + 2]) - center;

        bool Separated(Vector3 l)
        {
            if (l.LengthSquared() < 1e-10f)
            {
                return false;
            }
            var s0 = Vector3.Dot(a, l);
            var s1 = Vector3.Dot(b, l);
            var s2 = Vector3.Dot(c, l);
            var r = h.X * MathF.Abs(l.X) + h.Y * MathF.Abs(l.Y) + h.Z * MathF.Abs(l.Z);
            return MathF.Min(s0, MathF.Min(s1, s2)) > r || MathF.Max(s0, MathF.Max(s1, s2)) < -r;
        }

        if (Separated(Vector3.UnitX) || Separated(Vector3.UnitY) || Separated(Vector3.UnitZ))
        {
            return false;
        }
        var n = Vector3.Cross(b - a, c - a);
        if (Separated(n))
        {
            return false;
        }
        Span<Vector3> edges = [b - a, c - b, a - c];
        foreach (var e in edges)
        {
            if (Separated(Vector3.Cross(Vector3.UnitX, e)) ||
                Separated(Vector3.Cross(Vector3.UnitY, e)) ||
                Separated(Vector3.Cross(Vector3.UnitZ, e)))
            {
                return false;
            }
        }
        return true;
    }

    (float T, Vector3 Normal)? SweptBoxTriangle(Vector3 origin, Vector3 direction, Vector3 h, int triangleOffset)
    {
        var a = Vertex(_indices[triangleOffset]) - origin;
        var b = Vertex(_indices[triangleOffset + 1]) - origin;
        var c = Vertex(_indices[triangleOffset + 2]) - origin;

        var triNormal = Vector3.Cross(b - a, c - a);
        if (triNormal.LengthSquared() < 1e-12f)
        {
            return null;
        }

        var tEnter = 0f;
        var tExit = 1f;
        var enterAxis = triNormal;

        bool Axis(Vector3 l)
        {
            if (l.LengthSquared() < 1e-10f)
            {
                return true; // degenerate axis, no separation information
            }
            var s0 = Vector3.Dot(a, l);
            var s1 = Vector3.Dot(b, l);
            var s2 = Vector3.Dot(c, l);
            var m = MathF.Min(s0, MathF.Min(s1, s2));
            var M = MathF.Max(s0, MathF.Max(s1, s2));
            var r = h.X * MathF.Abs(l.X) + h.Y * MathF.Abs(l.Y) + h.Z * MathF.Abs(l.Z);
            var w = Vector3.Dot(direction, l);
            if (MathF.Abs(w) < 1e-9f)
            {
                return m <= r && M >= -r;
            }
            // overlap while m - t*w <= r and M - t*w >= -r
            var tA = (m - r) / w;
            var tB = (M + r) / w;
            var (axLo, axHi) = w > 0 ? (tA, tB) : (tB, tA);
            if (axLo > tEnter)
            {
                tEnter = axLo;
                enterAxis = l;
            }
            tExit = MathF.Min(tExit, axHi);
            return tEnter <= tExit;
        }

        if (!Axis(Vector3.UnitX) || !Axis(Vector3.UnitY) || !Axis(Vector3.UnitZ) || !Axis(triNormal))
        {
            return null;
        }
        Span<Vector3> edges = [b - a, c - b, a - c];
        foreach (var e in edges)
        {
            if (!Axis(Vector3.Cross(Vector3.UnitX, e)) ||
                !Axis(Vector3.Cross(Vector3.UnitY, e)) ||
                !Axis(Vector3.Cross(Vector3.UnitZ, e)))
            {
                return null;
            }
        }

        if (tEnter <= 1e-6f)
        {
            // Overlapping at the start of the step (the post-bounce backoff can
            // leave the hull touching the surface it just left). Report a
            // contact only when moving deeper into the plane from the side the
            // sweep starts on; flipping the normal against the motion here
            // would ghost-collide the BACKFACE of the surface just bounced off,
            // wedging the hull in place (observed as hundreds of alternating
            // n/-n bounces on sloped geometry).
            var n0 = Vector3.Normalize(triNormal);
            // Triangle vertices are relative to the sweep start, so the start
            // center's signed distance to the plane is -dot(a, n).
            if (Vector3.Dot(a, n0) > 0)
            {
                n0 = -n0;
            }
            // Require a real incidence angle, not a parallel graze: a hull
            // sliding down flush against a wall must fall freely, not bounce
            // off the wall every tick (which deadlocked the rest detection).
            return Vector3.Dot(Vector3.Normalize(direction), n0) < -0.01f ? (0f, n0) : null;
        }

        // Contact normal from the axis that produced the entry time. Entering
        // through the face plane gives the face normal; entering laterally
        // (sliding inside the plane slab past a triangle edge) gives the edge
        // or box axis instead - reporting the face normal there deflected the
        // hull back into thin-ridge pockets it was actually sliding out of.
        var normal = Vector3.Normalize(enterAxis);
        if (Vector3.Dot(normal, direction) > 0)
        {
            normal = -normal;
        }
        return (tEnter, normal);
    }

    (float T, Vector3 Normal)? RayHit(Vector3 from, Vector3 to)
    {
        var direction = to - from;
        var bestT = float.MaxValue;
        var bestNormal = Vector3.Zero;

        // Walk the segment's cells; a short integration step spans at most a few.
        var (x0, y0, z0) = CellOf(from);
        var (x1, y1, z1) = CellOf(to);
        for (var z = Math.Max(Math.Min(z0, z1), 0); z <= Math.Min(Math.Max(z0, z1), _nz - 1); z++)
        {
            for (var y = Math.Max(Math.Min(y0, y1), 0); y <= Math.Min(Math.Max(y0, y1), _ny - 1); y++)
            {
                for (var x = Math.Max(Math.Min(x0, x1), 0); x <= Math.Min(Math.Max(x0, x1), _nx - 1); x++)
                {
                    var bucket = _cells[(z * _ny + y) * _nx + x];
                    if (bucket == null)
                    {
                        continue;
                    }
                    foreach (var t in bucket)
                    {
                        if (HitTriangle(from, direction, t) is { } hit && hit.T < bestT)
                        {
                            (bestT, bestNormal) = hit;
                        }
                    }
                }
            }
        }
        return bestT <= 1f ? (bestT, bestNormal) : null;
    }

    (int X, int Y, int Z) CellOf(Vector3 p) => (
        (int)MathF.Floor((p.X - _origin.X) / _cellSize),
        (int)MathF.Floor((p.Y - _origin.Y) / _cellSize),
        (int)MathF.Floor((p.Z - _origin.Z) / _cellSize));

    Vector3 Vertex(int index) => new(_vertices[index * 3], _vertices[index * 3 + 1], _vertices[index * 3 + 2]);

    (float T, Vector3 Normal)? HitTriangle(Vector3 origin, Vector3 direction, int triangleOffset)
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
            return null;
        }
        var invDet = 1f / det;
        var s = origin - a;
        var u = invDet * Vector3.Dot(s, h);
        if (u is < 0f or > 1f)
        {
            return null;
        }
        var q = Vector3.Cross(s, edge1);
        var v = invDet * Vector3.Dot(direction, q);
        if (v < 0f || u + v > 1f)
        {
            return null;
        }
        var t = invDet * Vector3.Dot(edge2, q);
        if (t is <= 1e-5f or > 1f)
        {
            return null;
        }
        var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
        if (Vector3.Dot(normal, direction) > 0)
        {
            normal = -normal;
        }
        return (t, normal);
    }
}
