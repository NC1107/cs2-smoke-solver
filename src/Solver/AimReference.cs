using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// What a lineup has to align its aim against: how much of the crosshair's
/// neighborhood is open sky, how far (in degrees) the nearest silhouette sits
/// from the crosshair, and how far out along CS2's grenade reticle the nearest
/// silhouette sits. Only a throw with nothing to align against anywhere the
/// reticle reaches is genuinely unusable in a match.
/// </summary>
public readonly record struct AimReferenceInfo(
    float SkyFraction,
    float NearestSilhouetteDeg,
    float NearestReticleDeg,
    Vector3? ReferencePoint)
{
    // Nothing at the crosshair AND nothing under the reticle's arms: there is
    // no way to reproduce this aim in game. Sky at the crosshair alone is not
    // enough - that is what the reticle is for.
    public bool IsSkyShot => SkyFraction > 0.95f && !float.IsFinite(NearestReticleDeg);

    // Coarse display tier: "sky" = nothing to aim against at all, "reticle" =
    // sky at the crosshair but the reticle's arms cross a silhouette, "flat" =
    // geometry but no silhouette inside the cone (blank wall), "edge" = a
    // silhouette within NearestSilhouetteDeg of the crosshair.
    public string Tier =>
        IsSkyShot ? "sky"
        : float.IsFinite(NearestSilhouetteDeg) ? "edge"
        : SkyFraction > 0.95f ? "reticle"
        : "flat";
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

    // CS2's grenade reticle is not a dot. It draws lines from the crosshair out
    // to all four screen edges, with tick marks along them, so anything those
    // arms cross is something the throw can be lined up against - which is the
    // whole point of the feature, and why a throw pointing at open sky can still
    // be a perfectly repeatable lineup as long as some skyline is on screen.
    // Half-angles are taken at 4:3, where fov_desired 90 is the horizontal FOV.
    // The vertical half-angle follows from Source's Hor+ scaling and is the same
    // at every aspect ratio; the horizontal one is the narrowest a player can
    // have, so a silhouette found inside it is on a 16:9 screen too.
    const float ReticleHalfWidthDeg = 45f;
    const float ReticleHalfHeightDeg = 36.87f;
    const int ReticleSamples = 41;

    /// <summary>
    /// Casts a small angular raster around the aim direction and reports sky
    /// coverage plus the nearest silhouette (hit/miss boundary or depth jump
    /// between adjacent rays). Uses the same eye the trajectory sim uses so
    /// the analysis matches the in-game aim X placement.
    /// </summary>
    public static AimReferenceInfo Analyze(TriangleCollider collider, Vector3 feet, ThrowType type, float pitchDeg, float yawDeg)
    {
        var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
        var camera = CameraBasis(pitchDeg, yawDeg);
        var step = 2f * ConeHalfAngleDeg / (RaysPerAxis - 1);

        var depths = new float[RaysPerAxis, RaysPerAxis];
        var sky = 0;
        var center = (RaysPerAxis - 1) / 2;
        Vector3? referencePoint = null;
        for (var i = 0; i < RaysPerAxis; i++)
        {
            for (var j = 0; j < RaysPerAxis; j++)
            {
                var dir = ScreenDirection(camera,
                    -ConeHalfAngleDeg + i * step,
                    -ConeHalfAngleDeg + j * step);
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

        var reticle = MathF.Min(
            NearestArmSilhouette(collider, eye, camera, ReticleHalfWidthDeg, horizontal: true),
            NearestArmSilhouette(collider, eye, camera, ReticleHalfHeightDeg, horizontal: false));

        return new AimReferenceInfo((float)sky / (RaysPerAxis * RaysPerAxis), nearest, reticle, referencePoint);
    }

    /// <summary>
    /// Walks one arm of the reticle out to the screen edge and returns how many
    /// degrees from the crosshair the nearest silhouette on it sits, or infinity
    /// if the arm crosses nothing.
    /// </summary>
    static float NearestArmSilhouette(TriangleCollider collider, Vector3 eye, (Vector3 Forward, Vector3 Right, Vector3 Up) camera, float halfAngleDeg, bool horizontal)
    {
        var nearest = float.PositiveInfinity;
        var previousDepth = float.NaN;
        var previousAngle = 0f;
        for (var i = 0; i < ReticleSamples; i++)
        {
            var angle = -halfAngleDeg + 2f * halfAngleDeg * i / (ReticleSamples - 1);
            var dir = horizontal
                ? ScreenDirection(camera, angle, 0f)
                : ScreenDirection(camera, 0f, angle);
            var hit = collider.FirstHit(eye, eye + dir * MaxReferenceRange);
            var depth = hit is { } h ? h.T * MaxReferenceRange : float.PositiveInfinity;
            if (!float.IsNaN(previousDepth) && IsSilhouette(previousDepth, depth))
            {
                nearest = MathF.Min(nearest, MathF.Min(MathF.Abs(previousAngle), MathF.Abs(angle)));
            }
            previousDepth = depth;
            previousAngle = angle;
        }
        return nearest;
    }

    // Everything here is measured in the angles the player actually sees, so the
    // rays are built in the camera's basis rather than by nudging world yaw and
    // pitch. Those two only agree when the aim is level: at a 51 degree upward
    // pitch, 6 degrees of world yaw is under 4 degrees of screen, so a cone
    // built from world angles quietly shrinks to two thirds of its width exactly
    // on the steep throws this is meant to judge.
    static (Vector3 Forward, Vector3 Right, Vector3 Up) CameraBasis(float pitchDeg, float yawDeg)
    {
        var forward = Direction(pitchDeg, yawDeg);
        var across = Vector3.Cross(forward, Vector3.UnitZ);
        // Aiming straight up leaves "right" undefined. Any perpendicular will do:
        // with the horizon gone, every screen direction is equivalent.
        var right = across.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(across);
        return (forward, right, Vector3.Cross(right, forward));
    }

    // Offsetting by the tangent is what makes the angle between this ray and the
    // forward ray come out as exactly (dxDeg, dyDeg) on screen.
    static Vector3 ScreenDirection((Vector3 Forward, Vector3 Right, Vector3 Up) camera, float dxDeg, float dyDeg) =>
        Vector3.Normalize(
            camera.Forward
            + camera.Right * MathF.Tan(dxDeg * MathF.PI / 180f)
            + camera.Up * MathF.Tan(dyDeg * MathF.PI / 180f));

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

    // Raw pitch on purpose: this models what the player's camera looks at,
    // not where the grenade releases (DeriveInitial applies the pitch bias).
    static Vector3 Direction(float pitchDeg, float yawDeg) =>
        GrenadeTrajectory.ForwardFromAngles(pitchDeg, yawDeg);
}
