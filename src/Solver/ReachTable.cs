using System.Collections.Concurrent;
using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// How far each kind of throw can carry, measured by the exact simulator on
/// flat ground, for the sweep's range prunes. Left clicks keep the long-
/// standing loose constants; the weaker clicks were bounded by the click
/// scale squared, which put a right-click run-jump at 279u when the simulator
/// lands one at 1,600u on the flat (the jump and the run do not weaken with
/// the click, and the grenade rolls on after it lands).
/// </summary>
public static class ReachTable
{
    // Flat ground long enough that only a left-click jump throw runs off it;
    // a throw that does is unbounded here and keeps the constant.
    const float PlaneLength = 8192f;
    const float Margin = 1.3f;

    static readonly ConcurrentDictionary<ThrowConstants, IReadOnlyDictionary<(ThrowType, float), float>> Tables = new();

    /// <summary>The farthest a throw of this kind lands on flat ground, with margin; the left-click constant at full strength.</summary>
    public static float Bound(ThrowConstants k, ThrowType type, float strength)
    {
        if (strength >= 0.99f)
        {
            return LeftClickBound(type);
        }
        var table = Tables.GetOrAdd(k, Measure);
        return table.TryGetValue((type, k.SpeedScale(strength)), out var reach) ? reach : LeftClickBound(type);
    }

    // Loose upper bounds; a real measured jumpthrow covers 2286u, so err generously.
    public static float LeftClickBound(ThrowType type) => type switch
    {
        ThrowType.Stand or ThrowType.Crouch => 2000f,
        ThrowType.JumpThrow or ThrowType.CrouchJumpThrow => 2700f,
        _ => 3100f,
    };

    static IReadOnlyDictionary<(ThrowType, float), float> Measure(ThrowConstants k)
    {
        var mesh = new CollisionMesh
        {
            MapName = "flat",
            GameBuildId = "reach",
            Vertices = [0, 0, 0, PlaneLength, 0, 0, PlaneLength, 2048, 0, 0, 2048, 0],
            Indices = [0, 1, 2, 0, 2, 3],
            TriangleAttributes = [0, 0],
            AttributeNames = ["default"],
            AttributeInteractAs = [[]],
        };
        var collider = new TriangleCollider(mesh, new Vector3(0, 0, -16), new Vector3(PlaneLength, 2048, 4096));
        var feet = new Vector3(64, 1024, 0);
        var table = new Dictionary<(ThrowType, float), float>();
        foreach (var type in new[] { ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow, ThrowType.RunJumpThrow })
        {
            foreach (var strength in new[] { 0.5f, 0f })
            {
                var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
                var farthest = 0f;
                var unbounded = false;
                for (var pitch = -89f; pitch <= 0f; pitch += 1f)
                {
                    var r = GrenadeTrajectory.SimulateExact(collider, new ThrowSpec(eye, 0f, pitch, type, strength, 0f), k);
                    if (r.Lost || r.RestPoint.X >= PlaneLength - 64f)
                    {
                        unbounded = true;
                        break;
                    }
                    farthest = MathF.Max(farthest, r.RestPoint.X - feet.X);
                }
                table[(type, k.SpeedScale(strength))] = unbounded ? LeftClickBound(type) : MathF.Min(LeftClickBound(type), farthest * Margin);
            }
        }
        return table;
    }
}
