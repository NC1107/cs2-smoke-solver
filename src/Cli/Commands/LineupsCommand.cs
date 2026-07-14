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

public static class LineupsCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var from = ParseVec(Require(options, "from"));
        var targets = ParseTargets(Require(options, "to"));
        var originParts = Require(options, "origins").Split(',', StringSplitOptions.TrimEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        if (originParts.Length is not (4 or 6))
        {
            throw new ArgumentException("--origins expects x0,y0,x1,y1 or x0,y0,x1,y1,z0,z1");
        }
        var originMin2 = new Vector2(originParts[0], originParts[1]);
        var originMax2 = new Vector2(originParts[2], originParts[3]);
        var originZ = originParts.Length == 6 ? (originParts[4], originParts[5]) : (float.MinValue, float.MaxValue);
        var (mesh, voxelSize, p, attributeFilter) = LoadCommon(options);
        var jitter = float.Parse(options.GetValueOrDefault("jitter", "16"), CultureInfo.InvariantCulture);
        var minCells = int.Parse(options.GetValueOrDefault("min-cells", "3"), CultureInfo.InvariantCulture);
        var top = int.Parse(options.GetValueOrDefault("top", "12"), CultureInfo.InvariantCulture);
        var types = options.GetValueOrDefault("types", "stand,jump,runjump")
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(t => t.ToLowerInvariant() switch
            {
                "stand" => ThrowType.Stand,
                "jump" => ThrowType.JumpThrow,
                "runjump" => ThrowType.RunJumpThrow,
                var other => throw new ArgumentException($"unknown throw type '{other}'"),
            })
            .ToList();

        // The grid must cover the sightlines (zone), the origin region, and the flight arcs.
        var (meshMin, meshMax) = mesh.ComputeBounds();
        var zonePad = 2 * p.MaxRadius + 4 * voxelSize;
        var min = targets.Aggregate(from, Vector3.Min) - new Vector3(zonePad);
        var max = targets.Aggregate(from, Vector3.Max) + new Vector3(zonePad);
        min = Vector3.Min(min, new Vector3(originMin2.X - 64, originMin2.Y - 64, meshMin.Z));
        max = Vector3.Max(max, new Vector3(originMax2.X + 64, originMax2.Y + 64, 0));
        max.Z = Math.Min(meshMax.Z + 64, max.Z + 900);
        var grid = BuildGrid(mesh, voxelSize, min, max, attributeFilter);

        var sightlineMin = targets.Aggregate(from, Vector3.Min) - new Vector3(zonePad);
        var sightlineMax = targets.Aggregate(from, Vector3.Max) + new Vector3(zonePad);
        var raycaster = new TriangleRaycaster(mesh, sightlineMin, sightlineMax, attributeFilter);

        var sightlines = targets.Select(t => new SightlineSpec(from, t, jitter)).ToList();
        var started = System.Diagnostics.Stopwatch.StartNew();
        var zoneResult = LandingZoneSolver.Solve(grid, raycaster, sightlines, p, minCells);
        Console.WriteLine($"eye rays: {zoneResult.ClearPairs}/{zoneResult.TotalPairs} geometry-clear");
        Console.WriteLine($"landing zone: {zoneResult.Zone.Count} cells in {started.ElapsedMilliseconds} ms");
        if (zoneResult.Zone.Count == 0)
        {
            Console.Error.WriteLine("no landing zone; nothing to aim for");
            return 2;
        }

        // The zone tells us where smoke seals the sightlines; the anchor pins it to the
        // choke itself. A smoke that blocks rays mid-lane leaves everything between it
        // and the choke visible, so by default only anchor-adjacent cells count.
        var zoneCells = zoneResult.Zone;
        if (options.TryGetValue("anchor", out var anchorSpec))
        {
            var anchorParts = anchorSpec.Split(',', StringSplitOptions.TrimEntries)
                .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var anchor = new Vector3(anchorParts[0], anchorParts[1], anchorParts[2]);
            var anchorRadius = anchorParts.Length > 3 ? anchorParts[3] : 180f;
            zoneCells = [.. zoneCells.Where(c => Vector3.Distance(c.Center, anchor) <= anchorRadius)];
            Console.WriteLine($"anchored zone: {zoneCells.Count} cells within {anchorRadius:F0}u of ({anchor.X:F0},{anchor.Y:F0},{anchor.Z:F0})");
            if (zoneCells.Count == 0)
            {
                Console.Error.WriteLine("no sealing cells near the anchor; widen the radius or move the anchor");
                return 2;
            }
        }

        var zoneCrossings = new Dictionary<int, int>();
        foreach (var cell in zoneCells)
        {
            var (x, y, z) = grid.CellOf(cell.Center);
            zoneCrossings[grid.Index(x, y, z)] = cell.MinCrossings;
        }

        var navAreasPath = options.GetValueOrDefault(
            "nav",
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", $"{mesh.MapName}.navareas.json"));
        if (!File.Exists(navAreasPath))
        {
            Console.Error.WriteLine($"nav areas not found at {navAreasPath}; run extract first (or pass --nav)");
            return 2;
        }
        var areas = LoadJson<List<NavAreaJson>>(navAreasPath, "nav areas");
        var navOrigins = LineupSolver.OriginsFromNavAreas(
            grid,
            [.. areas.Select(a => a.Corners)],
            new Vector3(originMin2.X, originMin2.Y, Math.Max(min.Z, originZ.Item1)),
            new Vector3(originMax2.X, originMax2.Y, Math.Min(max.Z, originZ.Item2)));
        Console.WriteLine($"origins: {navOrigins.Count} nav-mesh samples");

        var throwConstants = LoadConstants(options);
        started.Restart();
        var candidates = LineupSolver.Solve(
            grid,
            zoneCrossings,
            new Vector3(originMin2.X, originMin2.Y, Math.Max(min.Z, originZ.Item1)),
            new Vector3(originMax2.X, originMax2.Y, Math.Min(max.Z, originZ.Item2)),
            types,
            origins: navOrigins,
            constants: throwConstants);
        Console.WriteLine($"candidates: {candidates.Count} distinct origins in {started.ElapsedMilliseconds} ms");

        started.Restart();
        var collider = new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter());
        var lineups = LineupSolver.VerifyExact(grid, collider, zoneCrossings, candidates, constants: throwConstants);
        Console.WriteLine($"verified against exact geometry: {lineups.Count}/{candidates.Count} in {started.ElapsedMilliseconds} ms");

        foreach (var (l, i) in lineups.Take(top).Select((l, i) => (l, i)))
        {
            Console.WriteLine($"#{i + 1}: {Describe(l.Type, l.Strength)}, {l.Bounces} bounce(s), {l.FlightTime:F1}s flight, stability {l.Stability:P0}, rest ({l.RestPoint.X:F0},{l.RestPoint.Y:F0},{l.RestPoint.Z:F0})");
            Console.WriteLine($"    setpos {l.Feet.X:F0} {l.Feet.Y:F0} {l.Feet.Z + 1:F0}; setang {l.PitchDeg:F1} {l.YawDeg:F1} 0");
        }

        if (options.TryGetValue("json", out var jsonPath))
        {
            var payload = new
            {
                map = mesh.MapName,
                buildId = mesh.GameBuildId,
                voxelSize,
                @params = new { p.MaxRadius, p.CellBudget },
                calibrated = false,
                sightlines = sightlines.Select(s => new
                {
                    from = new[] { s.EyeA.X, s.EyeA.Y, s.EyeA.Z },
                    to = new[] { s.EyeB.X, s.EyeB.Y, s.EyeB.Z },
                }),
                zone = zoneResult.Zone.Select(c => new { center = new[] { c.Center.X, c.Center.Y, c.Center.Z }, c.MinCrossings }),
                lineups = lineups.Take(Math.Max(top, 40)).Select(l => new
                {
                    feet = new[] { l.Feet.X, l.Feet.Y, l.Feet.Z },
                    yaw = l.YawDeg,
                    pitch = l.PitchDeg,
                    type = l.Type.ToString(),
                    how = Describe(l.Type, l.Strength), strength = l.Strength, click = l.Strength >= 0.99f ? "left" : l.Strength >= 0.49f ? "left+right" : "right",
                    rest = new[] { l.RestPoint.X, l.RestPoint.Y, l.RestPoint.Z },
                    l.Bounces,
                    flightTime = l.FlightTime,
                    l.RestCrossings,
                    stability = l.Stability,
                    console = $"setpos {l.Feet.X:F0} {l.Feet.Y:F0} {l.Feet.Z + 1:F0}; setang {l.PitchDeg:F1} {l.YawDeg:F1} 0",
                }),
            };
            File.WriteAllText(jsonPath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"wrote {jsonPath}");
        }
        return lineups.Count > 0 ? 0 : 2;
    }
}
