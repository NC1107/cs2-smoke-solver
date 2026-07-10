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

        // Two passes build the CSR directly: count entries per cell, prefix-sum
        // into offsets, then place triangle ids.
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
                    var cell = (z * _ny + y) * _nx + x;
                    for (var i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                    {
                        if (SweptBoxTriangle(from, direction, halfExtents, _cellTris[i]) is { } hit
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
                    var cell = (z * _ny + y) * _nx + x;
                    for (var i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                    {
                        if (BoxTriangleOverlap(center, halfExtents, _cellTris[i]))
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

        var (x0, y0, z0) = CellOf(from);
        var (x1, y1, z1) = CellOf(to);
        var span = Math.Abs(x1 - x0) + Math.Abs(y1 - y0) + Math.Abs(z1 - z0);
        if (span <= 3)
        {
            // Per-tick integration steps cover at most a few cells; the tiny
            // AABB walk beats DDA setup cost there.
            for (var z = Math.Max(Math.Min(z0, z1), 0); z <= Math.Min(Math.Max(z0, z1), _nz - 1); z++)
            {
                for (var y = Math.Max(Math.Min(y0, y1), 0); y <= Math.Min(Math.Max(y0, y1), _ny - 1); y++)
                {
                    for (var x = Math.Max(Math.Min(x0, x1), 0); x <= Math.Min(Math.Max(x0, x1), _nx - 1); x++)
                    {
                        var cell = (z * _ny + y) * _nx + x;
                        for (var i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
                        {
                            if (HitTriangle(from, direction, _cellTris[i]) is { } hit && hit.T < bestT)
                            {
                                (bestT, bestNormal) = hit;
                            }
                        }
                    }
                }
            }
            return bestT <= 1f ? (bestT, bestNormal) : null;
        }

        // Long rays (aim references, radar probes): Amanatides-Woo DDA visits
        // only the cells on the line - O(n) instead of O(n^3) over the AABB -
        // and stops as soon as the best hit precedes the next cell boundary.
        var gridMin = _origin;
        var gridMax = _origin + new Vector3(_nx, _ny, _nz) * _cellSize;
        var tMin = 0f;
        var tMax = 1f;
        for (var axis = 0; axis < 3; axis++)
        {
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var o = axis == 0 ? from.X : axis == 1 ? from.Y : from.Z;
            var lo = axis == 0 ? gridMin.X : axis == 1 ? gridMin.Y : gridMin.Z;
            var hi = axis == 0 ? gridMax.X : axis == 1 ? gridMax.Y : gridMax.Z;
            if (MathF.Abs(d) < 1e-9f)
            {
                if (o < lo || o > hi)
                {
                    return null;
                }
                continue;
            }
            var t0 = (lo - o) / d;
            var t1 = (hi - o) / d;
            if (t0 > t1)
            {
                (t0, t1) = (t1, t0);
            }
            tMin = MathF.Max(tMin, t0);
            tMax = MathF.Min(tMax, t1);
            if (tMin > tMax)
            {
                return null;
            }
        }

        var start = from + direction * tMin;
        var cx = Math.Clamp((int)MathF.Floor((start.X - _origin.X) / _cellSize), 0, _nx - 1);
        var cy = Math.Clamp((int)MathF.Floor((start.Y - _origin.Y) / _cellSize), 0, _ny - 1);
        var cz = Math.Clamp((int)MathF.Floor((start.Z - _origin.Z) / _cellSize), 0, _nz - 1);
        var stepX = direction.X > 0 ? 1 : direction.X < 0 ? -1 : 0;
        var stepY = direction.Y > 0 ? 1 : direction.Y < 0 ? -1 : 0;
        var stepZ = direction.Z > 0 ? 1 : direction.Z < 0 ? -1 : 0;
        float NextBoundary(int cell, int step, float origin) =>
            _origin.X * 0 + origin + (cell + (step > 0 ? 1 : 0)) * _cellSize;
        var tDeltaX = stepX != 0 ? _cellSize / MathF.Abs(direction.X) : float.MaxValue;
        var tDeltaY = stepY != 0 ? _cellSize / MathF.Abs(direction.Y) : float.MaxValue;
        var tDeltaZ = stepZ != 0 ? _cellSize / MathF.Abs(direction.Z) : float.MaxValue;
        var tNextX = stepX != 0 ? (NextBoundary(cx, stepX, _origin.X) - from.X) / direction.X : float.MaxValue;
        var tNextY = stepY != 0 ? (NextBoundary(cy, stepY, _origin.Y) - from.Y) / direction.Y : float.MaxValue;
        var tNextZ = stepZ != 0 ? (NextBoundary(cz, stepZ, _origin.Z) - from.Z) / direction.Z : float.MaxValue;

        var tCellEnter = tMin;
        while (tCellEnter <= tMax)
        {
            if (bestT < tCellEnter)
            {
                break; // the recorded hit precedes every cell still ahead
            }
            var cell = (cz * _ny + cy) * _nx + cx;
            for (var i = _cellStart[cell]; i < _cellStart[cell + 1]; i++)
            {
                if (HitTriangle(from, direction, _cellTris[i]) is { } hit && hit.T < bestT)
                {
                    (bestT, bestNormal) = hit;
                }
            }
            if (tNextX <= tNextY && tNextX <= tNextZ)
            {
                tCellEnter = tNextX; cx += stepX; tNextX += tDeltaX;
                if (cx < 0 || cx >= _nx) { break; }
            }
            else if (tNextY <= tNextZ)
            {
                tCellEnter = tNextY; cy += stepY; tNextY += tDeltaY;
                if (cy < 0 || cy >= _ny) { break; }
            }
            else
            {
                tCellEnter = tNextZ; cz += stepZ; tNextZ += tDeltaZ;
                if (cz < 0 || cz >= _nz) { break; }
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
