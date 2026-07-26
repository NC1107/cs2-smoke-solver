using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;
using SmokeSolver.Solver;
using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;

namespace SmokeSolver.Cli;

/// <summary>
/// Precomputes every position on a map a player can actually reach and stand,
/// using the real player hull, and writes it beside the other per-map data.
/// </summary>
// This is a build step rather than something the solver does per query: the
// hull sweeps that make it trustworthy cost roughly 20s over a whole map,
// which is fine once per map extraction and far too slow inside a solve. The
// output is the origin set every lineup search starts from.
public static class StandSpotsCommand
{
    public sealed record StandSpotFile(string Map, float Step, StandSpotJson[] Spots);
    public sealed record StandSpotJson(float[] Feet, string Stance, bool Nav);

    public static int Run(Dictionary<string, string> options)
    {
        var geoPath = Require(options, "geo");
        var navPath = options.GetValueOrDefault("nav") ??
                      Path.ChangeExtension(geoPath, null) + ".navareas.json";
        var outPath = options.GetValueOrDefault("out") ??
                      Path.ChangeExtension(geoPath, null) + ".standspots.json";
        var step = float.Parse(options.GetValueOrDefault("step", "16"), CultureInfo.InvariantCulture);

        if (!File.Exists(navPath))
        {
            Console.Error.WriteLine($"nav areas not found at {navPath}; run extract first (or pass --nav)");
            return 2;
        }

        var mesh = CollisionMesh.Load(geoPath);
        var (min, max) = mesh.ComputeBounds();
        var areas = JsonSerializer.Deserialize<List<NavAreaJson>>(File.ReadAllText(navPath))
                    ?? throw new InvalidDataException($"{navPath} is empty");
        float[][][] corners = [.. areas.Select(a => a.Corners)];

        // Player-solid, so the clip brushes that stop a player - and are
        // invisible to grenades - are exactly what bounds the standable set.
        var collider = new TriangleCollider(mesh, min, max, mesh.PlayerSolidFilter());

        Console.WriteLine($"{mesh.MapName}: scanning {max.X - min.X:F0} x {max.Y - min.Y:F0} at {step}u with the 32x32x72 player hull");
        var sw = Stopwatch.StartNew();
        var lastPercent = -1;
        var spots = StandSpots.Compute(collider, corners, min, max, step, (done, total) =>
        {
            var percent = done * 100 / total;
            if (percent != lastPercent && percent % 10 == 0)
            {
                lastPercent = percent;
                Console.Write($"\r  columns {percent}%   ");
            }
        });
        sw.Stop();

        var payload = new StandSpotFile(mesh.MapName, step,
        [
            .. spots.Select(s => new StandSpotJson(
                [MathF.Round(s.Feet.X, 2), MathF.Round(s.Feet.Y, 2), MathF.Round(s.Feet.Z, 2)],
                s.Stance.ToString(), s.NavCovered)),
        ]);
        File.WriteAllText(outPath, JsonSerializer.Serialize(payload));

        var navCovered = spots.Count(s => s.NavCovered);
        Console.WriteLine($"\r  {spots.Count} reachable stand spots in {sw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"    {navCovered} on the nav mesh, {spots.Count - navCovered} the nav mesh misses " +
                          $"({(spots.Count == 0 ? 0 : 100.0 * (spots.Count - navCovered) / spots.Count):F1}%)");
        Console.WriteLine($"    {spots.Count(s => s.Stance == StandSpots.Stance.Crouching)} reachable only crouched");
        Console.WriteLine($"  wrote {outPath}");
        return 0;
    }
}
