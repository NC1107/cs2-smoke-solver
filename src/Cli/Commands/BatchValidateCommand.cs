using System.Globalization;
using System.Text.Json;
using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.ValidateCommand;

namespace SmokeSolver.Cli;

/// <summary>
/// Unattended accuracy sweep across maps: for each map, switch the rig server
/// onto it, pick a spread of targets (saved in-game markers first, nav-sampled
/// spots to top up), and run the full validate loop (solve, throw every lineup
/// in-game, grade, triage) per target. One report per target lands in
/// data/validation/ and the dashboard index is rebuilt at the end, so a single
/// invocation produces a browsable accuracy picture for the whole pool.
/// </summary>
public static class BatchValidateCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var maps = Require(options, "maps").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var targetsPerMap = int.Parse(options.GetValueOrDefault("targets-per-map", "3"), CultureInfo.InvariantCulture);
        var fuzz = int.Parse(options.GetValueOrDefault("fuzz", "0"), CultureInfo.InvariantCulture);
        var batch = options.GetValueOrDefault("batch", $"batch-{DateTime.Now:yyyyMMdd-HHmm}");
        var dataDir = options.GetValueOrDefault("data", "data");
        var calibDir = options.GetValueOrDefault(
            "calib", Environment.GetEnvironmentVariable("SMOKESOLVER_CALIB_DIR") ?? "data/calib");

        var failures = 0;
        var ran = 0;
        foreach (var map in maps)
        {
            var geo = Path.Combine(dataDir, $"{map}.s2geo");
            var navPath = Path.Combine(dataDir, $"{map}.navareas.json");
            if (!File.Exists(geo) || !File.Exists(navPath))
            {
                Console.Error.WriteLine($"[{map}] skipped: missing {geo} or {navPath}");
                failures++;
                continue;
            }
            var targets = options.ContainsKey("no-markers")
                ? []
                : PickTargets(map, navPath, calibDir, targetsPerMap);
            if (fuzz > 0)
            {
                // An explicit seed makes repeated same-night iterations explore
                // different random targets; without one the seed is the date,
                // so a re-run reproduces the same batch.
                var seed = options.TryGetValue("seed", out var seedRaw)
                    ? StringComparer.Ordinal.GetHashCode($"{map}|{seedRaw}")
                    : StringComparer.Ordinal.GetHashCode($"{map}|{DateTime.Now:yyyyMMdd}");
                targets.AddRange(FuzzTargets(navPath, fuzz, seed));
            }
            if (targets.Count == 0)
            {
                Console.Error.WriteLine($"[{map}] skipped: no usable targets");
                failures++;
                continue;
            }
            Console.WriteLine($"=== {map}: {targets.Count} target(s): {string.Join(", ", targets.Select(t => t.Name))} ===");
            if (!options.ContainsKey("no-changelevel") && !ChangeLevel(map, calibDir))
            {
                Console.Error.WriteLine($"[{map}] rig server did not consume the changelevel request - is it running with the plugin loaded?");
                return 1;
            }

            foreach (var (name, pos) in targets)
            {
                Console.WriteLine($"--- {map} / {name} ({pos[0]:F0},{pos[1]:F0},{pos[2]:F0}) ---");
                var sub = new Dictionary<string, string>(options)
                {
                    ["geo"] = geo,
                    ["nav"] = navPath,
                    ["target"] = FormattableString.Invariant($"{pos[0]},{pos[1]},{pos[2]}"),
                    ["name"] = name,
                    ["batch"] = batch,
                    ["calib"] = calibDir,
                };
                sub.Remove("maps");
                sub.Remove("targets-per-map");
                ran++;
                if (ValidateCommand.Run(sub) != 0)
                {
                    failures++;
                }
            }
        }
        RebuildValidationIndex(Path.Combine(dataDir, "validation"));
        Console.WriteLine($"batch '{batch}': {ran} target run(s), {failures} failure(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Targets for one map: every saved in-game marker (curated spots beat
    /// anything synthetic), topped up to the requested count with nav-area
    /// centroids chosen by farthest-point sampling - the spread maximizes
    /// scenario diversity (ranges, elevations, geometry) instead of clustering
    /// around wherever the first random pick landed. Seeded by map name so
    /// successive batches test the same spots and stay comparable over time.
    /// </summary>
    static List<(string Name, float[] Pos)> PickTargets(string map, string navPath, string calibDir, int count)
    {
        var targets = new List<(string, float[])>();
        var markersPath = Path.Combine(calibDir, $"markers-{map}.json");
        if (File.Exists(markersPath))
        {
            try
            {
                foreach (var (name, pos) in JsonSerializer.Deserialize<Dictionary<string, float[]>>(File.ReadAllText(markersPath)) ?? [])
                {
                    if (pos is { Length: 3 })
                    {
                        targets.Add((name, pos));
                    }
                }
            }
            catch (JsonException e)
            {
                Console.Error.WriteLine($"[{map}] ignoring malformed markers file: {e.Message}");
            }
        }
        if (targets.Count >= count)
        {
            return targets;
        }

        var areas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath)) ?? [];
        // Tiny slivers (stair steps, ledge lips) make poor smoke targets and
        // poor scenario anchors; keep areas with some standable footprint.
        var centroids = areas
            .Select(a => a.Corners)
            .Where(c => c.Length >= 3 && Area2D(c) > 4000f)
            .Select(c => new float[]
            {
                c.Average(p => p[0]),
                c.Average(p => p[1]),
                c.Average(p => p[2]),
            })
            .ToList();
        if (centroids.Count == 0)
        {
            return targets;
        }
        var seedIndex = Math.Abs(StringComparer.Ordinal.GetHashCode(map)) % centroids.Count;
        var picked = targets.Select(t => t.Item2).ToList();
        if (picked.Count == 0)
        {
            picked.Add(centroids[seedIndex]);
            targets.Add(($"auto-1", centroids[seedIndex]));
        }
        while (targets.Count < count)
        {
            float[]? best = null;
            var bestScore = -1f;
            foreach (var c in centroids)
            {
                var nearest = picked.Min(p => Dist2D(p, c));
                if (nearest > bestScore)
                {
                    bestScore = nearest;
                    best = c;
                }
            }
            if (best == null || bestScore <= 0f)
            {
                break;
            }
            picked.Add(best);
            targets.Add(($"auto-{targets.Count + 1}", best));
        }
        return targets;
    }

    /// <summary>
    /// Random walkable targets: a nav area picked with probability
    /// proportional to its footprint, then a uniform point inside its
    /// bounding box re-tested against the polygon. Curated markers test the
    /// spots we know about; fuzzing is how the pipeline finds the failure
    /// geometry nobody thought to mark.
    /// </summary>
    static List<(string Name, float[] Pos)> FuzzTargets(string navPath, int count, int seed)
    {
        var areas = (JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath)) ?? [])
            .Select(a => a.Corners)
            .Where(c => c.Length >= 3 && Area2D(c) > 1000f)
            .ToList();
        var targets = new List<(string, float[])>();
        if (areas.Count == 0)
        {
            return targets;
        }
        var rng = new Random(seed);
        var weights = areas.Select(Area2D).ToArray();
        var total = weights.Sum();
        while (targets.Count < count)
        {
            var pick = (float)(rng.NextDouble() * total);
            var idx = 0;
            for (; idx < weights.Length - 1 && pick > weights[idx]; idx++)
            {
                pick -= weights[idx];
            }
            var corners = areas[idx];
            var minX = corners.Min(c => c[0]);
            var maxX = corners.Max(c => c[0]);
            var minY = corners.Min(c => c[1]);
            var maxY = corners.Max(c => c[1]);
            // Rejection-sample the polygon; a handful of tries is plenty for
            // nav shapes, and a pathological sliver just falls through to the
            // next weighted pick.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var x = minX + (float)rng.NextDouble() * (maxX - minX);
                var y = minY + (float)rng.NextDouble() * (maxY - minY);
                if (PointInPolygon(corners, x, y))
                {
                    targets.Add(($"fuzz-{targets.Count + 1}", new[] { x, y, corners.Average(c => c[2]) }));
                    break;
                }
            }
        }
        return targets;
    }

    static bool PointInPolygon(float[][] corners, float x, float y)
    {
        var inside = false;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            var (xi, yi) = (corners[i][0], corners[i][1]);
            var (xj, yj) = (corners[j][0], corners[j][1]);
            if (yi > y != yj > y && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
            {
                inside = !inside;
            }
        }
        return inside;
    }

    static float Area2D(float[][] corners)
    {
        var area = 0f;
        for (int i = 0, j = corners.Length - 1; i < corners.Length; j = i++)
        {
            area += (corners[j][0] + corners[i][0]) * (corners[j][1] - corners[i][1]);
        }
        return MathF.Abs(area) / 2f;
    }

    static float Dist2D(float[] a, float[] b)
    {
        var dx = a[0] - b[0];
        var dy = a[1] - b[1];
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Switches the rig server to a map through the plugin's request channel
    /// and waits for the request to be consumed (the plugin only polls while a
    /// map is live, so consumption doubles as the liveness check), then gives
    /// the new map time to load and re-apply practice settings.
    /// </summary>
    static bool ChangeLevel(string map, string calibDir)
    {
        var requestPath = Path.Combine(calibDir, "request.json");
        RequestFile.WriteAtomic(requestPath, JsonSerializer.Serialize(new { cmd = $"changelevel {map}" }));
        var waited = 0;
        while (File.Exists(requestPath) && waited < 20000)
        {
            Thread.Sleep(250);
            waited += 250;
        }
        if (File.Exists(requestPath))
        {
            File.Delete(requestPath);
            return false;
        }
        Console.WriteLine($"[{map}] changelevel accepted; waiting for the map to settle...");
        // Map load plus the plugin's deferred practice-settings/self-test
        // timers. The solve that follows takes minutes anyway; this only has
        // to cover the window where a throw request could hit a loading map.
        Thread.Sleep(25000);
        return true;
    }
}
