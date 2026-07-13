using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// What the crosshair rests against for a lineup: fraction of the aim
/// neighborhood that is open sky, and how far (in degrees) the nearest
/// silhouette sits from the crosshair. A throw aimed into featureless sky
/// has nothing to align against and is nearly unusable in a match no matter
/// how stable its trajectory is.
/// </summary>
public readonly record struct AimReferenceInfo(
    float SkyFraction,
    float NearestSilhouetteDeg,
    Vector3? ReferencePoint)
{
    public bool IsSkyShot => SkyFraction > 0.95f;

    // Coarse display tier: "sky" = nothing to aim against, "flat" = geometry
    // but no silhouette inside the cone (blank wall), "edge" = a silhouette
    // within NearestSilhouetteDeg of the crosshair.
    public string Tier => IsSkyShot ? "sky" : float.IsFinite(NearestSilhouetteDeg) ? "edge" : "flat";
}

public static class AimReference
{
    // The cone approximates the screen area a player scans while lining up:
    // +-6 degrees is roughly the middle seventh of a 90 degree FOV screen.
    const float ConeHalfAngleDeg = 6f;
    const int RaysPerAxis = 9;
    // Hits beyond this range give no usable visual anchor and read as sky.
    const float MaxReferenceRange = 3000f;
    // Neighboring rays whose hit depths differ by more than this ratio sit on
    // different surfaces, which reads as a silhouette edge on screen.
    const float DepthJumpRatio = 0.25f;

    /// <summary>
    /// Casts a small angular raster around the aim direction and reports sky
    /// coverage plus the nearest silhouette (hit/miss boundary or depth jump
    /// between adjacent rays). Uses the same eye the trajectory sim uses so
    /// the analysis matches the in-game aim X placement.
    /// </summary>
    public static AimReferenceInfo Analyze(TriangleCollider collider, Vector3 feet, ThrowType type, float pitchDeg, float yawDeg)
    {
        var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
        var step = 2f * ConeHalfAngleDeg / (RaysPerAxis - 1);

        var depths = new float[RaysPerAxis, RaysPerAxis];
        var sky = 0;
        var center = (RaysPerAxis - 1) / 2;
        Vector3? referencePoint = null;
        for (var i = 0; i < RaysPerAxis; i++)
        {
            for (var j = 0; j < RaysPerAxis; j++)
            {
                var yaw = yawDeg - ConeHalfAngleDeg + i * step;
                var pitch = pitchDeg - ConeHalfAngleDeg + j * step;
                var dir = Direction(pitch, yaw);
                var hit = collider.FirstHit(eye, eye + dir * MaxReferenceRange);
                var depth = hit is { } h ? h.T * MaxReferenceRange : float.PositiveInfinity;
                depths[i, j] = depth;
                if (float.IsInfinity(depth))
                {
                    sky++;
                }
                else if (i == center && j == center)
                {
                    referencePoint = eye + dir * depth;
                }
            }
        }

        // Nearest silhouette: the closest ray pair (by angular distance of the
        // nearer ray to the crosshair) whose depths disagree - one hitting sky
        // while its neighbor hits geometry, or a large relative depth jump.
        var nearest = float.PositiveInfinity;
        for (var i = 0; i < RaysPerAxis; i++)
        {
            for (var j = 0; j < RaysPerAxis; j++)
            {
                foreach (var (ni, nj) in ((int, int)[])[(i + 1, j), (i, j + 1)])
                {
                    if (ni >= RaysPerAxis || nj >= RaysPerAxis || !IsSilhouette(depths[i, j], depths[ni, nj]))
                    {
                        continue;
                    }
                    var di = MathF.Min(AngleFromCenter(i, j, center, step), AngleFromCenter(ni, nj, center, step));
                    nearest = MathF.Min(nearest, di);
                }
            }
        }

        return new AimReferenceInfo((float)sky / (RaysPerAxis * RaysPerAxis), nearest, referencePoint);
    }

    static bool IsSilhouette(float a, float b)
    {
        if (float.IsInfinity(a) != float.IsInfinity(b))
        {
            return true;
        }
        if (float.IsInfinity(a))
        {
            return false;
        }
        var (lo, hi) = a < b ? (a, b) : (b, a);
        return (hi - lo) / hi > DepthJumpRatio;
    }

    static float AngleFromCenter(int i, int j, int center, float step)
    {
        var dx = (i - center) * step;
        var dy = (j - center) * step;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    static Vector3 Direction(float pitchDeg, float yawDeg)
    {
        var pr = pitchDeg * MathF.PI / 180f;
        var yr = yawDeg * MathF.PI / 180f;
        return new Vector3(MathF.Cos(pr) * MathF.Cos(yr), MathF.Cos(pr) * MathF.Sin(yr), -MathF.Sin(pr));
    }
}
