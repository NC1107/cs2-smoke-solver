using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The two helpers that decide where a lineup says your feet go.
/// </summary>
// Both are load-bearing and neither had a test. FloorUnderHull is what stops a
// pasted setpos putting you inside the floor: its own comment records that a
// single centre ray read the ground low by a median of 1.2u and up to 5.3u
// against fifteen real de_dust2 spawns. ExactOriginOnly is what "solve from
// exactly here" means - one spot, no lattice neighbour and no pinned variant
// quietly substituted for the one the user pasted.
public class OriginPlacementTests
{
    static readonly Vector3 BoundsMin = new(-64, -64, -64);
    static readonly Vector3 BoundsMax = new(512, 512, 256);
    const float Voxel = 16f;

    static TriangleCollider ColliderFor(CollisionMesh mesh) => new(mesh, BoundsMin, BoundsMax);
    static VoxelGrid GridFor(CollisionMesh mesh) => VoxelGrid.Build(mesh, Voxel, BoundsMin, BoundsMax);

    static CollisionMesh FlatGround(float z = 0f) =>
        SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(-64, 512, z)]);

    // A low floor with a raised step over the quadrant where x and y both
    // exceed 256 (SyntheticMeshes.Ground squares both axes). A hull centred just
    // short of that corner overhangs it: the centre is over the low floor, one
    // corner is over the high one.
    static CollisionMesh SplitLevelGround() => SyntheticMeshes.FromQuads(
    [
        SyntheticMeshes.Ground(-64, 512, 0),
        SyntheticMeshes.Ground(256, 512, 24),
    ]);

    // ---- FloorUnderHull ----

    [Fact]
    public void OnFlatGroundTheHullFindsTheFloorUnderIt()
    {
        var z = LineupSolver.FloorUnderHull(ColliderFor(FlatGround()), new Vector3(128, 128, 40), 40f, 200f);

        Assert.NotNull(z);
        Assert.Equal(0f, z!.Value, 1f);
    }

    [Fact]
    public void AHullOverhangingAStepStandsOnTheHigherSurface()
    {
        // The regression this function exists for. A player whose hull overlaps
        // a step does not sink to the lower floor - they stand on the step, and
        // a single downward ray from the centre answers with the floor the
        // centre happens to be over, which is the low one.
        var collider = ColliderFor(SplitLevelGround());
        var overhanging = new Vector3(250, 250, 60);

        var hull = LineupSolver.FloorUnderHull(collider, overhanging, 40f, 200f);
        var centreRayOnly = collider.FirstHit(overhanging + new Vector3(0, 0, 40), overhanging - new Vector3(0, 0, 200));

        Assert.NotNull(hull);
        Assert.Equal(24f, hull!.Value, 1f);
        // And prove the naive answer really would have been wrong, so this test
        // fails if the step ever stops overlapping the hull.
        Assert.NotNull(centreRayOnly);
        var centreZ = float.Lerp(overhanging.Z + 40f, overhanging.Z - 200f, centreRayOnly!.Value.T);
        Assert.True(centreZ < hull.Value - 8f,
            $"test setup: the centre ray should hit the LOW floor ({centreZ:F1}) below the hull answer ({hull.Value:F1})");
    }

    [Fact]
    public void OverAHoleWithNothingUnderneathThereIsNoFloor()
    {
        // Nothing within reach below: the caller must be told so rather than
        // handed a height it made up.
        var z = LineupSolver.FloorUnderHull(ColliderFor(FlatGround()), new Vector3(128, 128, 4000), 8f, 64f);

        Assert.Null(z);
    }

    // ---- ExactOriginOnly ----

    [Fact]
    public void AnAlreadyGroundedSeedIsReturnedUnchanged()
    {
        // What someone pasting their own getpos expects: solve from where I am
        // standing, not from a tidied-up version of it.
        var mesh = FlatGround();
        var seed = new Vector3(133.5f, 207.25f, 0f);

        var origins = LineupSolver.ExactOriginOnly(GridFor(mesh), ColliderFor(mesh), seed);

        Assert.Single(origins);
        Assert.Equal(seed.X, origins[0].X, 0.01f);
        Assert.Equal(seed.Y, origins[0].Y, 0.01f);
    }

    [Fact]
    public void AFloatingSeedIsBroughtDownToTheFloor()
    {
        // A spawn entity is a marker, not a foot position - de_dust2's T spawn
        // floats 55u above the ground - and releasing a throw from mid-air puts
        // every lineup's setpos inside the floor.
        var mesh = FlatGround();
        var origins = LineupSolver.ExactOriginOnly(GridFor(mesh), ColliderFor(mesh), new Vector3(128, 128, 55));

        Assert.Single(origins);
        Assert.True(origins[0].Z < 20f,
            $"a seed floating 55u up should have been seated on the floor, got z={origins[0].Z:F1}");
    }

    [Fact]
    public void ExactlyOneOriginComesBackEvenBesideACorner()
    {
        // The contract that separates this from ExactOriginWithPins: beside a
        // corner the pinned path offers several nearby stances, and this one
        // must still answer with the single spot it was asked about.
        var mesh = SyntheticMeshes.FromQuads(
        [
            SyntheticMeshes.Ground(-64, 512, 0),
            SyntheticMeshes.WallX(0, -64, 512, 0, 128),
            SyntheticMeshes.WallY(-64, 512, 0, 0, 128),
        ]);

        var origins = LineupSolver.ExactOriginOnly(GridFor(mesh), ColliderFor(mesh), new Vector3(20, 20, 0));

        Assert.Single(origins);
    }
}
