using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
namespace SmokeSolver.Cli;

/// <summary>
/// Re-simulates every graded throw the rig ever recorded for a map, straight
/// from the engine's own launch position and velocity, and scores the rest
/// point against the real one. No game needed: the validation reports carry
/// the launch state and the real landing, so this is the physics corpus as an
/// offline benchmark. Run it before and after a collision or physics change to
/// see what the change did to ten thousand real throws instead of one.
///
///   replay --geo data/de_dust2.s2geo [--reports data/validation] [--worst 20] [--moved 8]
///          [--nonsolid passbullets] [--nonsolid-groups 2] [--no-edge-tip]
///
/// Experiments already run through it and falsified (2026-09-04, do not
/// retry without new evidence): a sphere hull instead of the box, preferring
/// a face normal when an edge axis wins a near tie, and treating every
/// passbullets group as air.
/// </summary>
public static class ReplayCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(options);
        options.TryAdd("attrs", SingleTargetDefaultAttrs);
        var (mesh, _, _, _) = LoadCommon(options);
        var constants = LoadConstants(options);
        var reportsDir = options.GetValueOrDefault("reports", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", "validation"));
        var worst = int.Parse(options.GetValueOrDefault("worst", "0"), CultureInfo.InvariantCulture);
        var since = options.GetValueOrDefault("since", "");

        var (meshMin, meshMax) = mesh.ComputeBounds();
        var solid = mesh.GrenadeSolidFilter();
        if (options.TryGetValue("nonsolid", out var nonsolidRaw))
        {
            // Experiment knob: treat groups whose interaction layers are exactly
            // this set as transparent to grenades, e.g. --nonsolid passbullets.
            var layers = nonsolidRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var transparent = new bool[mesh.AttributeNames.Length];
            for (var i = 0; i < transparent.Length; i++)
            {
                transparent[i] = mesh.AttributeInteractAs[i].Length > 0 && mesh.AttributeInteractAs[i].All(layers.Contains);
            }
            Console.WriteLine($"nonsolid groups: {string.Join(", ", transparent.Select((t, i) => (t, i)).Where(x => x.t).Select(x => $"#{x.i} {mesh.AttributeNames[x.i]}[{string.Join('+', mesh.AttributeInteractAs[x.i])}]"))}");
            var baseSolid = solid;
            solid = a => baseSolid(a) && !transparent[a];
        }
        if (options.TryGetValue("nonsolid-groups", out var groupsRaw))
        {
            var drop = groupsRaw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(g => byte.Parse(g, CultureInfo.InvariantCulture)).ToHashSet();
            Console.WriteLine($"nonsolid groups by index: {string.Join(", ", drop.Select(i => $"#{i} {mesh.AttributeNames[i]}[{string.Join('+', mesh.AttributeInteractAs[i])}]"))}");
            var baseSolid = solid;
            solid = a => baseSolid(a) && !drop.Contains(a);
        }
        if (options.TryGetValue("bounces-per-tick", out var perTick))
        {
            constants = constants with { BouncesPerTick = int.Parse(perTick, CultureInfo.InvariantCulture) };
            Console.WriteLine($"bounces per tick: {constants.BouncesPerTick}");
        }
        if (options.ContainsKey("no-edge-tip"))
        {
            constants = constants with { EdgeTipping = false };
            Console.WriteLine("edge tipping off");
        }
        var collider = new TriangleCollider(mesh, meshMin, meshMax, solid);

        var rows = new List<(string Report, int Index, Vector3 Pos, Vector3 Vel, Vector3 Real, float Reported)>();
        foreach (var file in Directory.EnumerateFiles(reportsDir, $"{mesh.MapName}-*.json").OrderBy(f => f))
        {
            if (string.CompareOrdinal(Path.GetFileName(file), $"{mesh.MapName}-{since}") < 0)
            {
                continue;
            }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (var r in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                if (!r.TryGetProperty("Pos", out var pos) || !r.TryGetProperty("Vel", out var vel) || !r.TryGetProperty("RealRest", out var real)
                    || !r.TryGetProperty("Detonated", out var det) || !det.GetBoolean())
                {
                    continue;
                }
                rows.Add((Path.GetFileNameWithoutExtension(file), r.GetProperty("Index").GetInt32(), Vec(pos), Vec(vel), Vec(real), r.GetProperty("ErrPredicted").GetSingle()));
            }
        }
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"no graded throws for {mesh.MapName} under {reportsDir}");
            return 1;
        }

        var errors = new float[rows.Count];
        var rests = new Vector3[rows.Count];
        Parallel.For(0, rows.Count, i =>
        {
            var result = GrenadeTrajectory.SimulateExactRaw(collider, rows[i].Pos, rows[i].Vel, constants);
            rests[i] = result.RestPoint;
            errors[i] = Vector3.Distance(result.RestPoint, rows[i].Real);
        });

        var sorted = errors.OrderBy(e => e).ToArray();
        float Pct(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
        var reported = rows.Select(r => r.Reported).OrderBy(e => e).ToArray();
        Console.WriteLine($"{mesh.MapName}: {rows.Count} throws  median {Pct(0.5):F2}u  p90 {Pct(0.9):F1}u  p99 {Pct(0.99):F0}u  within 3u {errors.Count(e => e <= 3f) * 100.0 / rows.Count:F1}%  over 8u {errors.Count(e => e > 8f)} ({errors.Count(e => e > 8f) * 100.0 / rows.Count:F1}%)");
        Console.WriteLine($"  as reported at the time: median {reported[reported.Length / 2]:F2}u  over 8u {reported.Count(e => e > 8f)}");
        var changed = rows.Where((r, i) => Math.Abs(errors[i] - r.Reported) > 1f).Count();
        Console.WriteLine($"  throws whose error moved by more than 1u since they were graded: {changed}");

        if (options.TryGetValue("moved", out var movedRaw))
        {
            // Throws whose error changed by more than this since the report
            // graded them: what the physics/mesh changes since did, both ways.
            var threshold = float.Parse(movedRaw, CultureInfo.InvariantCulture);
            var moved = Enumerable.Range(0, rows.Count).Where(i => Math.Abs(errors[i] - rows[i].Reported) > threshold).OrderBy(i => errors[i] - rows[i].Reported).ToList();
            Console.WriteLine($"  moved by more than {threshold:F0}u: {moved.Count(i => errors[i] < rows[i].Reported)} better, {moved.Count(i => errors[i] > rows[i].Reported)} worse");
            foreach (var i in moved)
            {
                var r = rows[i];
                Console.WriteLine($"  {r.Reported,6:F0}u -> {errors[i],5:F0}u  {r.Report} [{r.Index}]  launch ({r.Pos.X:F0},{r.Pos.Y:F0},{r.Pos.Z:F0}) sim rest ({rests[i].X:F0},{rests[i].Y:F0},{rests[i].Z:F0}) real ({r.Real.X:F0},{r.Real.Y:F0},{r.Real.Z:F0})");
            }
        }
        if (worst > 0)
        {
            foreach (var i in Enumerable.Range(0, rows.Count).OrderByDescending(i => errors[i]).Take(worst))
            {
                var r = rows[i];
                Console.WriteLine($"  {errors[i],6:F0}u  {r.Report} [{r.Index}]  launch ({r.Pos.X:F0},{r.Pos.Y:F0},{r.Pos.Z:F0}) sim rest ({rests[i].X:F0},{rests[i].Y:F0},{rests[i].Z:F0}) real ({r.Real.X:F0},{r.Real.Y:F0},{r.Real.Z:F0})  (was {r.Reported:F0}u)");
            }
        }
        return 0;
    }

    static Vector3 Vec(JsonElement e)
    {
        var a = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        return new Vector3(a[0], a[1], a[2]);
    }
}
