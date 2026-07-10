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

public static class SolveCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var from = ParseVec(Require(options, "from"));
        var targets = ParseTargets(Require(options, "to"));
        var (mesh, voxelSize, p, attributeFilter) = LoadCommon(options);
        var jitter = float.Parse(options.GetValueOrDefault("jitter", "16"), CultureInfo.InvariantCulture);
        var minCells = int.Parse(options.GetValueOrDefault("min-cells", "3"), CultureInfo.InvariantCulture);

        // A qualifying rest cell sits within MaxRadius of a segment, and its fill reaches
        // at most MaxRadius further; pad by both so no candidate's smoke is clipped.
        var pad = new Vector3(2 * p.MaxRadius + 4 * voxelSize);
        var regionMin = targets.Aggregate(from, Vector3.Min) - pad;
        var regionMax = targets.Aggregate(from, Vector3.Max) + pad;
        var grid = BuildGrid(mesh, voxelSize, regionMin, regionMax, attributeFilter);
        var raycaster = new TriangleRaycaster(mesh, regionMin, regionMax, attributeFilter);

        var sightlines = targets.Select(t => new SightlineSpec(from, t, jitter)).ToList();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var result = LandingZoneSolver.Solve(grid, raycaster, sightlines, p, minCells);
        var zone = result.Zone;
        Console.WriteLine($"eye rays: {result.ClearPairs}/{result.TotalPairs} geometry-clear");
        Console.WriteLine($"landing zone: {zone.Count} cells in {started.ElapsedMilliseconds} ms");
        if (zone.Count > 0)
        {
            var min = zone.Aggregate(zone[0].Center, (acc, c) => Vector3.Min(acc, c.Center));
            var max = zone.Aggregate(zone[0].Center, (acc, c) => Vector3.Max(acc, c.Center));
            Console.WriteLine($"zone bounds: ({min.X:F0},{min.Y:F0},{min.Z:F0})..({max.X:F0},{max.Y:F0},{max.Z:F0})");
        }

        if (options.TryGetValue("json", out var jsonPath))
        {
            var payload = new
            {
                map = mesh.MapName,
                buildId = mesh.GameBuildId,
                voxelSize,
                @params = new { p.MaxRadius, p.CellBudget },
                sightlines = sightlines.Select(s => new
                {
                    from = new[] { s.EyeA.X, s.EyeA.Y, s.EyeA.Z },
                    to = new[] { s.EyeB.X, s.EyeB.Y, s.EyeB.Z },
                    jitter,
                    minCells,
                }),
                cells = zone.Select(c => new { center = new[] { c.Center.X, c.Center.Y, c.Center.Z }, c.MinCrossings }),
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"wrote {jsonPath}");
        }
        if (options.TryGetValue("obj", out var objPath))
        {
            VoxelObj.Save(voxelSize, zone.Select(c => c.Center), objPath);
            Console.WriteLine($"wrote {objPath}");
        }
        return zone.Count > 0 ? 0 : 2;
    }
}
