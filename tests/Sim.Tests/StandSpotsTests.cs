using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

// The hull model exists because point raycasts answer the wrong question:
// they slip through gaps a player bridges and report floor for a spot whose
// hull is hanging off a ledge. These pin the rules it replaces them with.
public class StandSpotsTests
{
    static readonly Vector3 BoundsMin = new(0, 0, -64);
    static readonly Vector3 BoundsMax = new(1024, 1024, 512);

    static float[][] Square(float minX, float minY, float maxX, float maxY, float z) =>
    [
        [minX, minY, z], [maxX, minY, z], [maxX, maxY, z], [minX, maxY, z],
    ];

    static TriangleCollider Collide(params (float[], float[], float[], float[])[] quads) =>
        new(SyntheticMeshes.FromQuads(quads), BoundsMin, BoundsMax);

    static (float[], float[], float[], float[])[] Box(float minX, float minY, float maxX, float maxY, float top) =>
    [
        SyntheticMeshes.Ground(minX, maxX, top),
        SyntheticMeshes.WallY(minX, maxX, minY, 0, top),
        SyntheticMeshes.WallY(minX, maxX, maxY, 0, top),
        SyntheticMeshes.WallX(minX, minY, maxY, 0, top),
        SyntheticMeshes.WallX(maxX, minY, maxY, 0, top),
    ];

    [Fact]
    public void OpenGroundTakesTheStandingHull()
    {
        var collider = Collide(SyntheticMeshes.Ground(0, 1024, 0));

        Assert.Equal(StandSpots.Stance.Standing, StandSpots.StanceAt(collider, new Vector3(500, 500, 0)));
    }

    [Fact]
    public void ACeilingBelowStandingHeightForcesACrouch()
    {
        // 60u of headroom: too low to stand in, tall enough to crouch in.
        var collider = Collide(SyntheticMeshes.Ground(0, 1024, 0), SyntheticMeshes.Ceiling(0, 1024, 60));

        Assert.Equal(StandSpots.Stance.Crouching, StandSpots.StanceAt(collider, new Vector3(500, 500, 0)));
    }

    [Fact]
    public void AGapTooShortForEvenACrouchIsNotStandable()
    {
        var collider = Collide(SyntheticMeshes.Ground(0, 1024, 0), SyntheticMeshes.Ceiling(0, 1024, 40));

        Assert.Equal(StandSpots.Stance.None, StandSpots.StanceAt(collider, new Vector3(500, 500, 0)));
    }

    [Fact]
    public void FeetAreSupportedByTheWholeHullFootprintNotJustTheCentrePoint()
    {
        // Two crates with a 16u slot between them. A downward RAY at the centre
        // of the slot falls straight through to the floor; the 32x32 hull
        // bridges the gap and rests on both crate tops. The hull answer is the
        // one a player experiences.
        var quads = new List<(float[], float[], float[], float[])> { SyntheticMeshes.Ground(0, 1024, 0) };
        quads.AddRange(Box(400, 400, 492, 600, 40));
        quads.AddRange(Box(508, 400, 600, 600, 40));
        var collider = new TriangleCollider(SyntheticMeshes.FromQuads(quads), BoundsMin, BoundsMax);

        var heights = StandSpots.SupportedHeights(collider, 500, 500, BoundsMin.Z, 200f);

        Assert.NotEmpty(heights);
        Assert.True(MathF.Abs(heights[0] - 40) < 2,
            $"hull should be held up by the crates either side of the slot, got z={heights[0]}");
    }

    [Fact]
    public void AClimbTallerThanACrouchJumpIsNotTraversable()
    {
        var collider = Collide(SyntheticMeshes.Ground(0, 1024, 0));
        var from = new Vector3(400, 500, 0);

        Assert.True(StandSpots.CanTraverse(collider, from, new Vector3(424, 500, StandSpots.JumpRise - 2), StandSpots.Stance.Standing));
        Assert.False(StandSpots.CanTraverse(collider, from, new Vector3(424, 500, StandSpots.CrouchJumpRise + 2), StandSpots.Stance.Standing));
    }

    [Fact]
    public void FallingCannotPassThroughAFloorIntoTheSpaceBelowIt()
    {
        // An upper floor at z=0 over a basement at z=-60, sealed: stepping off
        // the upper floor lands you on the upper floor's own surface, never in
        // the sealed space underneath. Allowing any drop reached voids a player
        // can never occupy.
        var collider = Collide(SyntheticMeshes.Ground(0, 1024, 0), SyntheticMeshes.Ground(0, 1024, -60));
        var from = new Vector3(500, 500, 0);

        Assert.False(StandSpots.CanTraverse(collider, from, new Vector3(524, 500, -60), StandSpots.Stance.Standing));
    }

    [Theory]
    [InlineData(-180, true)]   // survivable, so it is a route
    [InlineData(-260, false)]  // past 210u: the player is hurt getting there
    public void ADropIsOnlyARouteWhenThePlayerSurvivesIt(float lowerZ, bool expected)
    {
        // Source hurts the player above 580 u/s, which against sv_gravity 800
        // is a 210u fall. Somewhere reachable only by injuring yourself is not
        // somewhere a lineup starts - and treating every drop as free let the
        // search walk off the top of a skyscraper map and claim every surface
        // on the way down.
        // Own bounds: the shared ones stop at z=-64, above these floors.
        var collider = new TriangleCollider(
            SyntheticMeshes.FromQuads([
                SyntheticMeshes.Ground(0, 400, 0),          // upper ledge
                SyntheticMeshes.Ground(0, 1024, lowerZ),    // floor below it
            ]),
            new Vector3(0, 0, lowerZ - 64), BoundsMax);
        var from = new Vector3(380, 380, 0);

        var reached = StandSpots.CanTraverse(collider, from, new Vector3(412, 380, lowerZ), StandSpots.Stance.Standing);

        Assert.Equal(expected, reached);
    }

    [Fact]
    public void ReachableSetGrowsFromNavSeedsAndStopsAtUnreachableGeometry()
    {
        // A crate a jump away from walkable ground, and a pillar far too tall
        // to climb. The crate must be found; the pillar top must not be.
        var quads = new List<(float[], float[], float[], float[])> { SyntheticMeshes.Ground(0, 1024, 0) };
        quads.AddRange(Box(400, 400, 560, 560, 40));
        quads.AddRange(Box(800, 800, 900, 900, 300));
        var collider = new TriangleCollider(SyntheticMeshes.FromQuads(quads), BoundsMin, BoundsMax);
        var nav = Square(100, 100, 700, 700, 0);

        var spots = StandSpots.Compute(collider, [nav], new Vector3(64, 64, -64), new Vector3(960, 960, 400), step: 32f);

        Assert.Contains(spots, s => s.Feet.Z > 30 && s.Feet.Z < 50
            && s.Feet.X >= 400 && s.Feet.X <= 560 && s.Feet.Y >= 400 && s.Feet.Y <= 560);
        Assert.DoesNotContain(spots, s => s.Feet.Z > 200);
    }
}
