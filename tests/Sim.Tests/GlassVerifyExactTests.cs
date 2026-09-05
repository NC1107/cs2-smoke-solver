using System.Numerics;
using SmokeSolver.Solver;
using Xunit;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// The exact verifier knows which panes a lineup breaks and where the same
/// throw lands once that glass is gone. A throw through an intact pane loses
/// 60% of its speed at the pane, so the two landings differ; the verifier
/// reports both when it is given the glass-gone world, and neither when it
/// is not.
/// </summary>
public class GlassVerifyExactTests
{
    static readonly Vector3 RegionMin = new(-256, -512, -16);
    static readonly Vector3 RegionMax = new(2048, 512, 512);

    // Flat ground with a pane of glass across the flight at x = 300.
    static CollisionMesh Scene() => SyntheticMeshes.FromQuads(
        [
            (SyntheticMeshes.Ground(-256, 2048, 0).Item1, SyntheticMeshes.Ground(-256, 2048, 0).Item2, SyntheticMeshes.Ground(-256, 2048, 0).Item3, SyntheticMeshes.Ground(-256, 2048, 0).Item4, (byte)0),
            (new[] { 300f, -256f, 0f }, new[] { 300f, 256f, 0f }, new[] { 300f, 256f, 200f }, new[] { 300f, -256f, 200f }, (byte)1),
            (new[] { 302f, -256f, 0f }, new[] { 302f, 256f, 0f }, new[] { 302f, 256f, 200f }, new[] { 302f, -256f, 200f }, (byte)1),
        ],
        ["default", "EntityBreakable"],
        [[], []]);

    // A flat standing throw straight at the pane: it breaks through and lands
    // well short of where the same throw lands with the pane gone.
    static Lineup Candidate() =>
        new(new Vector3(0, 0, 0), YawDeg: 0f, PitchDeg: -5f, ThrowType.Stand, Vector3.Zero,
            Bounces: 1, FlightTime: 1f, RestCrossings: 3, Strength: 1f);

    static Dictionary<int, int> ZoneAround(VoxelGrid grid, Vector3 at, int radius)
    {
        var zone = new Dictionary<int, int>();
        var (x, y, z) = grid.CellOf(at);
        for (var dx = -radius; dx <= radius; dx++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dz = -radius; dz <= radius; dz++)
                {
                    if (grid.InBounds(x + dx, y + dy, z + dz))
                    {
                        zone[grid.Index(x + dx, y + dy, z + dz)] = 3;
                    }
                }
            }
        }
        return zone;
    }

    [Fact]
    public void AThrowThroughAPaneReportsThePaneAndTheLandingWithoutIt()
    {
        var mesh = Scene();
        var grid = VoxelGrid.Build(mesh, 16f, RegionMin, RegionMax);
        var intact = new TriangleCollider(mesh, RegionMin, RegionMax, mesh.GrenadeSolidFilter());
        var glassMask = mesh.GroupMask(["EntityBreakable"]);
        var solid = mesh.GrenadeSolidFilter();
        var gone = new TriangleCollider(mesh, RegionMin, RegionMax, a => solid(a) && !glassMask[a]);
        var c = Candidate();
        var eye = c.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(c.Type));
        var restIntact = GrenadeTrajectory.SimulateExact(intact, new ThrowSpec(eye, c.YawDeg, c.PitchDeg, c.Type, c.Strength)).RestPoint;
        var restGone = GrenadeTrajectory.SimulateExact(gone, new ThrowSpec(eye, c.YawDeg, c.PitchDeg, c.Type, c.Strength)).RestPoint;
        Assert.True(restIntact.X > 302f, $"did not get through the pane: x={restIntact.X:F0}");
        Assert.True(restGone.X - restIntact.X > 8f, $"the pane made no difference: {restIntact.X:F0} vs {restGone.X:F0}");

        var verified = LineupSolver.VerifyExact(grid, intact, ZoneAround(grid, restIntact, 3), [c], colliderGlassGone: gone);

        var l = Assert.Single(verified);
        Assert.Equal(1, l.GlassBreaks);
        Assert.NotNull(l.RestIfBroken);
        Assert.True(Vector3.Distance(l.RestIfBroken!.Value, restGone) < 1f, $"restIfBroken {l.RestIfBroken} vs {restGone}");
        Assert.True(Vector3.Distance(l.RestPoint, restIntact) < 1f);
        Assert.True(SmokeSolver.Cli.LineupApi.StateDependent(l));
    }

    [Fact]
    public void WithoutTheGlassGoneWorldNoAlternativeLandingIsReported()
    {
        var mesh = Scene();
        var grid = VoxelGrid.Build(mesh, 16f, RegionMin, RegionMax);
        var intact = new TriangleCollider(mesh, RegionMin, RegionMax, mesh.GrenadeSolidFilter());
        var c = Candidate();
        var eye = c.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(c.Type));
        var restIntact = GrenadeTrajectory.SimulateExact(intact, new ThrowSpec(eye, c.YawDeg, c.PitchDeg, c.Type, c.Strength)).RestPoint;

        var verified = LineupSolver.VerifyExact(grid, intact, ZoneAround(grid, restIntact, 3), [c]);

        var l = Assert.Single(verified);
        Assert.Equal(1, l.GlassBreaks);
        Assert.Null(l.RestIfBroken);
    }
}
