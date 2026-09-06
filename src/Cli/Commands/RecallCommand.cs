using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SmokeSolver.Extraction;
using SmokeSolver.Sim;
using SmokeSolver.Solver;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
using static SmokeSolver.Cli.TargetSolver;
namespace SmokeSolver.Cli;

/// <summary>
/// The recall bench: how many of the throw kinds the exhaustive exact search
/// lands from a stand spot the normal solve path also returns. The sweep is a
/// recall filter over an approximation of the map; this is the instrument
/// that says what it drops, per map, so a change to it can be measured.
/// </summary>
public static class RecallCommand
{
    // A throw kind, as a player asks for one: stance, click and run direction.
    // The route (bounce count) is reported separately: the first cs_italy
    // pairs showed the referee landing the same kind by four to eleven
    // bounces, and counting each as a missed lineup would have measured the
    // sweep against chaos nobody would throw. Bounces is 0 in a kind key and
    // the real count in a route key.
    public readonly record struct Kind(ThrowType Type, float Strength, float RunYawOffsetDeg, int Bounces);

    public static Kind KindOf(Lineup l) => new(l.Type, l.Strength, l.RunYawOffsetDeg, 0);
    public static Kind RouteOf(Lineup l) => new(l.Type, l.Strength, l.RunYawOffsetDeg, l.Bounces);

    public sealed record Tally(int Both, int ExactOnly, int SweepOnly)
    {
        public static readonly Tally Zero = new(0, 0, 0);
        public static Tally operator +(Tally a, Tally b) => new(a.Both + b.Both, a.ExactOnly + b.ExactOnly, a.SweepOnly + b.SweepOnly);
        public int ExactLandable => Both + ExactOnly;
        public double Recall => ExactLandable == 0 ? 1.0 : (double)Both / ExactLandable;
    }

    public sealed record Comparison(Tally Kinds, List<Kind> ExactOnly, List<Kind> SweepOnly, Tally Routes, List<Kind> ExactOnlyRoutes);

    // A referee lineup below this stability is not one a person can throw
    // (fewer than two of five aims 0.6 degrees apart still land it), and the
    // first cs_italy pairs were full of them: seven-bounce run-jumps at 0.2.
    // Measuring the sweep against those measures noise. The product's own
    // map-wide floor is the same 0.4.
    public const float ReliableStability = 0.4f;

    /// <summary>
    /// Kinds in both lists, kinds only the referee landed (the sweep's
    /// misses) and kinds only the sweep returned (lattice artefacts, or a
    /// route the 1-degree referee lattice stepped over); the same again per
    /// route. Only referee lineups at or above <paramref name="minStability"/>
    /// count as landable.
    /// </summary>
    public static Comparison Compare(IEnumerable<Lineup> normal, IEnumerable<Lineup> referee, float minStability = ReliableStability)
    {
        var normalList = normal.ToList();
        var refereeList = referee.Where(l => l.Stability >= minStability).ToList();
        var (kinds, exactOnly, sweepOnly) = Diff(normalList, refereeList, KindOf);
        var (routes, exactOnlyRoutes, _) = Diff(normalList, refereeList, RouteOf);
        return new Comparison(kinds, exactOnly, sweepOnly, routes, exactOnlyRoutes);
    }

    static (Tally, List<Kind>, List<Kind>) Diff(List<Lineup> normal, List<Lineup> referee, Func<Lineup, Kind> key)
    {
        var sweep = normal.Select(key).ToHashSet();
        var exact = referee.Select(key).ToHashSet();
        var exactOnly = exact.Except(sweep).OrderBy(k => k.Type).ThenBy(k => k.Strength).ThenBy(k => k.Bounces).ToList();
        var sweepOnly = sweep.Except(exact).OrderBy(k => k.Type).ThenBy(k => k.Strength).ThenBy(k => k.Bounces).ToList();
        return (new Tally(sweep.Intersect(exact).Count(), exactOnly.Count, sweepOnly.Count), exactOnly, sweepOnly);
    }

    // The maps with an in-game validation corpus: the only ones whose solver
    // output has ever been checked against the real game.
    static readonly string[] BenchMaps =
    [
        "cs_italy", "cs_office", "cs_shelter", "de_ancient", "de_anubis", "de_boulder", "de_cache", "de_dust2",
        "de_fachwerk", "de_inferno", "de_mirage", "de_nuke", "de_overpass", "de_train", "de_vertigo",
    ];

    // Nick's A-site bench (technical_debt.md, "The A-site test bench"): the
    // target at the centre of de_dust2's A site and nine spots he calls
    // reasonable lineups. Positions are getpos eye heights; feet are 64u lower,
    // and the ones without a height take the nearest stand spot's floor.
    static readonly Vector3 ASiteTarget = new(1130.38f, 2504.53f, 95.75f);
    static readonly (float X, float Y, float? EyeZ)[] ASiteSpots =
    [
        (1069.05f, 2348.03f, null), (1235.97f, 2348.05f, null), (1235.96f, 2460.91f, null), (1069.03f, 2411.97f, null),
        (1101.04f, 2569.63f, null), (1235.97f, 2561.04f, null), (1300.04f, 2446.28f, 54f), (1300.03f, 2342.97f, 22f), (1004.97f, 2379.97f, 21f),
    ];

    public sealed record BenchOrigin(Vector3 Feet, string Label);
    public sealed record BenchTarget(string Map, string Name, Vector3 Pos, List<BenchOrigin> Origins);

    public static int Run(Dictionary<string, string> options)
    {
        var dataDir = options.GetValueOrDefault("data", "data");
        var maps = options.TryGetValue("maps", out var mapsRaw)
            ? mapsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : BenchMaps;
        var targetCap = int.Parse(options.GetValueOrDefault("targets", "3"), CultureInfo.InvariantCulture);
        var spotsPerTarget = int.Parse(options.GetValueOrDefault("spots", "4"), CultureInfo.InvariantCulture);
        var seed = int.Parse(options.GetValueOrDefault("seed", "1"), CultureInfo.InvariantCulture);
        var tolerance = float.Parse(options.GetValueOrDefault("tolerance", "32"), CultureInfo.InvariantCulture);
        var (minDist, maxDist) = (150f, 1500f);
        var listMisses = options.ContainsKey("list");
        var verbose = options.ContainsKey("verbose");
        var why = options.ContainsKey("why");
        var minStability = float.Parse(options.GetValueOrDefault("min-stability", ReliableStability.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        // The referee depends on physics, the mesh and VerifyExact, none of
        // which a recall hypothesis touches, so it is kept on disk per pair
        // and a later run only pays for the solve path. --refresh-referee
        // after any change to VerifyExact or the exhaustive search itself.
        var refereeDir = Path.Combine(dataDir, "tmp", "recall-referee");
        Directory.CreateDirectory(refereeDir);
        var refreshReferee = options.ContainsKey("refresh-referee");
        var jsonOut = options.GetValueOrDefault("json", "");

        if (options.ContainsKey("mapwide"))
        {
            return RunMapWideTiming(options, dataDir, maps, targetCap, tolerance);
        }

        var grand = Tally.Zero;
        var grandRoutes = Tally.Zero;
        var perMap = new List<(string Map, Tally Tally, double SolveSeconds, int Pairs)>();
        var rows = new List<object>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        foreach (var map in maps)
        {
            var geo = Path.Combine(dataDir, $"{map}.s2geo");
            if (!File.Exists(geo))
            {
                Console.Error.WriteLine($"{map}: no mesh at {geo}, skipped");
                continue;
            }
            var standSpots = MapRegistry.LoadStandSpots(dataDir, map);
            if (standSpots is not { Count: > 0 })
            {
                Console.Error.WriteLine($"{map}: no stand spots, skipped");
                continue;
            }
            var mapOptions = new Dictionary<string, string>(options) { ["geo"] = geo };
            mapOptions.TryAdd("attrs", SingleTargetDefaultAttrs);
            var (mesh, _, _, attributeFilter) = LoadCommon(mapOptions);
            var navAreas = LoadJson<List<NavAreaJson>>(mapOptions.GetValueOrDefault("nav", DefaultNavAreasPath(mapOptions, mesh)), "nav areas");
            var constants = LoadConstants(mapOptions);
            var bench = BenchFor(map, dataDir, standSpots, targetCap, spotsPerTarget, seed, minDist, maxDist);

            var mapTally = Tally.Zero;
            var mapRoutes = Tally.Zero;
            var solveSeconds = 0.0;
            var pairs = 0;
            foreach (var t in bench)
            {
                foreach (var o in t.Origins)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    // The solve path's own time ends where the lattice starts;
                    // the rest of the wall time is the referee's.
                    var latticeAt = double.NaN;
                    Action<string, int> onPhase = (phase, n) =>
                    {
                        if (phase == "exhaustive" && double.IsNaN(latticeAt)) { latticeAt = sw.Elapsed.TotalSeconds; }
                        if (verbose) { Console.WriteLine($"    [{sw.Elapsed.TotalSeconds,6:F1}s] {phase} {n}"); }
                    };
                    var refereePath = Path.Combine(refereeDir, RefereeKey(map, mesh.GameBuildId, t.Pos, o.Feet, tolerance) + ".json");
                    var cached = !refreshReferee && File.Exists(refereePath) ? ReadReferee(refereePath) : null;
                    var solve = SolveForTarget(mesh, attributeFilter, navAreas, t.Pos, hasTargetZ: true,
                        new Vector2(o.Feet.X, o.Feet.Y), 0f, tolerance, constants, onPhase,
                        standSpots: standSpots, exactOrigin: true, originZ: o.Feet.Z, referee: cached is null, refereeKnown: why ? cached : null);
                    var total = sw.Elapsed.TotalSeconds;
                    // A cached referee, or a sweep that found something, means the
                    // lattice never ran for the user: the wall time is all theirs.
                    var seconds = double.IsNaN(latticeAt) || solve.Lineups.Count > 0 && cached is not null ? total : latticeAt;
                    var referee = cached ?? solve.Referee ?? [];
                    if (cached is null)
                    {
                        WriteReferee(refereePath, referee);
                    }
                    var c = Compare(solve.Lineups, referee, minStability);
                    if (why && solve.RefereeNotes is { Count: > 0 })
                    {
                        Console.WriteLine($"  {map} {t.Name} <- {o.Label}:");
                        foreach (var note in solve.RefereeNotes) { Console.WriteLine($"      {note}"); }
                    }
                    mapTally += c.Kinds;
                    mapRoutes += c.Routes;
                    solveSeconds += seconds;
                    pairs++;
                    if (listMisses && (c.ExactOnly.Count > 0 || c.SweepOnly.Count > 0))
                    {
                        Console.WriteLine($"  {map} {t.Name} <- {o.Label} ({o.Feet.X:F0},{o.Feet.Y:F0},{o.Feet.Z:F0}): both {c.Kinds.Both}, exact-only {Describe(c.ExactOnly)}, sweep-only {Describe(c.SweepOnly)}; routes both {c.Routes.Both} exact-only {c.Routes.ExactOnly}; solve {seconds:F0}s, referee {total - seconds:F0}s");
                    }
                    rows.Add(new
                    {
                        map, target = t.Name, targetPos = new[] { t.Pos.X, t.Pos.Y, t.Pos.Z }, origin = o.Label,
                        feet = new[] { o.Feet.X, o.Feet.Y, o.Feet.Z }, both = c.Kinds.Both, exactOnly = c.ExactOnly.Select(k => k.ToString()).ToList(),
                        sweepOnly = c.SweepOnly.Select(k => k.ToString()).ToList(), routesBoth = c.Routes.Both,
                        routesExactOnly = c.ExactOnlyRoutes.Select(k => k.ToString()).ToList(), seconds, refereeSeconds = total - seconds,
                    });
                }
            }
            perMap.Add((map, mapTally, solveSeconds, pairs));
            grand += mapTally;
            grandRoutes += mapRoutes;
            Console.WriteLine(MapLine(map, pairs, mapTally, mapRoutes, solveSeconds / Math.Max(1, pairs)));
        }
        Console.WriteLine(MapLine("TOTAL", perMap.Sum(m => m.Pairs), grand, grandRoutes, perMap.Sum(m => m.SolveSeconds) / Math.Max(1, perMap.Sum(m => m.Pairs))) + $"  ({started.Elapsed.TotalMinutes:F1} min)");
        if (jsonOut.Length > 0)
        {
            File.WriteAllText(jsonOut, JsonSerializer.Serialize(new { tolerance, seed, rows }, new JsonSerializerOptions { WriteIndented = false }));
        }
        return 0;
    }

    // String.GetHashCode is randomised per process; the bench must draw the
    // same spots every run or no two measurements are comparable.
    static int StableHash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s)
        {
            h = (h ^ c) * 16777619u;
        }
        return (int)(h & 0x7fffffff);
    }

    static string RefereeKey(string map, string build, Vector3 target, Vector3 feet, float tolerance) =>
        $"{map}-{build}-{target.X:F0}_{target.Y:F0}_{target.Z:F0}-{feet.X:F0}_{feet.Y:F0}_{feet.Z:F0}-{tolerance:F0}";

    sealed record RefereeRow(float[] Feet, float Yaw, float Pitch, string Type, float Strength, float Run, int Bounces, float[] Rest, float Stability);

    static void WriteReferee(string path, List<Lineup> lineups) =>
        File.WriteAllText(path, JsonSerializer.Serialize(lineups.Select(l => new RefereeRow(
            [l.Feet.X, l.Feet.Y, l.Feet.Z], l.YawDeg, l.PitchDeg, l.Type.ToString(), l.Strength, l.RunYawOffsetDeg, l.Bounces,
            [l.RestPoint.X, l.RestPoint.Y, l.RestPoint.Z], l.Stability)).ToList()));

    static List<Lineup>? ReadReferee(string path)
    {
        try
        {
            var rows = JsonSerializer.Deserialize<List<RefereeRow>>(File.ReadAllText(path)) ?? [];
            return [.. rows.Select(r => new Lineup(new Vector3(r.Feet[0], r.Feet[1], r.Feet[2]), r.Yaw, r.Pitch, Enum.Parse<ThrowType>(r.Type),
                new Vector3(r.Rest[0], r.Rest[1], r.Rest[2]), r.Bounces, 0f, 1, r.Stability, r.Strength, r.Run))];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return null;
        }
    }

    static string MapLine(string map, int pairs, Tally kinds, Tally routes, double secondsPerPair) =>
        $"{map,-12} pairs {pairs,3}  kinds: both {kinds.Both,4} exact-only {kinds.ExactOnly,4} sweep-only {kinds.SweepOnly,3} recall {kinds.Recall * 100,5:F1}%  routes: both {routes.Both,4} exact-only {routes.ExactOnly,4}  solve {secondsPerPair,5:F1}s/pair";

    static string Describe(List<Kind> kinds) => kinds.Count == 0 ? "-" :
        string.Join(" ", kinds.Select(k => $"{k.Type}/{k.Strength:0.#}{(k.RunYawOffsetDeg != 0 ? $"@{k.RunYawOffsetDeg:0}" : "")}{(k.Bounces > 0 ? $"x{k.Bounces}" : "")}"));

    /// <summary>
    /// The map's targets (canonical pro-landing clusters when the map has
    /// them, else the distinct targets its validation runs used) and, for each,
    /// the seeded stand spots at bench distance plus Nick's bench on dust2.
    /// </summary>
    public static List<BenchTarget> BenchFor(string map, string dataDir, IReadOnlyList<StandSpotOrigin> standSpots, int targetCap, int spotsPerTarget, int seed, float minDist, float maxDist)
    {
        var targets = new List<(string Name, Vector3 Pos)>();
        var targetsPath = Path.Combine(dataDir, $"{map}.targets.json");
        if (File.Exists(targetsPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(targetsPath));
            foreach (var t in doc.RootElement.EnumerateArray())
            {
                var p = t.GetProperty("pos");
                targets.Add((t.GetProperty("name").GetString() ?? "?", new Vector3(p[0].GetSingle(), p[1].GetSingle(), p[2].GetSingle())));
            }
        }
        else
        {
            var indexPath = Path.Combine(dataDir, "validation", "index.json");
            if (File.Exists(indexPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(indexPath));
                var seen = new HashSet<(int, int, int)>();
                var runs = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement : doc.RootElement.GetProperty("runs");
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.GetProperty("map").GetString() != map || !run.TryGetProperty("target", out var tp) || tp.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }
                    var pos = new Vector3(tp[0].GetSingle(), tp[1].GetSingle(), tp[2].GetSingle());
                    if (seen.Add(((int)MathF.Round(pos.X / 16), (int)MathF.Round(pos.Y / 16), (int)MathF.Round(pos.Z / 16))))
                    {
                        targets.Add((run.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : $"target {targets.Count + 1}", pos));
                    }
                }
                targets.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            }
        }
        // Deterministic: the file's order for canonical targets, name order for
        // validation targets, and the same seeded draw of spots every run.
        var picked = targets.Take(targetCap).ToList();
        var bench = new List<BenchTarget>();
        foreach (var (name, pos) in picked)
        {
            var rng = new Random(StableHash($"{seed}|{map}|{name}"));
            var inRange = standSpots
                .Where(s => Vector2.Distance(new Vector2(s.Feet.X, s.Feet.Y), new Vector2(pos.X, pos.Y)) is var d && d >= minDist && d <= maxDist)
                .OrderBy(s => s.Feet.X).ThenBy(s => s.Feet.Y).ThenBy(s => s.Feet.Z)
                .ToList();
            var origins = new List<BenchOrigin>();
            for (var i = 0; i < spotsPerTarget && inRange.Count > 0; i++)
            {
                var s = inRange[rng.Next(inRange.Count)];
                inRange.Remove(s);
                origins.Add(new BenchOrigin(s.Feet, $"spot {i + 1}"));
            }
            bench.Add(new BenchTarget(map, name, pos, origins));
        }
        if (map == "de_dust2")
        {
            var origins = new List<BenchOrigin>();
            for (var i = 0; i < ASiteSpots.Length; i++)
            {
                var (x, y, eyeZ) = ASiteSpots[i];
                var z = eyeZ is { } e ? e - 64f
                    : standSpots.OrderBy(s => Vector2.Distance(new Vector2(s.Feet.X, s.Feet.Y), new Vector2(x, y))).First().Feet.Z;
                origins.Add(new BenchOrigin(new Vector3(x, y, z), $"bench {i + 1}"));
            }
            bench.Add(new BenchTarget(map, "A site (bench)", ASiteTarget, origins));
        }
        return bench;
    }

    // The speed gate: cold map-wide solves of the bench targets, so a recall
    // change can be charged for the time it costs the map-wide sweep.
    static int RunMapWideTiming(Dictionary<string, string> options, string dataDir, IReadOnlyList<string> maps, int targetCap, float tolerance)
    {
        var repeats = int.Parse(options.GetValueOrDefault("repeats", "3"), CultureInfo.InvariantCulture);
        double total = 0;
        foreach (var map in maps)
        {
            var geo = Path.Combine(dataDir, $"{map}.s2geo");
            var standSpots = MapRegistry.LoadStandSpots(dataDir, map);
            var mapOptions = new Dictionary<string, string>(options) { ["geo"] = geo };
            mapOptions.TryAdd("attrs", SingleTargetDefaultAttrs);
            var (mesh, _, _, attributeFilter) = LoadCommon(mapOptions);
            var navAreas = LoadJson<List<NavAreaJson>>(mapOptions.GetValueOrDefault("nav", DefaultNavAreasPath(mapOptions, mesh)), "nav areas");
            var constants = LoadConstants(mapOptions);
            var spawnFronts = MapRegistry.SpawnFronts(dataDir, map);
            var bench = BenchFor(map, dataDir, standSpots ?? [], targetCap, 0, 1, 150f, 1500f).Where(t => !t.Name.EndsWith("(bench)", StringComparison.Ordinal)).ToList();
            foreach (var t in bench)
            {
                var times = new List<double>();
                var count = 0;
                for (var r = 0; r < repeats; r++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var solve = SolveForTarget(mesh, attributeFilter, navAreas, t.Pos, hasTargetZ: true, null, 0f, tolerance, constants,
                        standSpots: standSpots, spawnFronts: spawnFronts);
                    times.Add(sw.Elapsed.TotalSeconds);
                    count = solve.Lineups.Count;
                }
                times.Sort();
                var median = times[times.Count / 2];
                total += median;
                Console.WriteLine($"{map,-12} {t.Name,-24} lineups {count,4}  median {median,6:F1}s  ({string.Join(" ", times.Select(x => x.ToString("F1", CultureInfo.InvariantCulture)))})");
            }
        }
        Console.WriteLine($"{"TOTAL",-12} {"sum of medians",-24} {total,6:F1}s");
        return 0;
    }
}
