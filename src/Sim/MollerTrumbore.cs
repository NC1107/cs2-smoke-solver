using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// The one shared Möller-Trumbore ray/segment-vs-triangle core. Returns the
/// parametric hit distance t along <paramref name="direction"/>, or null when
/// the ray misses the triangle's plane or barycentric bounds.
/// </summary>
// Callers apply their own t acceptance window, on purpose - the two windows in
// this codebase differ and both are load-bearing:
//  - TriangleCollider (physics sweep) accepts t in (1e-5, 1]: a contact right
//    at the segment end is still a contact.
//  - TriangleRaycaster (sightline occlusion) accepts t in (1e-4, 1 - 1e-4):
//    both segment endpoints sit ON surfaces (eye/target probes), so both ends
//    back off to avoid self-hitting the probed surface.
// Keeping the window at the call site keeps that difference visible instead of
// silently drifting between two full copies of the intersection math.
public static class MollerTrumbore
{
    public static float? Intersect(Vector3 origin, Vector3 direction, Vector3 a, Vector3 b, Vector3 c)
    {
        const float epsilon = 1e-7f;
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
        return invDet * Vector3.Dot(edge2, q);
    }
}
