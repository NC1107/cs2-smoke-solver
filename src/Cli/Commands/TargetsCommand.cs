using System.Globalization;
using System.Numerics;
using System.Text.Json;

using static SmokeSolver.Cli.CliParsing;
namespace SmokeSolver.Cli;

/// <summary>
/// Seeds the canonical smoke targets for a map from where pros actually land
/// smokes, so a spot has a durable identity to vote on and a name to click.
/// </summary>
// A target today is a raw coordinate to a tenth of a unit, which is why two
// people who found "the same" smoke never agree on it, why the cache fills with
// near-duplicates, and why votes would have nothing to attach to. Valve's own
// callouts cannot fix that: measured on de_dust2, the nearest env_cs_place
// marker to the densest pro landing cluster is 414u away - they name rooms,
// not the spots inside rooms that smokes go.
//
// The pro-demo landings are the honest seed. Clustered, the top spots on a map
// ARE the community's canonical targets, arrived at by thousands of real
// throws rather than by anyone's guess. Names, though, are a human's job: a
// cluster gets the nearby callout when one is genuinely close, and a
// provisional "near X" otherwise, and the file is meant to be hand-edited.
public static class TargetsCommand
{
    // Landings within this of a cluster's seed belong to it. Roughly a smoke's
    // own coverage radius: two landings closer than this block the same thing.
    const float ClusterRadius = 110f;
    // Fewer than this and it is one team's one-off, not a spot.
    const int MinLandings = 6;
    // A callout this close is the spot's name; further and it is only a hint.
    const float CalloutNameReach = 120f;

    public static int Run(Dictionary<string, string> options)
    {
        var root = Path.GetFullPath(options.GetValueOrDefault("root", "."));
        var map = Require(options, "map");
        var dataDir = Path.Combine(root, "data");
        var proPath = Path.Combine(dataDir, $"{map}.prosmokes.json");
        var outPath = options.GetValueOrDefault("out", Path.Combine(dataDir, $"{map}.targets.json"));
        var radius = float.Parse(options.GetValueOrDefault("radius", ClusterRadius.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);
        var minLandings = int.Parse(options.GetValueOrDefault("min", MinLandings.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

        if (!File.Exists(proPath))
        {
            Console.Error.WriteLine($"no pro landing data at {proPath} - run rig/parse-demo-smokes.py for this map first");
            return 1;
        }

        using var pro = JsonDocument.Parse(File.ReadAllText(proPath));
        var lands = pro.RootElement.GetProperty("lands").EnumerateArray()
            .Select(l => new Vector3(l[0].GetSingle(), l[1].GetSingle(), l[2].GetSingle()))
            .ToList();

        var places = LoadPlaces(dataDir, map);
        var clusters = Cluster(lands, radius, minLandings);

        // Existing names survive a re-run: the whole point of the file is that a
        // human edits it, and regenerating must not throw that work away. A
        // cluster keeps its name when its centre lands within the radius of an
        // already-named one.
        var existing = File.Exists(outPath) ? ReadExisting(outPath) : [];

        var targets = new List<CanonicalTarget>();
        foreach (var (members, centre, spread) in clusters)
        {
            var kept = existing.FirstOrDefault(e => Vector2.Distance(new Vector2(e.Pos[0], e.Pos[1]), new Vector2(centre.X, centre.Y)) <= radius && e.Named);
            string name;
            bool named;
            if (kept is not null)
            {
                name = kept.Name;
                named = true;
            }
            else
            {
                var (callout, dist) = NearestPlace(places, centre);
                named = false;
                name = callout is null ? "unnamed"
                    : dist <= CalloutNameReach ? callout
                    : $"near {callout}";
            }
            targets.Add(new CanonicalTarget(name, named, [centre.X, centre.Y, centre.Z], members.Count, spread));
        }

        var json = JsonSerializer.Serialize(targets.Select(t => new
        {
            name = t.Name,
            // False until a person has confirmed the name. The viewer shows
            // provisional names in a lighter style so a guess never reads as
            // a fact, and this command never overwrites a true one.
            named = t.Named,
            pos = t.Pos.Select(v => MathF.Round(v)).ToArray(),
            landings = t.Landings,
            // 80th-percentile distance of the cluster's landings from its
            // centre: how loose the pros themselves are about this spot.
            spread = MathF.Round(t.Spread),
        }), new JsonSerializerOptions { WriteIndented = true });

        var temp = outPath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, outPath, overwrite: true);

        Console.WriteLine($"{map}: {lands.Count} pro landings -> {targets.Count} targets ({targets.Count(t => t.Named)} named, {targets.Count(t => !t.Named)} provisional)");
        Console.WriteLine($"  covered {targets.Sum(t => t.Landings)} of {lands.Count} landings; wrote {outPath}");
        foreach (var t in targets)
        {
            Console.WriteLine($"  {(t.Named ? " " : "?")} {t.Name,-22} [{t.Pos[0],6:F0},{t.Pos[1],6:F0},{t.Pos[2],5:F0}]  {t.Landings,3} landings, spread {t.Spread,3:F0}u");
        }
        return 0;
    }

    sealed record CanonicalTarget(string Name, bool Named, float[] Pos, int Landings, float Spread);

    // Greedy density clustering: seed on whichever remaining landing has the
    // most neighbours, absorb everything within the radius, repeat. Simple,
    // deterministic, and it puts the densest spots first, which is the order
    // the file should read in.
    static List<(List<Vector3> Members, Vector3 Centre, float Spread)> Cluster(List<Vector3> lands, float radius, int minLandings)
    {
        var remaining = new List<Vector3>(lands);
        var clusters = new List<(List<Vector3>, Vector3, float)>();
        var r2 = radius * radius;
        static float Dist2(Vector3 a, Vector3 b) => (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);

        while (remaining.Count > 0)
        {
            var seed = remaining.MaxBy(p => remaining.Count(q => Dist2(p, q) <= r2));
            var members = remaining.Where(q => Dist2(seed, q) <= r2).ToList();
            if (members.Count < minLandings)
            {
                break;
            }
            var centre = new Vector3(members.Average(m => m.X), members.Average(m => m.Y), members.Average(m => m.Z));
            var spreads = members.Select(m => MathF.Sqrt(Dist2(centre, m))).OrderBy(d => d).ToList();
            var spread = spreads[(int)(0.8f * (spreads.Count - 1))];
            clusters.Add((members, centre, spread));
            var taken = new HashSet<Vector3>(members);
            remaining.RemoveAll(taken.Contains);
        }
        return clusters;
    }

    static List<(string Name, Vector3 Origin)> LoadPlaces(string dataDir, string map)
    {
        var path = Path.Combine(dataDir, $"{map}.entities.json");
        if (!File.Exists(path))
        {
            return [];
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var places = new List<(string, Vector3)>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.GetProperty("ClassName").GetString() != "env_cs_place" ||
                !e.TryGetProperty("Place", out var placeEl) || placeEl.GetString() is not { Length: > 0 } name)
            {
                continue;
            }
            var o = e.GetProperty("Origin");
            places.Add((name, new Vector3(o[0].GetSingle(), o[1].GetSingle(), o[2].GetSingle())));
        }
        return places;
    }

    static (string? Name, float Distance) NearestPlace(List<(string Name, Vector3 Origin)> places, Vector3 at)
    {
        if (places.Count == 0)
        {
            return (null, float.MaxValue);
        }
        var best = places.MinBy(p => Vector2.Distance(new Vector2(p.Origin.X, p.Origin.Y), new Vector2(at.X, at.Y)));
        return (best.Name, Vector2.Distance(new Vector2(best.Origin.X, best.Origin.Y), new Vector2(at.X, at.Y)));
    }

    static List<CanonicalTarget> ReadExisting(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.EnumerateArray().Select(t => new CanonicalTarget(
                t.GetProperty("name").GetString() ?? "unnamed",
                t.TryGetProperty("named", out var n) && n.GetBoolean(),
                t.GetProperty("pos").EnumerateArray().Select(v => v.GetSingle()).ToArray(),
                t.TryGetProperty("landings", out var l) ? l.GetInt32() : 0,
                t.TryGetProperty("spread", out var s) ? s.GetSingle() : 0f)).ToList();
        }
        catch (Exception e) when (e is JsonException or IOException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"existing targets file unreadable, regenerating from scratch: {e.Message}");
            return [];
        }
    }
}
