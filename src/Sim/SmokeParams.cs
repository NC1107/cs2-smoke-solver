namespace SmokeSolver.Sim;

/// <summary>
/// Flood-fill bounds for one smoke volume model.
/// Calibrated values are placeholders until the game-in-the-loop calibration pass lands;
/// conservative values are a deliberate underestimate so blocked verdicts are precision-safe
/// without any calibration (see DESIGN.md, Conservative Bloom Mode).
/// </summary>
public sealed record SmokeParams(float MaxRadius, int CellBudget)
{
    /// <summary>The real grenade's reach: CS:GO shipped a 288-unit-diameter
    /// smoke and CS2 kept it.</summary>
    // The one sourced number in this file. UncalibratedDefault below is 165 and
    // says in its own name that nobody has checked it - it predates this and
    // drives the older CLI solve paths, so it is deliberately left alone rather
    // than quietly retuned underneath them.
    public const float GameRadius = 144f;

    /// <summary>What the coverage overlay draws, deliberately short of the real
    /// bloom so the area shown is area you can count on.</summary>
    // The overlay answers "will the smoke I am placing cover what I want it
    // to", and the honest answer errs small: a ring drawn at the grenade's
    // absolute maximum promises edges that a real throw, landing a few units
    // off, will not deliver. 128 is ~89% of the real radius - close enough to
    // be useful, short enough to be a promise rather than a hope.
    public const float CoverageRadius = 128f;

    /// <summary>Cells the fill may claim before it stops.</summary>
    // A real CS2 smoke has a fixed amount of volume to spend, which is why it
    // climbs higher in a stairwell than it spreads on open ground. A cell budget
    // is the same idea: the fill conforms to whatever geometry it is given and
    // runs out at the same total either way.
    public const int GameCellBudget = 3500;

    public static SmokeParams UncalibratedDefault { get; } = new(MaxRadius: 165f, CellBudget: GameCellBudget);

    /// <summary>The bloom the viewer's coverage overlay shows.</summary>
    public static SmokeParams Coverage { get; } = new(CoverageRadius, GameCellBudget);

    /// <summary>The bloom at the grenade's full documented reach.</summary>
    public static SmokeParams FullReach { get; } = new(GameRadius, GameCellBudget);

    public static SmokeParams Conservative { get; } = new(MaxRadius: 100f, CellBudget: int.MaxValue);
}
