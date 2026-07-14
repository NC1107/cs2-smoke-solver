using System.Numerics;
using SmokeSolver.Solver;

namespace SmokeSolver.Sim.Tests;

public class AimReferenceTests
{
    static readonly Vector3 RegionMin = new(0, 0, -64);
    static readonly Vector3 RegionMax = new(4096, 4096, 1200);

    // Ground plus a free-standing wall: aiming at its face, its top edge, and
    // over it into empty sky covers all three reference tiers.
    static readonly TriangleCollider Scene = BuildScene();

    static TriangleCollider BuildScene()
    {
        var mesh = SyntheticMeshes.FromQuads([
            SyntheticMeshes.Ground(0, 4096, 0),
            SyntheticMeshes.WallX(1000, 0, 4096, 0, 160),
        ]);
        return new TriangleCollider(mesh, RegionMin, RegionMax);
    }

    static readonly Vector3 Feet = new(500, 2048, 0);

    [Fact]
    public void AimIntoEmptySkyIsFlaggedAsASkyShot()
    {
        // 45 degrees up, facing away from the wall over open terrain: every ray
        // in the cone escapes, and so does every ray down both reticle arms, so
        // there is nothing on screen to line the throw up against.
        var info = AimReference.Analyze(Scene, Feet, ThrowType.Stand, pitchDeg: -45f, yawDeg: 180f);

        Assert.True(info.IsSkyShot, $"expected a sky shot, sky fraction was {info.SkyFraction}");
        Assert.Equal("sky", info.Tier);
        Assert.Null(info.ReferencePoint);
        Assert.False(float.IsFinite(info.NearestReticleDeg),
            $"nothing should sit under the reticle here, found a silhouette {info.NearestReticleDeg} deg out");
    }

    [Fact]
    public void SkyAtTheCrosshairIsStillAimableWhenTheReticleCrossesASilhouette()
    {
        // 30 degrees up at the wall. The +-6 degree cone sees nothing but sky -
        // the wall's top edge sits only ~10.9 degrees above the eye - so judging
        // this by the crosshair alone writes it off as unaimable. CS2's grenade
        // reticle draws an arm ~37 degrees down the screen from the crosshair,
        // which puts that top edge right under it, ~19 degrees out.
        var info = AimReference.Analyze(Scene, Feet, ThrowType.Stand, pitchDeg: -30f, yawDeg: 0f);

        Assert.True(info.SkyFraction > 0.95f,
            $"the crosshair cone should be open sky, sky fraction was {info.SkyFraction}");
        Assert.False(info.IsSkyShot, "the reticle has the wall's top edge to align against");
        Assert.Equal("reticle", info.Tier);
        Assert.True(info.NearestReticleDeg is > 15f and < 24f,
            $"expected the wall's top edge ~19 deg below the crosshair, got {info.NearestReticleDeg}");
    }

    [Fact]
    public void AimAtTheMiddleOfABlankWallHasGeometryButNoSilhouette()
    {
        // Level aim at the wall face from 500u: the +-6 degree cone spans
        // ~+-52u, entirely inside the 4096-wide, 160-tall face, and a flat
        // plane produces no depth discontinuities.
        var info = AimReference.Analyze(Scene, Feet, ThrowType.Stand, pitchDeg: 0f, yawDeg: 0f);

        Assert.False(info.IsSkyShot);
        Assert.Equal("flat", info.Tier);
        Assert.NotNull(info.ReferencePoint);
        Assert.True(MathF.Abs(info.ReferencePoint!.Value.X - 1000f) < 1f,
            $"reference should sit on the wall at x=1000, got {info.ReferencePoint}");
    }

    [Fact]
    public void AimAtTheWallTopEdgeReportsANearbySilhouette()
    {
        // Crosshair on the top edge of the wall (z=160, ~10.9 degrees up from
        // the 64u eye at 500u range): rays below hit the face, rays above
        // clear it, so a hit-pattern discontinuity sits at the crosshair.
        var info = AimReference.Analyze(Scene, Feet, ThrowType.Stand, pitchDeg: -10.9f, yawDeg: 0f);

        Assert.False(info.IsSkyShot);
        Assert.Equal("edge", info.Tier);
        Assert.True(info.NearestSilhouetteDeg <= 2f,
            $"the edge runs through the crosshair; nearest silhouette was {info.NearestSilhouetteDeg} deg away");
    }
}
