using System.Numerics;

namespace SmokeSolver.Sim;

// Box-vs-mesh queries: the swept-hull SAT sweep grenades fly on, and the
// static box-overlap probe the radar renderer uses.
public sealed partial class TriangleCollider
{
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

    // Delegates to the voxelizer's SAT test instead of keeping a second,
    // independently-written copy of the same 13-axis algorithm. Equivalence is
    // pinned by TriBoxOverlapDifferentialTests (200k randomized cases plus the
    // degenerate/touching ones where independent SAT code classically drifts).
    bool BoxTriangleOverlap(Vector3 center, Vector3 h, int triangleOffset) =>
        TriBoxOverlap.Test(center, h,
            Vertex(_indices[triangleOffset]),
            Vertex(_indices[triangleOffset + 1]),
            Vertex(_indices[triangleOffset + 2]));

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

}
