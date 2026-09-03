using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// A durable name for a lineup: the same physical throw gets the same id
/// whichever solve produced it.
/// </summary>
// A lineup is a tuple of raw floats, and two solves of "the same" throw are
// not float-equal: a solver version bump shifts the refined aim by a fraction
// of a degree, a mesh re-extraction moves the floor by a fraction of a unit.
// Anything that wants to remember a lineup - a favourite, a vote, a shared
// set - needs identity that survives that.
//
// The quantisation is the precision the tool already hands players. The
// console command it emits is `setpos X Y Z; setang P Y` at whole units and a
// tenth of a degree (CliParsing.SetposCommand), because that is what a person
// can type. Finer than that manufactures distinctions nobody can act on;
// coarser merges genuinely different stances, which sit 16u apart on the origin
// grid. A tenth of a degree is also below the solver's own 0.6 degree
// refinement step, so it absorbs a version bump's jitter without merging two
// distinct refinement seeds.
//
// Deliberately mesh-independent: it hashes the throw, not the world the throw
// was found in, so a re-extraction that leaves the throw unchanged leaves the
// id unchanged. A re-extraction that moves the floor enough to change the
// rounded feet height IS a different throw, and then the id changes too.
public static class LineupIdentity
{
    // Sixteen hex characters is 64 bits: no realistic chance of two distinct
    // throws colliding, short enough to sit in a URL or a JSON key.
    const int IdLength = 16;

    public static string Canonical(ThrowType type, float strength, float runYawOffsetDeg, Vector3 feet, float yawDeg, float pitchDeg)
    {
        // Yaw is periodic; a throw at 179.95 and one at -180.05 are the same
        // aim and must not hash apart.
        var yaw = ((yawDeg % 360f) + 360f) % 360f;
        if (yaw >= 359.95f)
        {
            yaw = 0f;
        }
        return string.Create(CultureInfo.InvariantCulture,
            $"{type}|{strength:F2}|{runYawOffsetDeg:F0}|{MathF.Round(feet.X):F0}|{MathF.Round(feet.Y):F0}|{MathF.Round(feet.Z):F0}|{yaw:F1}|{pitchDeg:F1}");
    }

    public static string Id(ThrowType type, float strength, float runYawOffsetDeg, Vector3 feet, float yawDeg, float pitchDeg)
    {
        var canonical = Canonical(type, strength, runYawOffsetDeg, feet, yawDeg, pitchDeg);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash)[..IdLength].ToLowerInvariant();
    }

    public static string Id(Lineup l) =>
        Id(l.Type, l.Strength, l.RunYawOffsetDeg, l.Feet, l.YawDeg, l.PitchDeg);
}
