using System.Numerics;
using SmokeSolver.Solver;
using Xunit;

namespace SmokeSolver.Sim.Tests;

public class WeakClickRangeTests
{
    [Fact]
    public void WeakClicksAreBoundedByWhatTheSimulatorLandsNotByTheClickScaleSquared()
    {
        var k = ThrowConstants.Default;
        // Left clicks keep the constants the map-wide sweep has always used.
        Assert.Equal(2000f, ReachTable.Bound(k, ThrowType.Stand, 1f));
        Assert.Equal(3100f, ReachTable.Bound(k, ThrowType.RunJumpThrow, 1f));
        // The old bound for a right click was the constant times 0.09: 180u
        // standing, 279u run-jumping. The flat-ground simulator lands a
        // right-click stand throw past 250u and a right-click run-jump past
        // 1,500u, and the bound must sit above what it lands.
        Assert.True(ReachTable.Bound(k, ThrowType.Stand, 0f) > 250f);
        Assert.True(ReachTable.Bound(k, ThrowType.RunJumpThrow, 0f) > 1500f);
        Assert.True(ReachTable.Bound(k, ThrowType.JumpThrow, 0f) > ReachTable.Bound(k, ThrowType.Stand, 0f));
        // Never above the left click's constant.
        Assert.True(ReachTable.Bound(k, ThrowType.RunJumpThrow, 0.5f) <= 3100f);
    }

    [Fact]
    public void TheSweepFliesARightClickRunJumpTheSimulatorLands()
    {
        // Open ground. A right-click run-jump carries the run for its whole
        // flight, so on the flat it lands no nearer than ~1,250u and as far as
        // ~1,600u; the referee lands one 1,450u out, and the sweep must
        // propose it rather than prune the kind unflown at 279u.
        var mesh = SyntheticMeshes.FromQuads([SyntheticMeshes.Ground(0, 2048, 0)]);
        var grid = VoxelGrid.Build(mesh, 16f, new Vector3(0, 0, -16), new Vector3(2048, 2048, 512));
        var collider = new TriangleCollider(mesh, new Vector3(0, 0, -16), new Vector3(2048, 2048, 512));
        var feet = new Vector3(256, 1024, 0);
        var target = new Vector3(1706, 1024, 0);
        Assert.NotEmpty(LineupSolver.ExhaustiveExactSpot(collider, feet, target, 48f, [ThrowType.RunJumpThrow], [0f], ThrowConstants.Default, stepDeg: 2f));
        var zone = new Dictionary<int, int>();
        for (var x = target.X - 48; x <= target.X + 48; x += grid.VoxelSize)
        {
            for (var y = target.Y - 48; y <= target.Y + 48; y += grid.VoxelSize)
            {
                var (cx, cy, cz) = grid.CellOf(new Vector3(x, y, 0f));
                for (var dz = 0; dz <= 3; dz++)
                {
                    if (grid.InBounds(cx, cy, cz + dz)) { zone[grid.Index(cx, cy, cz + dz)] = 1; }
                }
            }
        }
        var pruned = new List<string>();

        var found = LineupSolver.Solve(grid, zone, new Vector3(0, 0, -16), new Vector3(2048, 2048, 512), [ThrowType.RunJumpThrow],
            yawStepDeg: 2f, pitchStepDeg: 2f, origins: [feet], strengths: [0f], collider: collider, target: target,
            keepEveryKind: true, measuredWeakClickReach: true, onPruned: (_, _, _, _, why) => pruned.Add(why));

        Assert.Empty(pruned);
        Assert.Contains(found, l => l.Type == ThrowType.RunJumpThrow && l.Strength == 0f);

        // The map-wide sweep keeps the old bound on purpose (its cold solve
        // is what a user waits for): the same kind is still pruned there.
        var prunedMapWide = new List<string>();
        var mapWide = LineupSolver.Solve(grid, zone, new Vector3(0, 0, -16), new Vector3(2048, 2048, 512), [ThrowType.RunJumpThrow],
            yawStepDeg: 2f, pitchStepDeg: 2f, origins: [feet], strengths: [0f], collider: collider, target: target,
            keepEveryKind: true, onPruned: (_, _, _, _, why) => prunedMapWide.Add(why));
        Assert.Empty(mapWide);
        Assert.Contains(prunedMapWide, why => why.Contains("max range 279u"));
    }
}
