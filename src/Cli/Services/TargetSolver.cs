using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
using static SmokeSolver.Cli.LineupApi;
using static SmokeSolver.Cli.TargetSolver;
using static SmokeSolver.Cli.ExtractCommand;
using static SmokeSolver.Cli.InfoCommand;
using static SmokeSolver.Cli.SmokeCommand;
using static SmokeSolver.Cli.SightlineCommand;
using static SmokeSolver.Cli.SolveCommand;
using static SmokeSolver.Cli.GroundCommand;
using static SmokeSolver.Cli.LineupsCommand;
using static SmokeSolver.Cli.ViewerDataCommand;
using static SmokeSolver.Cli.ServeCommand;
using static SmokeSolver.Cli.ThrowCommand;
using static SmokeSolver.Cli.CalibrateCommand;
using static SmokeSolver.Cli.ValidateCommand;
using static SmokeSolver.Cli.ExportGltfCommand;
using static SmokeSolver.Cli.BestLineupCommand;
using static SmokeSolver.Cli.PointLineupCommand;

namespace SmokeSolver.Cli;

public static class TargetSolver
{
    public static TargetSolve SolveForTarget(
        CollisionMesh mesh,
        Func<byte, bool>? attributeFilter,
        List<NavAreaJson> navAreas,
        Vector3 target,
        bool hasTargetZ,
        Vector2? originClickOpt,
        float originReach,
        float tolerance,
        ThrowConstants constants,
        Action<string, int>? onPhase = null,
        Action<Vector3, int>? onOrigin = null,
        Action<Vector3, bool>? onCandidate = null,
        float minStability = 0.4f,
        bool fineScan = false,
        IReadOnlyList<ThrowType>? types = null,
        IReadOnlyList<float>? strengths = null)
    {
        var hasOrigin = originClickOpt.HasValue;
        var originClick = originClickOpt ?? new Vector2(target.X, target.Y);
        // Materialized once: three consumers below used to each rebuild this
        // array-of-arrays from the nav list per solve.
        float[][][] corners = [.. navAreas.Select(a => a.Corners)];
        onPhase?.Invoke("prepare", 0);

        var (meshMin, meshMax) = mesh.ComputeBounds();
        var voxelSize = 16f;
        var min = new Vector3(
            MathF.Max(MathF.Min(target.X, originClick.X - originReach) - 500, meshMin.X),
            MathF.Max(MathF.Min(target.Y, originClick.Y - originReach) - 500, meshMin.Y),
            meshMin.Z);
        var max = new Vector3(
            MathF.Min(MathF.Max(target.X, originClick.X + originReach) + 500, meshMax.X),
            MathF.Min(MathF.Max(target.Y, originClick.Y + originReach) + 500, meshMax.Y),
            // Cap relative to the target: an absolute world-Z cap silently
            // excluded all playable space on high maps like de_vertigo.
            MathF.Min(meshMax.Z + 64, target.Z + 900));
        var grid = VoxelGrid.Build(mesh, voxelSize, min, max, attributeFilter);

        var navZ = hasTargetZ ? null : LineupSolver.NavGroundZ(corners, target.X, target.Y);
        if (navZ is { } z0)
        {
            target = target with { Z = z0 };
        }
        else if (!hasTargetZ)
        {
            var (tx, ty, _) = grid.CellOf(target with { Z = 200 });
            target = SnapTargetToGround(grid, tx, ty) ?? target with { Z = 0 };
        }

        // The "zone" for a two-click query is simply resting close enough to the target.
        var zoneCrossings = new Dictionary<int, int>();
        var cellRange = (int)MathF.Ceiling(tolerance / voxelSize);
        var (cx, cy, cz) = grid.CellOf(target);
        for (var dz = -1; dz <= cellRange; dz++)
        {
            for (var dy = -cellRange; dy <= cellRange; dy++)
            {
                for (var dx = -cellRange; dx <= cellRange; dx++)
                {
                    int x = cx + dx, y = cy + dy, z = cz + dz;
                    if (!grid.InBounds(x, y, z) || grid.IsSolid(grid.Index(x, y, z)))
                    {
                        continue;
                    }
                    if (Vector3.Distance(grid.CellCenter(x, y, z), target) <= tolerance + voxelSize)
                    {
                        zoneCrossings[grid.Index(x, y, z)] = 1;
                    }
                }
            }
        }

        if (zoneCrossings.Count == 0)
        {
            Console.Error.WriteLine($"target ({target.X:F0},{target.Y:F0},{target.Z:F0}) has no reachable landing cells (inside solid, or tolerance too small)");
        }

        // Built before the origins, not after: they are snapped onto its triangles
        // so that the spot a lineup names is the spot the player actually stands on.
        var collider = BuildGrenadeCollider(mesh, min, max);

        var origins = LineupSolver.OriginsFromNavAreas(
                grid,
                corners,
                new Vector3(originClick.X - originReach, originClick.Y - originReach, meshMin.Z),
                new Vector3(originClick.X + originReach, originClick.Y + originReach, max.Z),
                sampleStep: 24f,
                collider: collider)
            .Where(o => Vector2.Distance(new Vector2(o.X, o.Y), originClick) <= originReach)
            .ToList();
        if (hasOrigin)
        {
            // The click names the player's exact intended stand spot. Test it
            // literally (and its pinned variants) - the lattice's nearest sample
            // can sit half a grid step away, and for a tight known lineup that
            // is the difference between finding it and not.
            var clickZ = LineupSolver.NavGroundZ(corners, originClick.X, originClick.Y) ?? target.Z;
            origins.AddRange(LineupSolver.ExactOriginWithPins(grid, collider, new Vector3(originClick.X, originClick.Y, clickZ)));
        }

        // Map-wide searches use a coarser angle grid to stay interactive; a near-click
        // search can afford a fine one. At long range one degree of pitch moves the
        // landing tens of units, so local grids must be fine enough not to step over
        // the tolerance sphere.
        // Fine scan halves the angle lattice: a probe goes 1 -> 0.5 deg, a
        // map-wide sweep 3x4 -> 2x2 - roughly 3x the work, for lineups whose
        // in-zone angle ribbon the normal lattice steps over.
        var (yawStep, pitchStep) = hasOrigin
            ? (fineScan ? (0.5f, 0.5f) : (1f, 1f))
            : (fineScan ? (2f, 2f) : (3f, 4f));
        var coverage = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), int>();
        onPhase?.Invoke("sweep", origins.Count);
        var candidates = LineupSolver.Solve(
            grid, zoneCrossings, min, max,
            types ?? [ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow, ThrowType.RunJumpThrow],
            yawStep, pitchStep,
            // A probe is about ONE spot: keep the exact click, its pinned
            // variants, and each lattice neighbor as distinct results instead
            // of collapsing them into one 64u representative.
            dedupeBucketSize: hasOrigin ? 8f : 64f,
            origins: origins, strengths: strengths, constants: constants, coverage: coverage, onOrigin: onOrigin,
            collider: collider);
        onPhase?.Invoke("verify", candidates.Count);
        var lineups = LineupSolver.VerifyExact(grid, collider, zoneCrossings, candidates, minStability: minStability, constants: constants, onCandidate: onCandidate);

        return new TargetSolve(
            target,
            origins.Count,
            [.. coverage.Select(kv => new[] { kv.Key.X, kv.Key.Y, kv.Value })],
            lineups,
            collider);
    }

    public static Vector3? SnapTargetToGround(VoxelGrid grid, int x, int y)
    {
        if (x < 0 || x >= grid.Nx || y < 0 || y >= grid.Ny)
        {
            return null;
        }
        for (var z = grid.Nz - 2; z >= 1; z--)
        {
            if (!grid.IsSolid(grid.Index(x, y, z)) && grid.IsSolid(grid.Index(x, y, z - 1)))
            {
                var c = grid.CellCenter(x, y, z);
                return new Vector3(c.X, c.Y, c.Z - grid.VoxelSize / 2);
            }
        }
        return null;
    }

    // Where to draw the in-sky aim X: the first surface the aim ray hits, pulled
    // 24u toward the eye so the marker is never buried inside the geometry the
    // player must line their crosshair against.
    public static Vector3 AimReferencePoint(TriangleCollider collider, Vector3 feet, ThrowType type, float pitchDeg, float yawDeg)
    {
        var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
        var dir = GrenadeTrajectory.ForwardFromAngles(pitchDeg, yawDeg);
        var far = eye + dir * 1200f;
        var hit = collider.FirstHit(eye, far);
        var dist = hit is { } h ? MathF.Max(60f, h.T * 1200f - 24f) : 1200f;
        return eye + dir * dist;
    }
}
