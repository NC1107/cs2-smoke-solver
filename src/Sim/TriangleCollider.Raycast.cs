using System.Numerics;

namespace SmokeSolver.Sim;

// Ray-vs-mesh queries: the zero-width FirstHit entry points, the two-strategy
// grid traversal (short-range AABB walk vs long-range Amanatides-Woo DDA),
// and the per-triangle Möller-Trumbore acceptance window.
public sealed partial class TriangleCollider
{
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
            origin + (cell + (step > 0 ? 1 : 0)) * _cellSize;
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

    (float T, Vector3 Normal)? HitTriangle(Vector3 origin, Vector3 direction, int triangleOffset)
    {
        var a = Vertex(_indices[triangleOffset]);
        var b = Vertex(_indices[triangleOffset + 1]);
        var c = Vertex(_indices[triangleOffset + 2]);
        var t = MollerTrumbore.Intersect(origin, direction, a, b, c);
        // Physics sweep window: a contact right at the segment end (t == 1) is
        // still a contact; only sub-1e-5 self-grazes are rejected.
        if (t is not (> 1e-5f and <= 1f))
        {
            return null;
        }
        var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
        if (Vector3.Dot(normal, direction) > 0)
        {
            normal = -normal;
        }
        return (t.Value, normal);
    }
}
