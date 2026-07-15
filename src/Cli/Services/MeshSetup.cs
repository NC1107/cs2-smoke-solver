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

public static class MeshSetup
{
    // The default solid set for single-target commands (bestlineup, plineup,
    // validate). Read via GetValueOrDefault rather than written back into the
    // caller's options dictionary - three commands used to each mutate their
    // input with their own copy of this string.
    public const string SingleTargetDefaultAttrs = "Default,default,EntitySolid";

    // The canonical grenade-solid collider for a bounds; five commands each
    // spelled this constructor call out.
    public static TriangleCollider BuildGrenadeCollider(CollisionMesh mesh, Vector3 min, Vector3 max) =>
        new(mesh, min, max, mesh.GrenadeSolidFilter());

    // What blocks the PLAYER, for pin probing and stand-spot checks; the
    // grenade collider above is wrong for that job because it drops the
    // player clips that railings and ledges pin players with.
    public static TriangleCollider BuildPlayerCollider(CollisionMesh mesh, Vector3 min, Vector3 max) =>
        new(mesh, min, max, mesh.PlayerSolidFilter());

    // extract always writes <map>.navareas.json next to the .s2geo, so every
    // command can find nav data by convention instead of demanding --nav.
    public static string DefaultNavAreasPath(Dictionary<string, string> options, CollisionMesh mesh) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", $"{mesh.MapName}.navareas.json");

    public static (CollisionMesh Mesh, float VoxelSize, SmokeParams Params, Func<byte, bool>? AttributeFilter) LoadCommon(
        Dictionary<string, string> options)
    {
        var mesh = CollisionMesh.Load(Require(options, "geo"));
        var voxelSize = float.Parse(options.GetValueOrDefault("voxel", "16"), CultureInfo.InvariantCulture);
        Func<byte, bool>? attributeFilter = null;
        if (options.TryGetValue("attrs", out var attrs))
        {
            var requested = attrs.Split(',', StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var allowed = mesh.AttributeNames
                .Select((name, i) => (name, i))
                .Where(x => requested.Contains(x.name))
                .Select(x => (byte)x.i)
                .ToHashSet();
            var included = mesh.AttributeNames
                .Select((name, i) => (name, i))
                .Where(x => allowed.Contains((byte)x.i));
            Console.WriteLine($"attribute filter: {string.Join(", ", included.Select(x => $"[{x.i}] {x.name}"))}");
            attributeFilter = a => allowed.Contains(a);
        }
        var p = options.ContainsKey("conservative")
            ? SmokeParams.Conservative
            : new SmokeParams(
                MaxRadius: float.Parse(options.GetValueOrDefault("radius", SmokeParams.UncalibratedDefault.MaxRadius.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
                CellBudget: int.Parse(options.GetValueOrDefault("budget", SmokeParams.UncalibratedDefault.CellBudget.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));
        return (mesh, voxelSize, p, attributeFilter);
    }

    public static VoxelGrid BuildGrid(CollisionMesh mesh, float voxelSize, Vector3 min, Vector3 max, Func<byte, bool>? attributeFilter)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var grid = VoxelGrid.Build(mesh, voxelSize, min, max, attributeFilter);
        Console.WriteLine($"voxelized {grid.Nx}x{grid.Ny}x{grid.Nz} cells ({grid.SolidCount} solid) in {started.ElapsedMilliseconds} ms");
        return grid;
    }

    public static (VoxelGrid Grid, SmokeVolume Smoke, CollisionMesh Mesh, Func<byte, bool>? AttributeFilter) BuildAndFill(
        Dictionary<string, string> options, Vector3 restPoint, params Vector3[] extraPoints)
    {
        var (mesh, voxelSize, p, attributeFilter) = LoadCommon(options);

        // Voxelize only the region the query touches; the map is far larger than any one smoke.
        var pad = new Vector3(p.MaxRadius + 4 * voxelSize);
        var min = restPoint - pad;
        var max = restPoint + pad;
        foreach (var point in extraPoints)
        {
            min = Vector3.Min(min, point - new Vector3(2 * voxelSize));
            max = Vector3.Max(max, point + new Vector3(2 * voxelSize));
        }

        var grid = BuildGrid(mesh, voxelSize, min, max, attributeFilter);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var smoke = SmokeFloodFill.Fill(grid, restPoint, p);
        var (smin, smax) = smoke.ComputeBounds();
        Console.WriteLine($"smoke: {smoke.Cells.Length} cells in {started.ElapsedMilliseconds} ms, bounds ({smin.X:F0},{smin.Y:F0},{smin.Z:F0})..({smax.X:F0},{smax.Y:F0},{smax.Z:F0})");
        return (grid, smoke, mesh, attributeFilter);
    }

    public static ThrowConstants LoadConstants(Dictionary<string, string> options)
    {
        var path = options.GetValueOrDefault(
            "constants",
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", "throw-constants.json"));
        if (File.Exists(path))
        {
            Console.WriteLine($"throw constants: {path}");
            return LoadJson<ThrowConstants>(path, "throw constants");
        }
        return ThrowConstants.Default;
    }
}
