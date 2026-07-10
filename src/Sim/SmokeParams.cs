namespace SmokeSolver.Sim;

/// <summary>
/// Flood-fill bounds for one smoke volume model.
/// Calibrated values are placeholders until the game-in-the-loop calibration pass lands;
/// conservative values are a deliberate underestimate so blocked verdicts are precision-safe
/// without any calibration (see DESIGN.md, Conservative Bloom Mode).
/// </summary>
public sealed record SmokeParams(float MaxRadius, int CellBudget)
{
    public static SmokeParams UncalibratedDefault { get; } = new(MaxRadius: 165f, CellBudget: 3500);

    public static SmokeParams Conservative { get; } = new(MaxRadius: 100f, CellBudget: int.MaxValue);
}
