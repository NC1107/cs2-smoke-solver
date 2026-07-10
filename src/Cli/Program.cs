using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

var commands = new Dictionary<string, Func<Dictionary<string, string>, int>>
{
    ["extract"] = Extract,
    ["info"] = Info,
    ["smoke"] = Smoke,
    ["sightline"] = Sightline,
    ["solve"] = Solve,
    ["ground"] = Ground,
    ["lineups"] = Lineups,
    ["viewerdata"] = ViewerData,
    ["serve"] = Serve,
    ["throw"] = Throw,
    ["calibrate"] = Calibrate,
    ["validate"] = Validate,
    ["exportgltf"] = ExportGltf,
    ["bestlineup"] = BestLineup,
    ["pointlineup"] = PointLineup,
};

if (args.Length == 0 || !commands.TryGetValue(args[0], out var command))
{
    Console.Error.WriteLine("usage: smokesolver <extract|info|smoke|sightline|solve> [--option value ...]");
    Console.Error.WriteLine("  extract   --game <cs2 dir> --map <name> --out <dir>");
    Console.Error.WriteLine("  info      --geo <file.s2geo>");
    Console.Error.WriteLine("  smoke     --geo <file.s2geo> --rest x,y,z [--conservative] [--voxel 16] [--obj out.obj]");
    Console.Error.WriteLine("  sightline --geo <file.s2geo> --from x,y,z --to x,y,z --rest x,y,z [--conservative] [--voxel 16]");
    Console.Error.WriteLine("  solve     --geo <file.s2geo> --from x,y,z --to x,y,z [--conservative] [--jitter 16] [--json out] [--obj out]");
    Console.Error.WriteLine("  ground    --geo <file.s2geo> --from x,y --to x,y [--steps 20] [--zmax 500] [--attrs ...]");
    Console.Error.WriteLine("  lineups   --geo <file.s2geo> --from x,y,z --to \"x,y,z;x,y,z\" --origins x0,y0,x1,y1[,z0,z1] [--anchor x,y,z[,r]] [--types stand,jump,runjump] [--top 12] [--json out]");
    Console.Error.WriteLine("  viewerdata --geo <file.s2geo> --entities <file.json> --region x0,y0,x1,y1 [--attrs ...] [--out data/viewer-map.json]");
    Console.Error.WriteLine("  serve     [--port 8137] (serves viewer/ and data/ from the current directory)");
    Console.Error.WriteLine("  throw     --geo <file.s2geo> --pos x,y,z --ang pitch,yaw [--type stand|jump|runjump] [--strength 1|0.5|0] [--feet] [--solve zone.json]");
    Console.Error.WriteLine("  calibrate --geo <file.s2geo> --throws data/throws.json [--attrs ...] [--out data/throw-constants.json]");
    Console.Error.WriteLine("  validate  --geo <file.s2geo> --nav <navareas.json> --target x,y[,z] [--calib <dir>] [--limit N] [--pass 3] [--dry-run] (throws every solved lineup on the live rig server and grades sim accuracy)");
    return 1;
}
return command(ParseOptions(args.AsSpan(1)));

static Dictionary<string, string> ParseOptions(ReadOnlySpan<string> args)
{
    var options = new Dictionary<string, string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (!args[i].StartsWith("--"))
        {
            throw new ArgumentException($"unexpected argument '{args[i]}'");
        }
        var key = args[i][2..];
        if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
        {
            options[key] = args[++i];
        }
        else
        {
            options[key] = "true";
        }
    }
    return options;
}

static string Require(Dictionary<string, string> options, string key) =>
    options.TryGetValue(key, out var value) ? value : throw new ArgumentException($"missing required option --{key}");

static Vector3 ParseVec(string s)
{
    var parts = s.Split(',', ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length != 3)
    {
        throw new ArgumentException($"expected x,y,z but got '{s}'");
    }
    return new Vector3(
        float.Parse(parts[0], CultureInfo.InvariantCulture),
        float.Parse(parts[1], CultureInfo.InvariantCulture),
        float.Parse(parts[2], CultureInfo.InvariantCulture));
}

static string ReadBuildId(string gameDir)
{
    var steamInf = Path.Combine(gameDir, "game", "csgo", "steam.inf");
    foreach (var line in File.ReadLines(steamInf))
    {
        if (line.StartsWith("ClientVersion=", StringComparison.Ordinal))
        {
            return line["ClientVersion=".Length..].Trim();
        }
    }
    throw new InvalidDataException($"no ClientVersion in {steamInf}");
}

static int Extract(Dictionary<string, string> options)
{
    var gameDir = Require(options, "game");
    var map = Require(options, "map");
    var outDir = options.GetValueOrDefault("out", "data");
    Directory.CreateDirectory(outDir);

    var vpkPath = Path.Combine(gameDir, "game", "csgo", "maps", $"{map}.vpk");
    var buildId = ReadBuildId(gameDir);
    Console.WriteLine($"extracting {map} (build {buildId}) from {vpkPath}");

    var mesh = MapExtractor.ExtractWorldPhysics(vpkPath, map, buildId);
    var geoPath = Path.Combine(outDir, $"{map}.s2geo");
    mesh.Save(geoPath);

    var (min, max) = mesh.ComputeBounds();
    Console.WriteLine($"  {mesh.TriangleCount} triangles, {mesh.Vertices.Length / 3} vertices");
    Console.WriteLine($"  bounds min=({min.X:F0},{min.Y:F0},{min.Z:F0}) max=({max.X:F0},{max.Y:F0},{max.Z:F0})");
    Console.WriteLine($"  collision attributes: {string.Join(", ", mesh.AttributeNames)}");
    Console.WriteLine($"  wrote {geoPath}");

    var navData = MapExtractor.ExtractNavFile(vpkPath, map);
    var navPath = Path.Combine(outDir, $"{map}.nav");
    File.WriteAllBytes(navPath, navData);
    Console.WriteLine($"  wrote {navPath}");
    var navAreas = MapExtractor.ExtractNavAreas(navData);
    var navAreasPath = Path.Combine(outDir, $"{map}.navareas.json");
    File.WriteAllText(navAreasPath, JsonSerializer.Serialize(navAreas));
    Console.WriteLine($"  wrote {navAreasPath} ({navAreas.Count} walkable areas)");

    var entities = MapExtractor.ExtractEntities(vpkPath);
    var entitiesPath = Path.Combine(outDir, $"{map}.entities.json");
    File.WriteAllText(entitiesPath, JsonSerializer.Serialize(entities, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"  wrote {entitiesPath} ({entities.Count} entities)");

    if (options.ContainsKey("obj"))
    {
        var objPath = Path.Combine(outDir, $"{map}.obj");
        mesh.SaveObj(objPath);
        Console.WriteLine($"  wrote {objPath}");
    }
    return 0;
}

static int Info(Dictionary<string, string> options)
{
    var mesh = CollisionMesh.Load(Require(options, "geo"));
    var (min, max) = mesh.ComputeBounds();
    Console.WriteLine($"map: {mesh.MapName} (build {mesh.GameBuildId})");
    Console.WriteLine($"triangles: {mesh.TriangleCount}");
    Console.WriteLine($"bounds: min=({min.X:F0},{min.Y:F0},{min.Z:F0}) max=({max.X:F0},{max.Y:F0},{max.Z:F0})");
    var counts = new int[mesh.AttributeNames.Length];
    foreach (var a in mesh.TriangleAttributes)
    {
        counts[a]++;
    }
    for (var i = 0; i < mesh.AttributeNames.Length; i++)
    {
        Console.WriteLine($"attribute[{i}] {mesh.AttributeNames[i]}: {counts[i]} triangles");
    }
    return 0;
}

static (CollisionMesh Mesh, float VoxelSize, SmokeParams Params, Func<byte, bool>? AttributeFilter) LoadCommon(
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

static VoxelGrid BuildGrid(CollisionMesh mesh, float voxelSize, Vector3 min, Vector3 max, Func<byte, bool>? attributeFilter)
{
    var started = System.Diagnostics.Stopwatch.StartNew();
    var grid = VoxelGrid.Build(mesh, voxelSize, min, max, attributeFilter);
    Console.WriteLine($"voxelized {grid.Nx}x{grid.Ny}x{grid.Nz} cells ({grid.SolidCount} solid) in {started.ElapsedMilliseconds} ms");
    return grid;
}

static (VoxelGrid Grid, SmokeVolume Smoke, CollisionMesh Mesh, Func<byte, bool>? AttributeFilter) BuildAndFill(
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

static List<Vector3> ParseTargets(string s) =>
    [.. s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseVec)];

static int Solve(Dictionary<string, string> options)
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

static int Smoke(Dictionary<string, string> options)
{
    var restPoint = ParseVec(Require(options, "rest"));
    var (grid, smoke, _, _) = BuildAndFill(options, restPoint);

    if (options.TryGetValue("obj", out var objPath))
    {
        VoxelObj.Save(grid.VoxelSize, smoke.Cells.Select(grid.CellCenter), objPath);
        Console.WriteLine($"wrote {objPath}");
    }
    return smoke.Cells.Length > 0 ? 0 : 2;
}

static int Sightline(Dictionary<string, string> options)
{
    var from = ParseVec(Require(options, "from"));
    var to = ParseVec(Require(options, "to"));
    var restPoint = ParseVec(Require(options, "rest"));
    var (_, smoke, mesh, attributeFilter) = BuildAndFill(options, restPoint, from, to);

    var result = Occlusion.Test(smoke, from, to);
    var exactBlocked = new TriangleRaycaster(mesh, Vector3.Min(from, to) - Vector3.One, Vector3.Max(from, to) + Vector3.One, attributeFilter).Blocked(from, to);
    Console.WriteLine($"sightline: {result.SmokeCellsCrossed} smoke cells crossed, geometry blocked: voxel={result.GeometryBlocked} exact={exactBlocked}");
    if (result.FirstSolidHit is { } hit)
    {
        Console.WriteLine($"first solid voxel on ray: ({hit.X:F0},{hit.Y:F0},{hit.Z:F0})");
    }
    var blocked = result.SmokeBlocked(minSmokeCells: 3);
    Console.WriteLine(blocked ? "BLOCKED by smoke" : "NOT blocked by smoke");
    return blocked ? 0 : 2;
}

static int Lineups(Dictionary<string, string> options)
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

    // Reachability is non-negotiable: origins come from the nav mesh.
    var navAreasPath = options.GetValueOrDefault(
        "nav",
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", $"{mesh.MapName}.navareas.json"));
    if (!File.Exists(navAreasPath))
    {
        Console.Error.WriteLine($"nav areas not found at {navAreasPath}; run extract first (or pass --nav)");
        return 2;
    }
    var areas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navAreasPath))!;
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

static int ViewerData(Dictionary<string, string> options)
{
    var (mesh, _, _, attributeFilter) = LoadCommon(options);
    var region = Require(options, "region").Split(',', StringSplitOptions.TrimEntries)
        .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
    var (x0, y0, x1, y1) = (region[0], region[1], region[2], region[3]);
    var outPath = options.GetValueOrDefault("out", "data/viewer-map.json");
    var imagePath = Path.ChangeExtension(outPath, ".png");

    // Radar-style rendering: a horizontal slice at chest height over the local
    // playable ground. Walls become solid strokes, doorways become gaps, and
    // geometry above head height never clutters the picture.
    var navAreasPath = options.GetValueOrDefault(
        "nav",
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", $"{mesh.MapName}.navareas.json"));
    var navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navAreasPath))!;

    const float NavCell = 64f;
    var gw = (int)MathF.Ceiling((x1 - x0) / NavCell);
    var gh = (int)MathF.Ceiling((y1 - y0) / NavCell);
    var navZ = new float?[gw * gh];
    foreach (var area in navAreas)
    {
        var corners = area.Corners;
        var z = corners.Average(c => c[2]);
        var gx0 = Math.Max(0, (int)((corners.Min(c => c[0]) - x0) / NavCell));
        var gx1 = Math.Min(gw - 1, (int)((corners.Max(c => c[0]) - x0) / NavCell));
        var gy0 = Math.Max(0, (int)((corners.Min(c => c[1]) - y0) / NavCell));
        var gy1 = Math.Min(gh - 1, (int)((corners.Max(c => c[1]) - y0) / NavCell));
        for (var gy = gy0; gy <= gy1; gy++)
        {
            for (var gx = gx0; gx <= gx1; gx++)
            {
                var i = gy * gw + gx;
                if (navZ[i] == null || z < navZ[i])
                {
                    navZ[i] = z;
                }
            }
        }
    }

    // Exact-triangle radar: per-pixel vertical raycasts against the real
    // collision mesh at 2u pixels. The earlier 8u voxel slice rounded every
    // wall to voxel boundaries; ray-sampling the triangles renders edges,
    // arches, and props at their true footprint.
    var pixelSize = 2f;
    var (meshMin, meshMax) = mesh.ComputeBounds();
    var radarCollider = new TriangleCollider(
        mesh,
        new Vector3(x0, y0, meshMin.Z),
        new Vector3(x1, y1, MathF.Min(meshMax.Z, 800)),
        attributeFilter);
    Console.WriteLine("exact-triangle radar collider built");

    var navValues = navZ.Where(v => v != null).Select(v => v!.Value).ToList();
    var navLo = navValues.Min();
    var navHi = navValues.Max();

    var w = (int)((x1 - x0) / pixelSize);
    var h = (int)((y1 - y0) / pixelSize);
    var bitmap = new SkiaSharp.SKBitmap(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
    var pixels = new SkiaSharp.SKColor[w * h];

    // A vertical ray is parallel to vertical walls and never hits them; a
    // static box-vs-triangle test is orientation-free.
    bool HitBetween(float wx, float wy, float zLo, float zHi) =>
        radarCollider.BoxIntersects(
            new Vector3(wx, wy, (zLo + zHi) * 0.5f),
            new Vector3(pixelSize * 0.5f, pixelSize * 0.5f, (zHi - zLo) * 0.5f));

    Parallel.For(0, h, py =>
    {
        for (var px = 0; px < w; px++)
        {
            // Image row 0 is the north edge so the viewer can blit directly.
            var wx = x0 + (px + 0.5f) * pixelSize;
            var wy = y1 - (py + 0.5f) * pixelSize;
            var gx = (int)((wx - x0) / NavCell);
            var gy = (int)((wy - y0) / NavCell);
            var ground = gx >= 0 && gx < gw && gy >= 0 && gy < gh ? navZ[gy * gw + gx] : null;
            if (ground == null)
            {
                pixels[py * w + px] = new SkiaSharp.SKColor(0, 0, 0, 0);
                continue;
            }
            // Snap to the actual floor near the nav estimate before slicing.
            var floorZ = ground.Value;
            if (radarCollider.FirstHit(new Vector3(wx, wy, ground.Value + 40), new Vector3(wx, wy, ground.Value - 24), 0f) is { } fh)
            {
                floorZ = ground.Value + 40 + fh.T * (-64f);
            }
            // R encodes the class (0 floor, 128 low cover, 255 wall);
            // G encodes map-level ground height for a subtle floor tint.
            byte cls = 0;
            if (HitBetween(wx, wy, floorZ + 44, floorZ + 76))
            {
                cls = 255;
            }
            else if (HitBetween(wx, wy, floorZ + 12, floorZ + 44))
            {
                cls = 128;
            }
            var tint = (byte)(255 * (ground.Value - navLo) / MathF.Max(1, navHi - navLo));
            pixels[py * w + px] = new SkiaSharp.SKColor(cls, tint, 0, 255);
        }
    });
    // Boundary pass: walls enclosing the playable space sit just outside nav
    // coverage; probe them from their covered neighbors so the map gets the
    // dark outline a radar needs.
    var isCovered = new bool[w * h];
    for (var i = 0; i < pixels.Length; i++)
    {
        isCovered[i] = pixels[i].Alpha != 0;
    }
    Parallel.For(0, h, py =>
    {
        for (var px = 0; px < w; px++)
        {
            if (isCovered[py * w + px])
            {
                continue;
            }
            float? neighborGround = null;
            for (var dy = -1; dy <= 1 && neighborGround == null; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = px + dx, ny = py + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h || !isCovered[ny * w + nx])
                    {
                        continue;
                    }
                    var ngx = (int)((x0 + (nx + 0.5f) * pixelSize - x0) / NavCell);
                    var ngy = (int)((y1 - (ny + 0.5f) * pixelSize - y0) / NavCell);
                    if (ngx >= 0 && ngx < gw && ngy >= 0 && ngy < gh && navZ[ngy * gw + ngx] is { } nz2)
                    {
                        neighborGround = nz2;
                        break;
                    }
                }
            }
            if (neighborGround == null)
            {
                continue;
            }
            var wx = x0 + (px + 0.5f) * pixelSize;
            var wy = y1 - (py + 0.5f) * pixelSize;
            if (HitBetween(wx, wy, neighborGround.Value + 12, neighborGround.Value + 76))
            {
                pixels[py * w + px] = new SkiaSharp.SKColor(255, 0, 0, 255);
            }
        }
    });
    // Thicken the enclosing outline: at 2u pixels a one-pixel wall stroke
    // disappears when the browser minifies the overview; dilating the boundary
    // walls into adjacent uncovered pixels keeps the radar outline readable at
    // every zoom while leaving interior detail crisp.
    for (var pass = 0; pass < 2; pass++)
    {
        var snapshot = (SkiaSharp.SKColor[])pixels.Clone();
        Parallel.For(0, h, py =>
        {
            for (var px = 0; px < w; px++)
            {
                var i = py * w + px;
                if (snapshot[i].Alpha != 0)
                {
                    continue;
                }
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = px + dx, ny = py + dy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h)
                        {
                            continue;
                        }
                        var np = snapshot[ny * w + nx];
                        if (np.Alpha != 0 && np.Red == 255 && np.Green == 0)
                        {
                            pixels[i] = new SkiaSharp.SKColor(255, 0, 0, 255);
                            dy = 2;
                            break;
                        }
                    }
                }
            }
        });
    }
    bitmap.Pixels = pixels;

    using (var image = SkiaSharp.SKImage.FromBitmap(bitmap))
    using (var encoded = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
    using (var stream = File.Create(imagePath))
    {
        encoded.SaveTo(stream);
    }

    var callouts = new List<object[]>();
    if (options.TryGetValue("entities", out var entitiesPath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(entitiesPath));
        var places = new Dictionary<string, List<(float X, float Y)>>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("ClassName").GetString() != "env_cs_place")
            {
                continue;
            }
            var place = e.GetProperty("Place").GetString();
            if (string.IsNullOrEmpty(place))
            {
                continue;
            }
            var o = e.GetProperty("Origin");
            var (ex, ey) = (o[0].GetSingle(), o[1].GetSingle());
            if (ex < x0 || ex > x1 || ey < y0 || ey > y1)
            {
                continue;
            }
            places.TryAdd(place, []);
            places[place].Add((ex, ey));
        }
        foreach (var (name, pts) in places)
        {
            callouts.Add([name, (int)pts.Average(pt => pt.X), (int)pts.Average(pt => pt.Y)]);
        }
    }

    var payload = new
    {
        map = mesh.MapName,
        build = mesh.GameBuildId,
        region = new[] { (int)x0, (int)y0, (int)x1, (int)y1 },
        image = Path.GetFileName(imagePath),
        pixelSize = (int)pixelSize,
        callouts,
    };
    File.WriteAllText(outPath, JsonSerializer.Serialize(payload));
    Console.WriteLine($"wrote {imagePath} ({w}x{h}) and {outPath} ({callouts.Count} callouts)");
    return 0;
}

static int Serve(Dictionary<string, string> options)
{
    var port = int.Parse(options.GetValueOrDefault("port", "8137"), CultureInfo.InvariantCulture);
    var root = Path.GetFullPath(options.GetValueOrDefault("root", "."));

    // With --geo and --nav the server also answers interactive lineup queries.
    CollisionMesh? mesh = null;
    Func<byte, bool>? attributeFilter = null;
    List<NavAreaJson>? navAreas = null;
    ThrowConstants serveConstants = ThrowConstants.Default;
    if (options.ContainsKey("geo"))
    {
        (mesh, _, _, attributeFilter) = LoadCommon(options);
        serveConstants = LoadConstants(options);
        if (options.TryGetValue("nav", out var navPath))
        {
            navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath));
        }
        Console.WriteLine($"lineup API enabled for {mesh.MapName} ({navAreas?.Count ?? 0} nav areas)");
    }

    var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    Console.WriteLine($"serving {root} at http://localhost:{port}/  (ctrl-c to stop)");

    while (true)
    {
        var context = listener.GetContext();
        try
        {
            HandleRequest(context, root, mesh, attributeFilter, navAreas, serveConstants, options.GetValueOrDefault("attrs", ""));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"request failed: {e.Message}");
            try
            {
                context.Response.StatusCode = 500;
                context.Response.Close();
            }
            catch
            {
                // Client already gone; nothing to answer.
            }
        }
    }
}

static byte[] MeshPayload(CollisionMesh mesh, Func<byte, bool>? attributeFilter)
{
    if (MeshPayloadCache.Bytes != null)
    {
        return MeshPayloadCache.Bytes;
    }
    var indices = new List<int>(mesh.Indices.Length);
    for (var t = 0; t < mesh.Indices.Length; t += 3)
    {
        if (attributeFilter == null || attributeFilter(mesh.TriangleAttributes[t / 3]))
        {
            indices.Add(mesh.Indices[t]);
            indices.Add(mesh.Indices[t + 1]);
            indices.Add(mesh.Indices[t + 2]);
        }
    }
    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);
    bw.Write(mesh.Vertices.Length / 3);
    bw.Write(indices.Count);
    foreach (var v in mesh.Vertices)
    {
        bw.Write(v);
    }
    foreach (var i in indices)
    {
        bw.Write((uint)i);
    }
    bw.Flush();
    MeshPayloadCache.Bytes = ms.ToArray();
    return MeshPayloadCache.Bytes;
}

static void HandleRequest(
    System.Net.HttpListenerContext context,
    string root,
    CollisionMesh? mesh,
    Func<byte, bool>? attributeFilter,
    List<NavAreaJson>? navAreas,
    ThrowConstants constants,
    string attrs)
{
    var raw = context.Request.Url?.AbsolutePath ?? "/";
    if (raw == "/api/mesh" && mesh != null)
    {
        // Binary mesh for the 3D view: [int32 vertexCount][int32 indexCount]
        // [float32 x,y,z per vertex][uint32 indices], vision-filtered triangles.
        var body3d = MeshPayload(mesh, attributeFilter);
        context.Response.ContentType = "application/octet-stream";
        context.Response.OutputStream.Write(body3d);
        context.Response.Close();
        return;
    }
    if (raw == "/api/lineup" && context.Request.HttpMethod == "POST")
    {
        if (mesh == null || navAreas == null)
        {
            context.Response.StatusCode = 503;
            var msg = Encoding.UTF8.GetBytes("{\"error\":\"start serve with --geo, --nav (and --attrs) to enable lineup queries\"}");
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(msg);
            context.Response.Close();
            return;
        }
        if (context.Request.ContentLength64 > 4096)
        {
            WriteApiError(context, 400, "request body too large");
            return;
        }
        using var body = JsonDocument.Parse(context.Request.InputStream);
        if (ValidateLineupQuery(body.RootElement, mesh) is { } validationError)
        {
            WriteApiError(context, 400, validationError);
            return;
        }

        // Repeat clicks are free: results are cached on disk keyed by build,
        // constants, and the quantized query. A new game build or recalibration
        // changes the key, so stale answers cannot leak through.
        var cacheKey = QueryCacheKey(mesh, constants, body.RootElement, attrs);
        var cachePath = Path.Combine("data", "cache", cacheKey + ".json");
        string response;
        if (File.Exists(cachePath))
        {
            response = File.ReadAllText(cachePath);
        }
        else
        {
            response = RunTargetQuery(mesh, attributeFilter, navAreas, body.RootElement, constants);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            File.WriteAllText(cachePath, response);
        }
        var bytes = Encoding.UTF8.GetBytes(response);
        context.Response.ContentType = "application/json";
        context.Response.OutputStream.Write(bytes);
        context.Response.Close();
        return;
    }

    var relative = raw == "/" ? "viewer/index.html" : raw.TrimStart('/');
    var path = Path.GetFullPath(Path.Combine(root, relative));
    var allowed = path.StartsWith(root, StringComparison.Ordinal) &&
        (relative.StartsWith("viewer/", StringComparison.Ordinal) || relative.StartsWith("data/", StringComparison.Ordinal));
    if (!allowed || !File.Exists(path))
    {
        context.Response.StatusCode = 404;
        context.Response.Close();
        return;
    }
    context.Response.ContentType = Path.GetExtension(path) switch
    {
        ".html" => "text/html; charset=utf-8",
        ".json" => "application/json",
        ".js" => "text/javascript",
        ".css" => "text/css",
        _ => "application/octet-stream",
    };
    var bytes2 = File.ReadAllBytes(path);
    context.Response.OutputStream.Write(bytes2);
    context.Response.Close();
}

/// <summary>
/// Interactive two-click query: land a smoke at `target`, throwing from near `origin`.
/// Returns the best lineups from nav-walkable positions around the origin click.
/// </summary>
static void WriteApiError(System.Net.HttpListenerContext context, int status, string message)
{
    context.Response.StatusCode = status;
    context.Response.ContentType = "application/json";
    var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = message }));
    context.Response.OutputStream.Write(payload);
    context.Response.Close();
}

// Malformed or absurd queries must fail fast with a 400: a NaN or out-of-map
// coordinate otherwise flows into a minutes-long map-wide solve and a fresh
// cache file per distinct body.
static string? ValidateLineupQuery(JsonElement query, CollisionMesh mesh)
{
    if (query.ValueKind != JsonValueKind.Object)
    {
        return "body must be a JSON object";
    }
    if (!query.TryGetProperty("target", out var targetEl) || targetEl.ValueKind != JsonValueKind.Array ||
        targetEl.GetArrayLength() is < 2 or > 3)
    {
        return "target must be [x,y] or [x,y,z]";
    }
    var (meshMin, meshMax) = mesh.ComputeBounds();
    foreach (var el in targetEl.EnumerateArray())
    {
        if (el.ValueKind != JsonValueKind.Number || !float.IsFinite(el.GetSingle()))
        {
            return "target coordinates must be finite numbers";
        }
    }
    var tx = targetEl[0].GetSingle();
    var ty = targetEl[1].GetSingle();
    if (tx < meshMin.X - 512 || tx > meshMax.X + 512 || ty < meshMin.Y - 512 || ty > meshMax.Y + 512)
    {
        return "target is outside the map bounds";
    }
    if (query.TryGetProperty("origin", out var originEl))
    {
        if (originEl.ValueKind != JsonValueKind.Array || originEl.GetArrayLength() < 2 ||
            originEl.EnumerateArray().Any(e => e.ValueKind != JsonValueKind.Number || !float.IsFinite(e.GetSingle())))
        {
            return "origin must be [x,y] with finite numbers";
        }
    }
    if (query.TryGetProperty("originReach", out var reachEl) &&
        (reachEl.ValueKind != JsonValueKind.Number || !float.IsFinite(reachEl.GetSingle()) ||
         reachEl.GetSingle() is < 16 or > 4000))
    {
        return "originReach must be between 16 and 4000";
    }
    if (query.TryGetProperty("tolerance", out var tolEl) &&
        (tolEl.ValueKind != JsonValueKind.Number || !float.IsFinite(tolEl.GetSingle()) ||
         tolEl.GetSingle() is < 1 or > 512))
    {
        return "tolerance must be between 1 and 512";
    }
    return null;
}

static string QueryCacheKey(CollisionMesh mesh, ThrowConstants constants, JsonElement query, string attrs)
{
    var targetEl = query.GetProperty("target");
    var tx = (int)MathF.Round(targetEl[0].GetSingle() / 16f);
    var ty = (int)MathF.Round(targetEl[1].GetSingle() / 16f);
    var tz = targetEl.GetArrayLength() > 2 ? (int)MathF.Round(targetEl[2].GetSingle() / 16f) : int.MinValue;
    var origin = query.TryGetProperty("origin", out var originEl)
        ? $"{(int)MathF.Round(originEl[0].GetSingle() / 32f)},{(int)MathF.Round(originEl[1].GetSingle() / 32f)}"
        : "all";
    // Every input that changes the answer must be in the key, or two queries
    // differing only in that input replay each other's cached results.
    var reach = query.TryGetProperty("originReach", out var reachEl) ? reachEl.GetSingle() : -1f;
    var tol = query.TryGetProperty("tolerance", out var tolEl) ? tolEl.GetSingle() : 80f;
    // Bump when solver or sim behavior changes: cached answers from older code
    // must never be replayed as current results.
    const int QueryVersion = 6;
    var seed = $"v{QueryVersion}|{mesh.MapName}|{mesh.GameBuildId}|{JsonSerializer.Serialize(constants)}|{tx},{ty},{tz}|{origin}|{reach:F0}|{tol:F0}|{attrs}";
    var hash = System.Security.Cryptography.SHA1.HashData(Encoding.UTF8.GetBytes(seed));
    return Convert.ToHexString(hash)[..20].ToLowerInvariant();
}

static string RunTargetQuery(
    CollisionMesh mesh,
    Func<byte, bool>? attributeFilter,
    List<NavAreaJson> navAreas,
    JsonElement query,
    ThrowConstants constants)
{
    var targetEl = query.GetProperty("target");
    var target = new Vector3(targetEl[0].GetSingle(), targetEl[1].GetSingle(), 0);
    var hasTargetZ = targetEl.GetArrayLength() > 2;
    if (hasTargetZ)
    {
        target.Z = targetEl[2].GetSingle();
    }
    // Without an origin click, search everywhere a player can stand within throw
    // power of the target: reachability and ballistics are the only limits.
    var hasOrigin = query.TryGetProperty("origin", out var originEl);
    var originClick = hasOrigin
        ? new Vector2(originEl[0].GetSingle(), originEl[1].GetSingle())
        : (Vector2?)null;
    var originReach = hasOrigin
        ? (query.TryGetProperty("originReach", out var reachEl) ? reachEl.GetSingle() : 300f)
        : 3100f;
    var tolerance = query.TryGetProperty("tolerance", out var tolEl) ? tolEl.GetSingle() : 80f;

    var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, hasTargetZ, originClick, originReach, tolerance, constants);

    return JsonSerializer.Serialize(new
    {
        target = new[] { solve.Target.X, solve.Target.Y, solve.Target.Z },
        origins = solve.OriginCount,
        // Per evaluated origin: [x, y, raw option count]. Zero-count cells are
        // the interesting ones - places a player can stand where no simulated
        // throw reaches the target (either truly impossible or a sim gap).
        coverage = solve.Coverage,
        lineups = solve.Lineups.Take(hasOrigin ? 6 : 400).Select(l => new
        {
            feet = new[] { l.Feet.X, l.Feet.Y, l.Feet.Z },
            yaw = l.YawDeg,
            pitch = l.PitchDeg,
            type = l.Type.ToString(),
            how = Describe(l.Type, l.Strength), strength = l.Strength, click = l.Strength >= 0.99f ? "left" : l.Strength >= 0.49f ? "left+right" : "right",
            rest = new[] { l.RestPoint.X, l.RestPoint.Y, l.RestPoint.Z },
            l.Bounces,
            flightTime = l.FlightTime,
            stability = l.Stability,
            console = $"setpos {l.Feet.X:F0} {l.Feet.Y:F0} {l.Feet.Z + 1:F0}; setang {l.PitchDeg:F1} {l.YawDeg:F1} 0",
        }),
    });
}

static TargetSolve SolveForTarget(
    CollisionMesh mesh,
    Func<byte, bool>? attributeFilter,
    List<NavAreaJson> navAreas,
    Vector3 target,
    bool hasTargetZ,
    Vector2? originClickOpt,
    float originReach,
    float tolerance,
    ThrowConstants constants)
{
    var hasOrigin = originClickOpt.HasValue;
    var originClick = originClickOpt ?? new Vector2(target.X, target.Y);

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
    var candidates = LineupSolver.Solve(
        grid, zoneCrossings, min, max,
        [ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow, ThrowType.RunJumpThrow],
        yawStep, pitchStep, origins: origins, constants: constants, coverage: coverage);
    var collider = new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter());
    var lineups = LineupSolver.VerifyExact(grid, collider, zoneCrossings, candidates, constants: constants);

    return new TargetSolve(
        target,
        origins.Count,
        [.. coverage.Select(kv => new[] { kv.Key.X, kv.Key.Y, kv.Value })],
        lineups,
        min,
        max);
}

static Vector3? SnapTargetToGround(VoxelGrid grid, int x, int y)
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

static int Throw(Dictionary<string, string> options)
{
    // --pos takes a getpos-style EYE position; getpos prints eye, getpos_exact prints feet.
    var eye = ParseVec(Require(options, "pos"));
    if (options.ContainsKey("feet"))
    {
        eye += new Vector3(0, 0, 64);
    }
    var angParts = Require(options, "ang").Split(',', ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var pitch = float.Parse(angParts[0], CultureInfo.InvariantCulture);
    var yaw = float.Parse(angParts[1], CultureInfo.InvariantCulture);
    var type = options.GetValueOrDefault("type", "stand").ToLowerInvariant() switch
    {
        "stand" => ThrowType.Stand,
        "jump" => ThrowType.JumpThrow,
        "runjump" => ThrowType.RunJumpThrow,
        var other => throw new ArgumentException($"unknown throw type '{other}'"),
    };

    var strength = float.Parse(options.GetValueOrDefault("strength", "1"), CultureInfo.InvariantCulture);
    var (mesh, voxelSize, _, attributeFilter) = LoadCommon(options);
    var (meshMin, meshMax) = mesh.ComputeBounds();
    var reach = new Vector3(2600, 2600, 0);
    var min = Vector3.Max(eye - reach, meshMin) with { Z = meshMin.Z };
    var max = Vector3.Min(eye + reach, meshMax) with { Z = Math.Min(meshMax.Z + 64, eye.Z + 1300) };
    var grid = BuildGrid(mesh, voxelSize, min, max, attributeFilter);

    var baseConstants = LoadConstants(options);
    var constants = new ThrowConstants(
        ThrowSpeed: float.Parse(options.GetValueOrDefault("speed", baseConstants.ThrowSpeed.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
        GravityScale: float.Parse(options.GetValueOrDefault("gravity", baseConstants.GravityScale.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
        Elasticity: float.Parse(options.GetValueOrDefault("elasticity", baseConstants.Elasticity.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture),
        JumpVelocity: float.Parse(options.GetValueOrDefault("jumpv", baseConstants.JumpVelocity.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture));

    var result = GrenadeTrajectory.Simulate(grid, new ThrowSpec(eye, yaw, pitch, type, strength), constants);
    var traceLines = options.ContainsKey("trace") ? new List<string>() : null;
    var exact = GrenadeTrajectory.SimulateExact(new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter()), new ThrowSpec(eye, yaw, pitch, type, strength), constants, traceLines);
    if (traceLines != null)
    {
        foreach (var line in traceLines)
        {
            Console.WriteLine($"  exact {line}");
        }
    }
    Console.WriteLine($"throw: {Describe(type, strength)}, eye ({eye.X:F0},{eye.Y:F0},{eye.Z:F0}), pitch {pitch:F1}, yaw {yaw:F1}");
    if (result.FirstTouch is { } touch)
    {
        Console.WriteLine($"first touch (voxel): ({touch.X:F1},{touch.Y:F1},{touch.Z:F1})");
    }
    Console.WriteLine($"rest (voxel): ({result.RestPoint.X:F1},{result.RestPoint.Y:F1},{result.RestPoint.Z:F1})  bounces {result.Bounces}  flight {result.FlightTime:F2}s  lost {result.Lost}");
    Console.WriteLine($"rest (exact): ({exact.RestPoint.X:F1},{exact.RestPoint.Y:F1},{exact.RestPoint.Z:F1})  bounces {exact.Bounces}  flight {exact.FlightTime:F2}s  lost {exact.Lost}");
    var divergence = Vector3.Distance(result.RestPoint, exact.RestPoint);
    if (divergence > 64)
    {
        Console.WriteLine($"voxel/exact rest points diverge by {divergence:F0}u; trust the exact result (slanted geometry on the path)");
    }

    if (options.TryGetValue("solve", out var solvePath))
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(solvePath));
        var inZone = false;
        var (rx, ry, rz) = grid.CellOf(result.RestPoint);
        foreach (var c in doc.RootElement.GetProperty("zone").EnumerateArray())
        {
            var center = c.GetProperty("center");
            var (zx, zy, zz) = grid.CellOf(new Vector3(center[0].GetSingle(), center[1].GetSingle(), center[2].GetSingle()));
            // The zone stores the first free cell above ground while a grenade can rest
            // a voxel lower against the floor; tolerate one cell of z quantization.
            if (zx == rx && zy == ry && Math.Abs(zz - rz) <= 1)
            {
                inZone = true;
                break;
            }
        }
        Console.WriteLine(inZone ? "rest point IS in the sealing zone" : "rest point is NOT in the sealing zone");
    }
    return result.Lost ? 2 : 0;
}

static string Describe(ThrowType type, float strength = 1f)
{
    var movement = type switch
    {
        ThrowType.Stand => "stand still",
        ThrowType.Crouch => "crouch (hold ctrl)",
        ThrowType.JumpThrow => "jumpthrow bind",
        ThrowType.CrouchJumpThrow => "crouch + jumpthrow bind",
        _ => "run forward (W) + jumpthrow bind",
    };
    var buttons = strength switch
    {
        >= 0.99f => "left click",
        >= 0.49f => "left+right click",
        _ => "right click",
    };
    return $"{movement}, {buttons}";
}

static int Ground(Dictionary<string, string> options)
{
    var fromParts = Require(options, "from").Split(',');
    var toParts = Require(options, "to").Split(',');
    var from = new Vector2(float.Parse(fromParts[0], CultureInfo.InvariantCulture), float.Parse(fromParts[1], CultureInfo.InvariantCulture));
    var to = new Vector2(float.Parse(toParts[0], CultureInfo.InvariantCulture), float.Parse(toParts[1], CultureInfo.InvariantCulture));
    var steps = int.Parse(options.GetValueOrDefault("steps", "20"), CultureInfo.InvariantCulture);
    var zMax = float.Parse(options.GetValueOrDefault("zmax", "500"), CultureInfo.InvariantCulture);

    var (mesh, voxelSize, _, attributeFilter) = LoadCommon(options);
    var (meshMin, _) = mesh.ComputeBounds();
    var min = new Vector3(MathF.Min(from.X, to.X) - 32, MathF.Min(from.Y, to.Y) - 32, meshMin.Z);
    var max = new Vector3(MathF.Max(from.X, to.X) + 32, MathF.Max(from.Y, to.Y) + 32, zMax);
    var grid = BuildGrid(mesh, voxelSize, min, max, attributeFilter);

    Console.WriteLine("x\ty\tground_z");
    for (var i = 0; i <= steps; i++)
    {
        var t = (float)i / steps;
        var p = Vector2.Lerp(from, to, t);
        var (x, y, _) = grid.CellOf(new Vector3(p.X, p.Y, 0));
        var groundZ = float.NaN;
        for (var z = grid.Nz - 2; z >= 0; z--)
        {
            if (grid.IsSolid(grid.Index(x, y, z)) && !grid.IsSolid(grid.Index(x, y, z + 1)))
            {
                groundZ = grid.CellCenter(x, y, z).Z + grid.VoxelSize / 2;
                break;
            }
        }
        Console.WriteLine($"{p.X:F0}\t{p.Y:F0}\t{groundZ:F0}");
    }
    return 0;
}

static int Calibrate(Dictionary<string, string> options)
{
    var (mesh, _, _, attributeFilter) = LoadCommon(options);
    var outPath = options.GetValueOrDefault("out", "data/throw-constants.json");
    using var doc = JsonDocument.Parse(File.ReadAllText(Require(options, "throws")));

    // measure "rest" compares the settled position; measure "impact" compares the
    // first collision point (horizontal only: standing-on-the-spot measurements
    // carry eye-height ambiguity in z).
    var samples = new List<(ThrowSpec Spec, Vector3 Landing, bool Impact)>();
    foreach (var entry in doc.RootElement.EnumerateArray())
    {
        var (eye, pitch, yaw) = ParseGetPos(entry.GetProperty("throw").GetString()!);
        var (landingEye, _, _) = ParseGetPos(entry.GetProperty("landing").GetString()!);
        var type = entry.TryGetProperty("type", out var typeEl) ? typeEl.GetString()!.ToLowerInvariant() : "stand";
        var strength = entry.TryGetProperty("strength", out var strengthEl) ? strengthEl.GetSingle() : 1f;
        var impact = entry.TryGetProperty("measure", out var measureEl) && measureEl.GetString() == "impact";
        samples.Add((
            new ThrowSpec(eye, yaw, pitch, type switch
            {
                "stand" => ThrowType.Stand,
                "jump" => ThrowType.JumpThrow,
                "runjump" => ThrowType.RunJumpThrow,
                var other => throw new ArgumentException($"unknown throw type '{other}'"),
            }, strength),
            landingEye - new Vector3(0, 0, 64),
            impact));
    }
    Console.WriteLine($"{samples.Count} measured throw(s)");

    var min = new Vector3(float.MaxValue);
    var max = new Vector3(float.MinValue);
    foreach (var (spec, landing, _) in samples)
    {
        min = Vector3.Min(min, Vector3.Min(spec.EyePosition, landing));
        max = Vector3.Max(max, Vector3.Max(spec.EyePosition, landing));
    }
    var (meshMin, meshMax) = mesh.ComputeBounds();
    min = Vector3.Max(min - new Vector3(600, 600, 0), meshMin) with { Z = meshMin.Z };
    max = Vector3.Min(max + new Vector3(600, 600, 0), meshMax) with { Z = MathF.Min(meshMax.Z + 64, max.Z + 1300) };
    var collider = new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter());

    float Error(ThrowConstants k)
    {
        var total = 0f;
        foreach (var (spec, landing, impact) in samples)
        {
            var result = GrenadeTrajectory.SimulateExact(collider, spec, k);
            if (impact)
            {
                total += result.FirstTouch is { } touch
                    ? new Vector2(touch.X - landing.X, touch.Y - landing.Y).Length()
                    : 10000f;
                continue;
            }
            var d = result.RestPoint - landing;
            // Rest z is quantized by geometry anyway; horizontal accuracy matters most.
            total += result.Lost ? 10000f : new Vector2(d.X, d.Y).Length() + MathF.Abs(d.Z) * 0.5f;
        }
        return total / samples.Count;
    }

    var bestK = ThrowConstants.Default;
    var bestError = Error(bestK);
    Console.WriteLine($"error with current constants: {bestError:F1}u");

    (string Name, float[] Values)[] sweeps =
    [
        ("speed", [600, 625, 650, 675, 690, 705, 725, 750]),
        ("gravity", [0.32f, 0.36f, 0.4f, 0.44f, 0.48f]),
        ("jumpv", [240, 270, 300, 330, 360]),
        ("elasticity", [0.4f, 0.45f, 0.5f]),
        ("rightscale", [0.22f, 0.26f, 0.30f, 0.34f, 0.38f]),
        ("bothscale", [0.6f, 0.66f, 0.7f, 0.74f, 0.78f, 0.85f]),
    ];
    for (var pass = 0; pass < 3; pass++)
    {
        foreach (var (name, values) in sweeps)
        {
            foreach (var v in values)
            {
                var candidate = name switch
                {
                    "speed" => bestK with { ThrowSpeed = v },
                    "gravity" => bestK with { GravityScale = v },
                    "jumpv" => bestK with { JumpVelocity = v },
                    "elasticity" => bestK with { Elasticity = v },
                    "rightscale" => bestK with { RightClickScale = v },
                    _ => bestK with { BothClickScale = v },
                };
                var e = Error(candidate);
                if (e < bestError)
                {
                    (bestK, bestError) = (candidate, e);
                }
            }
        }
    }

    Console.WriteLine($"fitted: speed {bestK.ThrowSpeed:F0}, gravity {bestK.GravityScale:F2}, jumpv {bestK.JumpVelocity:F0}, elasticity {bestK.Elasticity:F2}, right {bestK.RightClickScale:F2}, both {bestK.BothClickScale:F2}");
    Console.WriteLine($"mean error: {bestError:F1}u over {samples.Count} sample(s)");
    foreach (var (spec, landing, impact) in samples)
    {
        var r = GrenadeTrajectory.SimulateExact(collider, spec, bestK);
        var point = impact ? (r.FirstTouch ?? r.RestPoint) : r.RestPoint;
        var kind = impact ? "impact" : "rest";
        Console.WriteLine($"  {kind} residual {new Vector2(point.X - landing.X, point.Y - landing.Y).Length():F0}u  predicted ({point.X:F0},{point.Y:F0},{point.Z:F0}) vs measured ({landing.X:F0},{landing.Y:F0},{landing.Z:F0})");
    }
    File.WriteAllText(outPath, JsonSerializer.Serialize(bestK, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"wrote {outPath} (picked up automatically next to the geometry file)");
    return 0;
}

static ThrowConstants LoadConstants(Dictionary<string, string> options)
{
    var path = options.GetValueOrDefault(
        "constants",
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", "throw-constants.json"));
    if (File.Exists(path))
    {
        Console.WriteLine($"throw constants: {path}");
        return JsonSerializer.Deserialize<ThrowConstants>(File.ReadAllText(path))!;
    }
    return ThrowConstants.Default;
}

static (Vector3 Eye, float Pitch, float Yaw) ParseGetPos(string line)
{
    var m = System.Text.RegularExpressions.Regex.Match(
        line,
        @"setpos\s+(-?[\d.]+)\s+(-?[\d.]+)\s+(-?[\d.]+)\s*;?\s*(?:setang\s+(-?[\d.]+)\s+(-?[\d.]+))?");
    if (!m.Success)
    {
        throw new ArgumentException($"cannot parse getpos line: '{line}'");
    }
    float F(int g) => float.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
    return (
        new Vector3(F(1), F(2), F(3)),
        m.Groups[4].Success ? F(4) : 0f,
        m.Groups[5].Success ? F(5) : 0f);
}


// End-to-end accuracy pipeline: solve a target, throw every lineup's exact
// initial conditions on the live rig server via the CalibrationThrower request
// file, match the captured real rest points back to predictions, and grade.
// This validates flight physics + map geometry (initial conditions are
// injected, so player release derivation is exercised only in the sim).
// Nearest practical lineup for a target, ranked by walking distance from the
// player's current position (movement-type penalties keep "walk 50u further"
// preferable to "hit a run-jump-throw"). Emits one JSON object for the watcher.
static int BestLineup(Dictionary<string, string> options)
{
    if (!options.ContainsKey("attrs"))
    {
        options["attrs"] = "Default,default,EntitySolid";
    }
    var (mesh, _, _, attributeFilter) = LoadCommon(options);
    var navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(Require(options, "nav")))!;
    var constants = LoadConstants(options);
    var tp = Require(options, "target").Split(',', StringSplitOptions.TrimEntries);
    var target = new Vector3(float.Parse(tp[0], CultureInfo.InvariantCulture), float.Parse(tp[1], CultureInfo.InvariantCulture), tp.Length > 2 ? float.Parse(tp[2], CultureInfo.InvariantCulture) : 0);
    var np = Require(options, "near").Split(',', StringSplitOptions.TrimEntries);
    var near = new Vector3(float.Parse(np[0], CultureInfo.InvariantCulture), float.Parse(np[1], CultureInfo.InvariantCulture), float.Parse(np[2], CultureInfo.InvariantCulture));

    var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, tp.Length > 2, null, 3100f, 80f, constants);
    if (solve.Lineups.Count == 0)
    {
        Console.WriteLine("{\"found\":false}");
        return 0;
    }
    float TypePenalty(ThrowType t) => t switch
    {
        ThrowType.Stand or ThrowType.Crouch => 0f,
        ThrowType.JumpThrow or ThrowType.CrouchJumpThrow => 200f,
        _ => 400f,
    };
    var best = solve.Lineups
        .OrderBy(l => Vector3.Distance(l.Feet, near) + TypePenalty(l.Type))
        .First();
    var aimCollider = new TriangleCollider(mesh, solve.RegionMin, solve.RegionMax, mesh.GrenadeSolidFilter());
    var aim = AimReferencePoint(aimCollider, best.Feet, best.Type, best.PitchDeg, best.YawDeg);
    var result = new
    {
        found = true,
        feet = new[] { best.Feet.X, best.Feet.Y, best.Feet.Z },
        pitch = best.PitchDeg,
        yaw = best.YawDeg,
        type = best.Type.ToString(),
        strength = best.Strength,
        dist = Vector3.Distance(best.Feet, near),
        candidates = solve.Lineups.Count,
        aim = new[] { aim.X, aim.Y, aim.Z },
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}

// Fixed-position inverse solve: the stand spot is given (player's exact feet)
// and we search angles x click x movement for a throw that rests on the
// target. Coarse grid aimed at the target, then two refinement passes.
static int PointLineup(Dictionary<string, string> options)
{
    if (!options.ContainsKey("attrs"))
    {
        options["attrs"] = "Default,default,EntitySolid";
    }
    var (mesh, _, _, attributeFilter) = LoadCommon(options);
    var constants = LoadConstants(options);
    var fp = Require(options, "from").Split(',', StringSplitOptions.TrimEntries);
    var feet = new Vector3(float.Parse(fp[0], CultureInfo.InvariantCulture), float.Parse(fp[1], CultureInfo.InvariantCulture), float.Parse(fp[2], CultureInfo.InvariantCulture));
    var tp = Require(options, "target").Split(',', StringSplitOptions.TrimEntries);
    var target = new Vector3(float.Parse(tp[0], CultureInfo.InvariantCulture), float.Parse(tp[1], CultureInfo.InvariantCulture), float.Parse(tp[2], CultureInfo.InvariantCulture));

    var (meshMin, meshMax) = mesh.ComputeBounds();
    var lo = Vector3.Min(feet, target) - new Vector3(700, 700, 250);
    var hi = Vector3.Max(feet, target) + new Vector3(700, 700, 950);
    var min = Vector3.Max(lo, meshMin);
    var max = Vector3.Min(hi, meshMax);
    var collider = new TriangleCollider(mesh, min, max, mesh.GrenadeSolidFilter());

    var baseYaw = MathF.Atan2(target.Y - feet.Y, target.X - feet.X) * 180f / MathF.PI;
    var types = new[] { ThrowType.Stand, ThrowType.Crouch, ThrowType.JumpThrow, ThrowType.CrouchJumpThrow };
    var strengths = new[] { 1f, 0.5f, 0f };

    (float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos) best =
        (float.MaxValue, ThrowType.Stand, 1f, 0, 0, default);
    // Simpler executions win near-ties.
    float TypePenalty(ThrowType t) => t switch
    {
        ThrowType.Stand => 0f,
        ThrowType.Crouch => 1f,
        ThrowType.JumpThrow => 3f,
        _ => 4f,
    };

    (float Err, float Pitch, float Yaw, Vector3 RestPos) Simulate(ThrowType type, float strength, float pitch, float yaw)
    {
        var eye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(type));
        var (pos, vel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, yaw, pitch, type, strength), constants);
        var sim = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants);
        return (Vector3.Distance(sim.RestPoint, target), pitch, yaw, sim.RestPoint);
    }

    var samples = new List<(float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos)>();
    void CoarseSweep(float yawRange, float yawStep)
    {
        foreach (var type in types)
        {
            foreach (var strength in strengths)
            {
                for (var yaw = baseYaw - yawRange; yaw <= baseYaw + yawRange; yaw += yawStep)
                {
                    for (var pitch = -86f; pitch <= 2f; pitch += 4f)
                    {
                        var s = Simulate(type, strength, pitch, yaw);
                        samples.Add((s.Err, type, strength, pitch, yaw, s.RestPos));
                    }
                }
            }
        }
    }

    // Bounce landscapes are non-convex: refining only the single best coarse
    // cell strands the answer in one basin. Refine the best N cells that are
    // meaningfully apart instead, and escalate to wall-bank yaws if needed.
    void RefineTopCandidates(int topN)
    {
        var accepted = new List<(float Err, ThrowType Type, float Strength, float Pitch, float Yaw, Vector3 RestPos)>();
        foreach (var c in samples.OrderBy(s => s.Err + TypePenalty(s.Type)))
        {
            if (accepted.Count >= topN) { break; }
            if (accepted.Any(a => a.Type == c.Type && a.Strength == c.Strength &&
                                  MathF.Abs(a.Yaw - c.Yaw) < 6f && MathF.Abs(a.Pitch - c.Pitch) < 8f))
            {
                continue;
            }
            accepted.Add(c);
        }
        foreach (var c in accepted)
        {
            var local = c;
            foreach (var (range, step) in new[] { (2.2f, 0.4f), (0.45f, 0.08f) })
            {
                for (var yaw = local.Yaw - range; yaw <= local.Yaw + range; yaw += step)
                {
                    for (var pitch = local.Pitch - range; pitch <= local.Pitch + range; pitch += step)
                    {
                        var s = Simulate(c.Type, c.Strength, pitch, yaw);
                        if (s.Err < local.Err)
                        {
                            local = (s.Err, c.Type, c.Strength, s.Pitch, s.Yaw, s.RestPos);
                        }
                    }
                }
            }
            if (local.Err + TypePenalty(local.Type) < best.Err + TypePenalty(best.Type))
            {
                best = local;
            }
        }
    }

    // quick: aimed sweep with bank-shot escalation; deep: exhaustive 360
    // sweep at finer steps for the best achievable answer.
    if (options.GetValueOrDefault("mode", "quick") == "deep")
    {
        CoarseSweep(180f, 2f);
        RefineTopCandidates(20);
    }
    else
    {
        CoarseSweep(32f, 2.5f);
        RefineTopCandidates(10);
        if (best.Err > 8f)
        {
            samples.Clear();
            CoarseSweep(90f, 3f);
            RefineTopCandidates(10);
        }
    }

    var tolerance = float.Parse(options.GetValueOrDefault("tolerance", "32"), CultureInfo.InvariantCulture);
    var aim = AimReferencePoint(collider, feet, best.Type, best.Pitch, best.Yaw);
    var bestEye = feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(best.Type));
    var (initPos, initVel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(bestEye, best.Yaw, best.Pitch, best.Type, best.Strength), constants);
    var result = new
    {
        found = best.Err <= tolerance,
        err = best.Err,
        pitch = best.Pitch,
        yaw = best.Yaw,
        type = best.Type.ToString(),
        strength = best.Strength,
        rest = new[] { best.RestPos.X, best.RestPos.Y, best.RestPos.Z },
        aim = new[] { aim.X, aim.Y, aim.Z },
        initpos = new[] { initPos.X, initPos.Y, initPos.Z },
        initvel = new[] { initVel.X, initVel.Y, initVel.Z },
    };
    Console.WriteLine(JsonSerializer.Serialize(result));
    return 0;
}

// Where to draw the in-sky aim X: the first surface the aim ray hits, pulled
// 24u toward the eye so the marker is never buried inside the geometry the
// player must line their crosshair against.
static Vector3 AimReferencePoint(TriangleCollider collider, Vector3 feet, ThrowType type, float pitchDeg, float yawDeg)
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

static int Validate(Dictionary<string, string> options)
{
    // --markers <file.json>: run the full validation once per saved in-game
    // marker (written by the plugin's !mark command), using each marker as the
    // target. One report per marker.
    if (options.TryGetValue("markers", out var markersPath))
    {
        var markers = JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(markersPath))!;
        if (markers.Count == 0)
        {
            Console.Error.WriteLine("no markers in file; use !mark <name> in-game first");
            return 1;
        }
        Console.WriteLine($"validating {markers.Count} marker(s): {string.Join(", ", markers.Keys)}");
        var failures = 0;
        foreach (var (name, pos) in markers)
        {
            Console.WriteLine($"=== marker '{name}' ({pos[0]:F0},{pos[1]:F0},{pos[2]:F0}) ===");
            var sub = new Dictionary<string, string>(options);
            sub.Remove("markers");
            sub.Remove("getpos");
            sub["target"] = FormattableString.Invariant($"{pos[0]},{pos[1]},{pos[2]}");
            failures += Validate(sub) == 0 ? 0 : 1;
        }
        return failures == 0 ? 0 : 1;
    }

    if (!options.ContainsKey("attrs"))
    {
        options["attrs"] = "Default,default,EntitySolid";
    }
    var (mesh, _, _, attributeFilter) = LoadCommon(options);
    var navAreas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(Require(options, "nav")))!;
    var constants = LoadConstants(options);

    Vector3 target;
    bool hasTargetZ;
    if (options.TryGetValue("getpos", out var gp))
    {
        var (eye, _, _) = ParseGetPos(gp);
        target = eye - new Vector3(0, 0, 64);
        hasTargetZ = true;
    }
    else
    {
        var parts = Require(options, "target").Split(',', StringSplitOptions.TrimEntries);
        target = new Vector3(
            float.Parse(parts[0], CultureInfo.InvariantCulture),
            float.Parse(parts[1], CultureInfo.InvariantCulture),
            parts.Length > 2 ? float.Parse(parts[2], CultureInfo.InvariantCulture) : 0);
        hasTargetZ = parts.Length > 2;
    }
    var tolerance = float.Parse(options.GetValueOrDefault("tolerance", "80"), CultureInfo.InvariantCulture);
    var limit = int.Parse(options.GetValueOrDefault("limit", "0"), CultureInfo.InvariantCulture);
    var passRadius = float.Parse(options.GetValueOrDefault("pass", "3"), CultureInfo.InvariantCulture);
    var calibDir = options.GetValueOrDefault(
        "calib", Environment.GetEnvironmentVariable("SMOKESOLVER_CALIB_DIR") ?? "data/calib");
    var dryRun = options.ContainsKey("dry-run");

    Console.WriteLine($"solving target ({target.X:F0},{target.Y:F0}{(hasTargetZ ? $",{target.Z:F0}" : "")}) tolerance {tolerance:F0}u ...");
    var started = System.Diagnostics.Stopwatch.StartNew();
    var solve = SolveForTarget(mesh, attributeFilter, navAreas, target, hasTargetZ, null, 3100f, tolerance, constants);
    var lineups = limit > 0 ? solve.Lineups.Take(limit).ToList() : solve.Lineups;
    Console.WriteLine($"{solve.Lineups.Count} lineups solved in {started.Elapsed.TotalSeconds:F0}s ({lineups.Count} selected, {solve.OriginCount} origins)");
    if (lineups.Count == 0)
    {
        return 1;
    }

    var collider = new TriangleCollider(mesh, solve.RegionMin, solve.RegionMax, mesh.GrenadeSolidFilter());
    var plans = lineups.Select((l, i) =>
    {
        var eye = l.Feet + new Vector3(0, 0, GrenadeTrajectory.EyeHeight(l.Type));
        var (pos, vel) = GrenadeTrajectory.DeriveInitial(new ThrowSpec(eye, l.YawDeg, l.PitchDeg, l.Type, l.Strength), constants);
        var predicted = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants);
        return new ValidatePlan(i, l, pos, vel, predicted.RestPoint, predicted.Bounces);
    }).ToList();

    if (dryRun)
    {
        foreach (var p in plans.Take(20))
        {
            Console.WriteLine($"  [{p.Index}] {p.Lineup.Type} s={p.Lineup.Strength:F1} pos ({p.Pos.X:F0},{p.Pos.Y:F0},{p.Pos.Z:F0}) vel ({p.Vel.X:F0},{p.Vel.Y:F0},{p.Vel.Z:F0}) -> predicted rest ({p.PredictedRest.X:F0},{p.PredictedRest.Y:F0},{p.PredictedRest.Z:F0})");
        }
        Console.WriteLine($"dry run: {plans.Count} throws planned, nothing submitted");
        return 0;
    }

    var requestPath = Path.Combine(calibDir, "request.json");
    var capturesPath = Path.Combine(calibDir, "captures.jsonl");
    var tailer = new CaptureTailer(capturesPath);
    var baseline = tailer.InitializeAtEnd();
    Console.WriteLine($"submitting {plans.Count} throws to {requestPath} (captures baseline offset {baseline}) ...");

    // Batches of 6 per request file: the plugin polls every 8 ticks (~1/8s)
    // and launches the whole batch in one tick, so throughput is bounded by
    // poll cadence rather than per-throw round trips.
    var submitted = 0;
    var stopPath = Path.Combine(calibDir, "stop-request");
    File.Delete(stopPath);
    foreach (var batch in plans.Chunk(6))
    {
        if (File.Exists(stopPath))
        {
            File.Delete(stopPath);
            Console.WriteLine($"stopped by user request after {submitted}/{plans.Count}");
            break;
        }
        RequestFile.WriteAtomic(requestPath, JsonSerializer.Serialize(new
        {
            throws = batch.Select(p => new
            {
                pos = new[] { p.Pos.X, p.Pos.Y, p.Pos.Z },
                vel = new[] { p.Vel.X, p.Vel.Y, p.Vel.Z },
                predict = new[] { p.PredictedRest.X, p.PredictedRest.Y, p.PredictedRest.Z },
                // Shown in server chat by the plugin when a spectator is connected.
                note = $"#{p.Index + 1}/{plans.Count} {p.Lineup.Type} {ClickName(p.Lineup.Strength)} {p.PredictedBounces}b -> predict ({p.PredictedRest.X:F0},{p.PredictedRest.Y:F0},{p.PredictedRest.Z:F0})",
            }).ToArray(),
        }));
        var waited = 0;
        while (File.Exists(requestPath) && waited < 15000)
        {
            Thread.Sleep(50);
            waited += 50;
        }
        if (File.Exists(requestPath))
        {
            Console.Error.WriteLine($"  batch at {batch[0].Index} not consumed after 15s; is the rig server running with the plugin loaded?");
            File.Delete(requestPath);
            continue;
        }
        submitted += batch.Length;
        if (submitted / 25 != (submitted - batch.Length) / 25)
        {
            Console.WriteLine($"  submitted {submitted}/{plans.Count}");
        }
        // ~4 throws/sec keeps concurrent smoke volumes (~20s lifetime) under
        // control so the server stays smooth for anyone spectating.
        Thread.Sleep(1500);
    }

    static string ClickName(float strength) =>
        strength >= 0.99f ? "left" : strength >= 0.49f ? "mid" : "right";
    Console.WriteLine($"all {submitted} submitted; waiting for captures (smokes persist ~20s each) ...");

    // Captures arrive when each projectile despawns; match them back to plans
    // by initial position + velocity (synthetic throws echo them exactly).
    var matches = new Dictionary<int, JsonElement>();
    var idleMs = 0;
    while (matches.Count < submitted && idleMs < 120000)
    {
        Thread.Sleep(2000);
        idleMs += 2000;
        foreach (var line in tailer.ReadNewLines())
        {
            JsonElement c;
            float[] s, v;
            try
            {
                c = JsonSerializer.Deserialize<JsonElement>(line);
                s = c.GetProperty("start").EnumerateArray().Select(e => e.GetSingle()).ToArray();
                v = c.GetProperty("velocity").EnumerateArray().Select(e => e.GetSingle()).ToArray();
            }
            catch (JsonException)
            {
                Console.Error.WriteLine("  skipping malformed capture line");
                continue;
            }
            foreach (var p in plans)
            {
                if (matches.ContainsKey(p.Index))
                {
                    continue;
                }
                if (MathF.Abs(s[0] - p.Pos.X) < 0.5f && MathF.Abs(s[1] - p.Pos.Y) < 0.5f && MathF.Abs(s[2] - p.Pos.Z) < 0.5f &&
                    MathF.Abs(v[0] - p.Vel.X) < 0.5f && MathF.Abs(v[1] - p.Vel.Y) < 0.5f && MathF.Abs(v[2] - p.Vel.Z) < 0.5f)
                {
                    matches[p.Index] = c;
                    idleMs = 0;
                    break;
                }
            }
        }

        if (matches.Count > 0 && matches.Count % 50 == 0)
        {
            Console.WriteLine($"  matched {matches.Count}/{submitted}");
        }
    }
    Console.WriteLine($"matched {matches.Count}/{submitted} captures");

    var results = new List<ValidateRow>();
    var errors = new List<float>();
    var withinPass = 0;
    var within8 = 0;
    var notDetonated = 0;
    foreach (var p in plans)
    {
        if (!matches.TryGetValue(p.Index, out var c))
        {
            continue;
        }
        var r = c.GetProperty("rest").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        var real = new Vector3(r[0], r[1], r[2]);
        var detonated = c.GetProperty("detonated").GetBoolean();
        if (!detonated)
        {
            notDetonated++;
        }
        var err = Vector3.Distance(real, p.PredictedRest);
        errors.Add(err);
        if (err <= passRadius)
        {
            withinPass++;
        }
        if (err <= 8f)
        {
            within8++;
        }

        // Auto-diagnosis: replay the sim per tick against the captured real
        // flight, count real bounces (velocity discontinuities), and classify
        // the first divergence so misses arrive pre-triaged.
        var samples = c.GetProperty("samples").EnumerateArray()
            .Select(s => s.EnumerateArray().Select(e => e.GetSingle()).ToArray()).ToList();
        var simTicks = new List<(Vector3 Position, Vector3 Velocity)>();
        GrenadeTrajectory.SimulateExactRaw(collider, p.Pos, p.Vel, constants, tickTrace: simTicks);
        var realBounces = 0;
        for (var i = 1; i < samples.Count; i++)
        {
            var dvx = samples[i][4] - samples[i - 1][4];
            var dvy = samples[i][5] - samples[i - 1][5];
            var dvz = samples[i][6] - samples[i - 1][6];
            if (samples[i][4] == 0 && samples[i][5] == 0 && samples[i][6] == 0)
            {
                break;
            }
            if (MathF.Abs(dvx) > 0.5f || MathF.Abs(dvy) > 0.5f || MathF.Abs(dvz + 5f) > 0.5f)
            {
                realBounces++;
            }
        }
        var divergenceTick = -1;
        // Sim rest ends its tick trace; if the flights matched to that point
        // but the rests differ, the sim stopped where the real one kept going
        // (or vice versa) - that is a rest mismatch, not a tracked flight.
        var divergenceClass = err > 8f ? "REST-MISMATCH" : "TRACKED";
        for (var i = 0; i < Math.Min(simTicks.Count, samples.Count); i++)
        {
            var realPos = new Vector3(samples[i][1], samples[i][2], samples[i][3]);
            if (Vector3.Distance(simTicks[i].Position, realPos) > 8f)
            {
                divergenceTick = i;
                var realVel = new Vector3(samples[i][4], samples[i][5], samples[i][6]);
                var prevRealVel = i > 0 ? new Vector3(samples[i - 1][4], samples[i - 1][5], samples[i - 1][6]) : p.Vel;
                var rdv = realVel - prevRealVel;
                var realBounced = MathF.Abs(rdv.X) > 0.5f || MathF.Abs(rdv.Y) > 0.5f || MathF.Abs(rdv.Z + 5f) > 0.5f;
                var simVel = simTicks[i].Velocity;
                var prevSimVel = i > 0 ? simTicks[i - 1].Velocity : p.Vel;
                var sdv = simVel - prevSimVel;
                var simBounced = MathF.Abs(sdv.X) > 0.5f || MathF.Abs(sdv.Y) > 0.5f || MathF.Abs(sdv.Z + 5f) > 0.5f;
                divergenceClass = realBounced && !simBounced ? "MISSED-BOUNCE"
                    : simBounced && !realBounced ? "PHANTOM-BOUNCE"
                    : simBounced && realBounced ? "BOUNCE-MISMATCH"
                    : "DRIFT";
                break;
            }
        }

        results.Add(new ValidateRow(
            p.Index,
            p.Lineup.Type.ToString(),
            p.Lineup.Strength,
            p.Lineup.Stability,
            new[] { p.Lineup.Feet.X, p.Lineup.Feet.Y, p.Lineup.Feet.Z },
            p.Lineup.YawDeg,
            p.Lineup.PitchDeg,
            p.PredictedBounces,
            realBounces,
            new[] { p.Pos.X, p.Pos.Y, p.Pos.Z },
            new[] { p.Vel.X, p.Vel.Y, p.Vel.Z },
            new[] { p.PredictedRest.X, p.PredictedRest.Y, p.PredictedRest.Z },
            new[] { real.X, real.Y, real.Z },
            detonated,
            err,
            Vector2.Distance(new Vector2(real.X, real.Y), new Vector2(solve.Target.X, solve.Target.Y)),
            divergenceTick,
            divergenceClass));
    }

    if (errors.Count == 0)
    {
        Console.Error.WriteLine("no captures matched; nothing to grade");
        return 1;
    }
    errors.Sort();
    var summary = new
    {
        lineups = plans.Count,
        submitted,
        matched = matches.Count,
        notDetonated,
        passRadius,
        withinPass,
        within8,
        errMedian = errors[errors.Count / 2],
        errMean = errors.Average(),
        errP90 = errors[(int)(errors.Count * 0.9)],
        errMax = errors[^1],
    };
    Console.WriteLine($"predicted-vs-real rest error: median {summary.errMedian:F1}u  mean {summary.errMean:F1}u  p90 {summary.errP90:F1}u  max {summary.errMax:F1}u");
    Console.WriteLine($"within {passRadius:F0}u: {withinPass}/{errors.Count} ({100.0 * withinPass / errors.Count:F0}%)   within 8u: {within8}/{errors.Count} ({100.0 * within8 / errors.Count:F0}%)   failed to detonate: {notDetonated}");
    Console.WriteLine("worst 10 (pre-triaged gap candidates):");
    foreach (var row in results.OrderByDescending(r => r.ErrPredicted).Take(10))
    {
        Console.WriteLine($"  [{row.Index}] err {row.ErrPredicted:F0}u {row.DivergenceClass}@tick{row.DivergenceTick}  {row.Type} s={row.Strength:F1} stab={row.Stability:P0} {row.PredictedBounces}b(sim)/{row.RealBounces}b(real)  predicted ({row.PredictedRest[0]:F0},{row.PredictedRest[1]:F0},{row.PredictedRest[2]:F0}) real ({row.RealRest[0]:F0},{row.RealRest[1]:F0},{row.RealRest[2]:F0})");
    }

    Directory.CreateDirectory("data/validation");
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var reportPath = $"data/validation/{mesh.MapName}-{stamp}.json";
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new
    {
        map = mesh.MapName,
        build = mesh.GameBuildId,
        timestamp = DateTime.Now.ToString("o"),
        target = new[] { solve.Target.X, solve.Target.Y, solve.Target.Z },
        tolerance,
        summary,
        results,
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"wrote {reportPath}");

    var md = new StringBuilder();
    md.AppendLine($"# Validation run: {mesh.MapName} @ ({solve.Target.X:F0},{solve.Target.Y:F0},{solve.Target.Z:F0})");
    md.AppendLine();
    md.AppendLine($"Build {mesh.GameBuildId}, {DateTime.Now:yyyy-MM-dd HH:mm}, tolerance {tolerance:F0}u, pass radius {passRadius:F0}u.");
    md.AppendLine($"{plans.Count} lineups solved, {submitted} thrown, {matches.Count} captured, {notDetonated} failed to detonate.");
    md.AppendLine();
    md.AppendLine($"Overall predicted-vs-real rest error: median {summary.errMedian:F1}u, mean {summary.errMean:F1}u, p90 {summary.errP90:F1}u, max {summary.errMax:F1}u.");
    md.AppendLine($"Within {passRadius:F0}u: {withinPass}/{errors.Count} ({100.0 * withinPass / errors.Count:F0}%); within 8u: {within8}/{errors.Count} ({100.0 * within8 / errors.Count:F0}%).");
    md.AppendLine();
    md.AppendLine("| Segment | n | median | p90 | max | <=pass | <=8u |");
    md.AppendLine("|---|---|---|---|---|---|---|");
    void Segment(string name, List<ValidateRow> rows)
    {
        if (rows.Count == 0)
        {
            return;
        }
        var es = rows.Select(r => r.ErrPredicted).OrderBy(e => e).ToList();
        md.AppendLine($"| {name} | {es.Count} | {es[es.Count / 2]:F1}u | {es[(int)(es.Count * 0.9)]:F1}u | {es[^1]:F1}u | {100.0 * es.Count(e => e <= passRadius) / es.Count:F0}% | {100.0 * es.Count(e => e <= 8f) / es.Count:F0}% |");
    }
    foreach (var t in new[] { "Stand", "Crouch", "JumpThrow", "CrouchJumpThrow", "RunJumpThrow" })
    {
        Segment(t, results.Where(r => r.Type == t).ToList());
    }
    Segment("bounces 0-4", results.Where(r => r.PredictedBounces <= 4).ToList());
    Segment("bounces 5-30", results.Where(r => r.PredictedBounces is > 4 and <= 30).ToList());
    Segment("bounces >30", results.Where(r => r.PredictedBounces > 30).ToList());
    Segment("stability 100%", results.Where(r => r.Stability >= 0.99f).ToList());
    Segment("stability <100%", results.Where(r => r.Stability < 0.99f).ToList());
    md.AppendLine();
    md.AppendLine("## Worst 10 (pre-triaged)");
    md.AppendLine();
    md.AppendLine("| # | err | class | tick | type | click | stab | sim/real bounces | predicted rest | real rest |");
    md.AppendLine("|---|---|---|---|---|---|---|---|---|---|");
    foreach (var row in results.OrderByDescending(r => r.ErrPredicted).Take(10))
    {
        var click = row.Strength >= 0.99f ? "left" : row.Strength >= 0.49f ? "mid" : "right";
        md.AppendLine($"| {row.Index} | {row.ErrPredicted:F0}u | {row.DivergenceClass} | {row.DivergenceTick} | {row.Type} | {click} | {row.Stability:P0} | {row.PredictedBounces}/{row.RealBounces} | ({row.PredictedRest[0]:F0},{row.PredictedRest[1]:F0},{row.PredictedRest[2]:F0}) | ({row.RealRest[0]:F0},{row.RealRest[1]:F0},{row.RealRest[2]:F0}) |");
    }
    md.AppendLine();
    md.AppendLine("Divergence class counts across misses (>8u): " + string.Join(", ",
        results.Where(r => r.ErrPredicted > 8f).GroupBy(r => r.DivergenceClass).OrderByDescending(g => g.Count()).Select(g => $"{g.Key} {g.Count()}")));
    var mdPath = $"data/validation/{mesh.MapName}-{stamp}.md";
    File.WriteAllText(mdPath, md.ToString());
    Console.WriteLine($"wrote {mdPath}");
    return 0;
}

// Textured render-mesh export for the 3D viewer: VRF walks the world resource
// (worldnodes, aggregates, entity models) and writes a GLB with materials and
// textures resolved from the map VPK plus the game's mounted search paths.
static int ExportGltf(Dictionary<string, string> options)
{
    var vpkPath = Path.GetFullPath(Require(options, "vpk"));
    var outPath = options.GetValueOrDefault("out", "data/de_dust2.glb");
    var package = new SteamDatabase.ValvePak.Package();
    package.Read(vpkPath);
    var entry = (package.Entries.TryGetValue("vwrld_c", out var worlds) ? worlds : [])
        .FirstOrDefault()
        ?? throw new FileNotFoundException($"no world resource (.vwrld_c) inside {vpkPath}");
    Console.WriteLine($"exporting {entry.GetFullPath()} with materials...");
    package.ReadEntry(entry, out var raw);
    var resource = new ValveResourceFormat.Resource { FileName = entry.GetFullPath() };
    resource.Read(new MemoryStream(raw));

    using var loader = new ValveResourceFormat.IO.GameFileLoader(package, vpkPath);
    var exporter = new ValveResourceFormat.IO.GltfModelExporter(loader)
    {
        ExportMaterials = true,
        AdaptTextures = true,
        ProgressReporter = new Progress<string>(s => Console.WriteLine($"  {s}")),
    };
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
    exporter.Export(resource, Path.GetFullPath(outPath), default);
    Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length / 1e6:F0} MB)");
    return 0;
}

sealed record NavAreaJson(uint Id, float[][] Corners);

static class MeshPayloadCache
{
    public static byte[]? Bytes;
}

// The plugin claims request.json by rename, so it must only ever see a
// complete file: write to a temp sibling and rename into place (atomic on
// the same filesystem).
static class RequestFile
{
    public static void WriteAtomic(string requestPath, string json)
    {
        var tmp = requestPath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, requestPath, overwrite: true);
    }
}

// Tail captures.jsonl without re-reading the whole growing file: remember the
// byte offset, and only consume newline-terminated lines so a read that lands
// mid-append defers the partial line to the next poll instead of crashing.
sealed class CaptureTailer(string path)
{
    long _offset;
    readonly List<string> _pending = [];

    public long InitializeAtEnd()
    {
        if (File.Exists(path))
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _offset = fs.Length;
        }
        return _offset;
    }

    public IReadOnlyList<string> ReadNewLines()
    {
        _pending.Clear();
        if (!File.Exists(path))
        {
            return _pending;
        }
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length < _offset)
        {
            _offset = 0; // the plugin rotated the file; start over on the fresh one
        }
        if (fs.Length == _offset)
        {
            return _pending;
        }
        fs.Seek(_offset, SeekOrigin.Begin);
        using var reader = new StreamReader(fs);
        var chunk = reader.ReadToEnd();
        var consumed = 0;
        int nl;
        while ((nl = chunk.IndexOf('\n', consumed)) >= 0)
        {
            var line = chunk[consumed..nl].Trim();
            if (line.Length > 0)
            {
                _pending.Add(line);
            }
            consumed = nl + 1;
        }
        _offset += System.Text.Encoding.UTF8.GetByteCount(chunk[..consumed]);
        return _pending;
    }
}

sealed record ValidatePlan(int Index, Lineup Lineup, Vector3 Pos, Vector3 Vel, Vector3 PredictedRest, int PredictedBounces);

sealed record ValidateRow(
    int Index,
    string Type,
    float Strength,
    float Stability,
    float[] Feet,
    float Yaw,
    float Pitch,
    int PredictedBounces,
    int RealBounces,
    float[] Pos,
    float[] Vel,
    float[] PredictedRest,
    float[] RealRest,
    bool Detonated,
    float ErrPredicted,
    float ErrTarget,
    int DivergenceTick,
    string DivergenceClass);

sealed record TargetSolve(
    Vector3 Target,
    int OriginCount,
    List<int[]> Coverage,
    List<Lineup> Lineups,
    Vector3 RegionMin,
    Vector3 RegionMax);

