using System.Numerics;

namespace SmokeSolver.Sim;

/// <summary>
/// Triangle vs axis-aligned box separating-axis test (Akenine-Möller).
/// </summary>
static class TriBoxOverlap
{
    public static bool Test(Vector3 boxCenter, Vector3 boxHalf, Vector3 a, Vector3 b, Vector3 c)
    {
        var v0 = a - boxCenter;
        var v1 = b - boxCenter;
        var v2 = c - boxCenter;

        var e0 = v1 - v0;
        var e1 = v2 - v1;
        var e2 = v0 - v2;

        if (!AxisTestX(e0, v0, v2, boxHalf) || !AxisTestY(e0, v0, v2, boxHalf) || !AxisTestZ(e0, v1, v2, boxHalf))
        {
            return false;
        }
        if (!AxisTestX(e1, v0, v2, boxHalf) || !AxisTestY(e1, v0, v2, boxHalf) || !AxisTestZ(e1, v0, v1, boxHalf))
        {
            return false;
        }
        if (!AxisTestX(e2, v0, v1, boxHalf) || !AxisTestY(e2, v0, v1, boxHalf) || !AxisTestZ(e2, v1, v2, boxHalf))
        {
            return false;
        }

        if (!OverlapsOnAxis(v0.X, v1.X, v2.X, boxHalf.X) ||
            !OverlapsOnAxis(v0.Y, v1.Y, v2.Y, boxHalf.Y) ||
            !OverlapsOnAxis(v0.Z, v1.Z, v2.Z, boxHalf.Z))
        {
            return false;
        }

        var normal = Vector3.Cross(e0, e1);
        return PlaneBoxOverlap(normal, v0, boxHalf);
    }

    static bool OverlapsOnAxis(float p0, float p1, float p2, float half)
    {
        var min = MathF.Min(p0, MathF.Min(p1, p2));
        var max = MathF.Max(p0, MathF.Max(p1, p2));
        return min <= half && max >= -half;
    }

    static bool PlaneBoxOverlap(Vector3 normal, Vector3 vert, Vector3 half)
    {
        var vmin = new Vector3(
            normal.X > 0 ? -half.X - vert.X : half.X - vert.X,
            normal.Y > 0 ? -half.Y - vert.Y : half.Y - vert.Y,
            normal.Z > 0 ? -half.Z - vert.Z : half.Z - vert.Z);
        var vmax = new Vector3(
            normal.X > 0 ? half.X - vert.X : -half.X - vert.X,
            normal.Y > 0 ? half.Y - vert.Y : -half.Y - vert.Y,
            normal.Z > 0 ? half.Z - vert.Z : -half.Z - vert.Z);
        if (Vector3.Dot(normal, vmin) > 0)
        {
            return false;
        }
        return Vector3.Dot(normal, vmax) >= 0;
    }

    static bool AxisTestX(Vector3 edge, Vector3 va, Vector3 vb, Vector3 half)
    {
        // Cross product axis (1,0,0) x edge.
        var p0 = edge.Z * va.Y - edge.Y * va.Z;
        var p1 = edge.Z * vb.Y - edge.Y * vb.Z;
        var rad = MathF.Abs(edge.Z) * half.Y + MathF.Abs(edge.Y) * half.Z;
        return MathF.Min(p0, p1) <= rad && MathF.Max(p0, p1) >= -rad;
    }

    static bool AxisTestY(Vector3 edge, Vector3 va, Vector3 vb, Vector3 half)
    {
        var p0 = edge.X * va.Z - edge.Z * va.X;
        var p1 = edge.X * vb.Z - edge.Z * vb.X;
        var rad = MathF.Abs(edge.Z) * half.X + MathF.Abs(edge.X) * half.Z;
        return MathF.Min(p0, p1) <= rad && MathF.Max(p0, p1) >= -rad;
    }

    static bool AxisTestZ(Vector3 edge, Vector3 va, Vector3 vb, Vector3 half)
    {
        var p0 = edge.Y * va.X - edge.X * va.Y;
        var p1 = edge.Y * vb.X - edge.X * vb.Y;
        var rad = MathF.Abs(edge.Y) * half.X + MathF.Abs(edge.X) * half.Y;
        return MathF.Min(p0, p1) <= rad && MathF.Max(p0, p1) >= -rad;
    }
}
