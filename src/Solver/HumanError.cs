using System.Numerics;
using SmokeSolver.Sim;

namespace SmokeSolver.Solver;

/// <summary>
/// How far from its landing a lineup can be expected to miss when a person,
/// not the solver, throws it: the feet placed the way the spot allows, the aim
/// set against whatever the crosshair has to line up on.
/// </summary>
// This is the number the ranking leads with and the viewer's default filter
// keeps under a threshold. The two halves of a lineup guide - "stand here",
// "aim at that" - each carry an error a player cannot get rid of, and the
// landing pays for both. A corner wedge places the feet exactly; open ground
// is a guess of a couple of dozen units. A silhouette on the crosshair sets
// the aim to half a degree; featureless sky is a several-degree guess, and
// what that costs depends on how far the grenade flies. Which is why a blind
// lob from a corner 200u away is a perfectly good lineup and a pinpoint aim
// from open ground 1500u away is not - a single "aim band" ordering had that
// backwards, and the corner lobs a player would actually use at a site were
// ranked below every long throw with something under the crosshair.
//
// The viewer computes the same estimate (state.js, humanError) so the filter
// and the order agree; keep the two in step.
public static class HumanError
{
    // Foot placement error by how the geometry pins the spot: 2 = corner, 1 =
    // wall (free to slide along it), 0 = open ground.
    public static float PositionError(int pin) => pin switch { 2 => 2f, 1 => 8f, _ => 24f };

    // Aim error in degrees by aim-reference band (AimReference.Band): 0 is a
    // silhouette on the crosshair, 6 is sky.
    public static float AimErrorDeg(int band) => band switch
    {
        0 => 0.5f,
        1 or 2 => 1f,
        3 => 1.5f,
        4 or 5 => 2.5f,
        _ => 5f,
    };

    // Extra landing error from the movement itself: timing a jump, and a run
    // whose speed at release is never quite the same twice.
    public static float MovementError(ThrowType type) => type switch
    {
        ThrowType.JumpThrow or ThrowType.CrouchJumpThrow => 6f,
        ThrowType.RunJumpThrow => 16f,
        _ => 0f,
    };

    // A rest point that moves this much under a one-tick foot shift is chaos,
    // and the scatter itself is the honest error.
    public const float ChaosScatter = 16f;

    // How much a throw the verifier found aim-fragile costs. Stability is the
    // share of small aim perturbations that still land within tolerance; the
    // three validated throws that missed by 200u all sat at the 0.4 gate, on
    // a beam edge, and the model had no term that could see it.
    public const float FragilityError = 24f;

    /// <summary>Expected landing miss, in units, for a person throwing this lineup.</summary>
    public static float Estimate(int pin, int band, float horizontalDistance, ThrowType type, float restScatter, float stability = 1f) =>
        PositionError(pin)
        + horizontalDistance * MathF.Tan(AimErrorDeg(band) * MathF.PI / 180f)
        + MovementError(type)
        + (restScatter > ChaosScatter ? restScatter : 0f)
        + (1f - Math.Clamp(stability, 0f, 1f)) * FragilityError;

    public static float Estimate(Lineup l, int pin, int band) =>
        Estimate(pin, band, Vector2.Distance(new Vector2(l.Feet.X, l.Feet.Y), new Vector2(l.RestPoint.X, l.RestPoint.Y)), l.Type, l.RestScatter, l.Stability);
}
