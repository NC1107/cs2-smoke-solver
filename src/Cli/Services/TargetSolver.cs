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
        Action<Vector3, bool>? onCandidate = null)
    {
        var hasOrigin = originClickOpt.HasValue;
        var originClick = originClickOpt ?? new Vector2(target.X, target.Y);
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

        var navZ = hasTargetZ ? null : LineupSolver.NavGroundZ([.. navAreas.Select(a => a.Corners)], target.X, target.Y);
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

        var origins = LineupSolver.OriginsFromNavAreas(
                grid,
                [.. navAreas.Select(a => a.Corners)],
                new Vector3(originClick.X - originReach, originClick.Y - originReach, meshMin.Z),
                new Vector3(originClick.X + originReach, originClick.Y + originReach, max.Z),
                sampleStep: 24f)
            .Where(o => Vector2.Distance(new Vector2(o.X, o.Y), originClick) <= originReach)
            .ToList();

        // Map-wide searches use a coarser angle grid to stay interactive; a near-click
        // search can afford a fine one. At long range one degree of pitch moves the
        // landing tens of units, so local grids must be fine enough not to step over
        // the tolerance sphere.
        var (yawStep, pitchStep) = hasOrigin ? (1f, 1f) : (3f, 4f);
        var coverage = new System.Collections.Concurrent.ConcurrentDictionary<(int X, int Y), int>();
        onPhase?.Invoke("sweep", origins.Count);
        var candidates = LineupSolver.Solve(
            grid, zoneCrossings, min, max,
            [ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow, ThrowType.RunJumpThrow],
            yawStep, pitchStep, origins: origins, constants: constants, coverage: coverage, onOrigin: onOrigin);
        var collider = new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter());
        onPhase?.Invoke("verify", candidates.Count);
        var lineups = LineupSolver.VerifyExact(grid, collider, zoneCrossings, candidates, constants: constants, onCandidate: onCandidate);

        return new TargetSolve(
            target,
            origins.Count,
            [.. coverage.Select(kv => new[] { kv.Key.X, kv.Key.Y, kv.Value })],
            lineups,
            min,
            max,
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
        var pr = pitchDeg * MathF.PI / 180f;
        var yr = yawDeg * MathF.PI / 180f;
        var dir = new Vector3(MathF.Cos(pr) * MathF.Cos(yr), MathF.Cos(pr) * MathF.Sin(yr), -MathF.Sin(pr));
        var far = eye + dir * 1200f;
        var hit = collider.FirstHit(eye, far);
        var dist = hit is { } h ? MathF.Max(60f, h.T * 1200f - 24f) : 1200f;
        return eye + dir * dist;
    }
}
