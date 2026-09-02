using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// That a lineup's stand spot is somewhere a player's hull can actually be, and
/// that what we say about its relationship to the wall is true.
/// </summary>
// Two ways to hand out a position nobody can stand in, and they fail in
// opposite directions. Push the spot too far and the 32x32x72 hull is inside
// the wall - the setpos drops you somewhere else, or nowhere. Leave it short
// and the lineup is labelled "walk into the wall" while actually needing you to
// stop a few units early, which is a measurement nobody can make mid-round.
// Both look identical on a radar, which is exactly why they need pinning here.
public class StanceGeometryTests
{
    static readonly Vector3 BoundsMin = new(-64, -64, -32);
    static readonly Vector3 BoundsMax = new(512, 512, 256);

    // Ground plus two perpendicular walls meeting at the origin corner.
    static CollisionMesh CornerRoom() => SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-64, 512, 0),
        SyntheticMeshes.WallX(0, -64, 512, 0, 128),
        SyntheticMeshes.WallY(-64, 512, 0, 0, 128),
    ]);

    static TriangleCollider Collider() => new(CornerRoom(), BoundsMin, BoundsMax);

    static VoxelGrid Grid() => VoxelGrid.Build(CornerRoom(), 16f, BoundsMin, BoundsMax);

    // Source keeps a small epsilon between a resting hull and the surface it is
    // against, and the stand-spot check allows the same 0.5u skin. Anything
    // deeper than that is geometry the player cannot occupy.
    const float SkinAllowance = 0.5f;
    const float Tolerance = 0.1f;

    [Fact]
    public void EveryGeneratedPinIsSomewhereTheHullActuallyFits()
    {
        var collider = Collider();
        var origins = new List<Vector3> { new(56f, 56f, 0f) };
        LineupSolver.AddPinnedOriginsTo(Grid(), collider, origins);

        Assert.True(origins.Count > 1, "the corner room should have produced pinned origins to check");
        foreach (var feet in origins)
        {
            Assert.NotEqual(StandSpots.Stance.None, StandSpots.StanceAt(collider, feet));
        }
    }

    [Fact]
    public void NoGeneratedOriginIsBuriedInAWall()
    {
        var collider = Collider();
        var origins = new List<Vector3> { new(56f, 56f, 0f) };
        LineupSolver.AddPinnedOriginsTo(Grid(), collider, origins);

        foreach (var feet in origins)
        {
            var gap = LineupSolver.PositionStance(collider, feet).WallGap;
            if (gap is { } g)
            {
                Assert.True(g >= -SkinAllowance - Tolerance,
                    $"feet at {feet} put the hull {-g:F2}u inside a wall, past the {SkinAllowance}u skin - " +
                    "a player cannot stand there, so no lineup should be thrown from it");
            }
        }
    }

    [Theory]
    // Against one wall, and wedged into the corner: both are positions reached
    // by walking into geometry, so both must report contact rather than a gap.
    [InlineData(16f, 300f, 1)]
    [InlineData(16f, 16f, 2)]
    public void APinnedStanceReportsContactNotAGap(float x, float y, int expectedPin)
    {
        var (pin, gap) = LineupSolver.PositionStance(Collider(), new Vector3(x, y, 0f));

        Assert.Equal(expectedPin, pin);
        Assert.NotNull(gap);
        Assert.True(gap!.Value <= 1.5f + Tolerance,
            $"a pinned spot claims you can walk into the wall, but its hull sits {gap.Value:F2}u short of one");
        Assert.True(gap!.Value >= -SkinAllowance - Tolerance,
            $"a pinned spot must not be buried: {gap.Value:F2}u");
    }

    [Theory]
    // The case the badge exists for: close enough to read as a wall spot on the
    // map, far enough that walking into the wall puts you somewhere else.
    [InlineData(24f, 300f, 8f)]
    [InlineData(20f, 300f, 4f)]
    [InlineData(28f, 300f, 12f)]
    public void ASpotShortOfTheWallReportsTheDistanceItIsShortBy(float x, float y, float expectedGap)
    {
        var (pin, gap) = LineupSolver.PositionStance(Collider(), new Vector3(x, y, 0f));

        Assert.Equal(0, pin);
        Assert.NotNull(gap);
        Assert.Equal(expectedGap, gap!.Value, 1f);
    }

    [Fact]
    public void OpenGroundWellClearOfAnyWallReportsNoGapAtAll()
    {
        // Past the notice range there is no wall worth mentioning, and a badge
        // reading "40u off the wall" on open ground would be noise.
        var (pin, gap) = LineupSolver.PositionStance(Collider(), new Vector3(300f, 300f, 0f));

        Assert.Equal(0, pin);
        Assert.Null(gap);
    }
}
