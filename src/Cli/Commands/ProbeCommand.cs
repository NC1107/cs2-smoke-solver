using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
namespace SmokeSolver.Cli;

/// <summary>
/// Ground truth for breakables: fires one synthetic grenade straight through
/// every breakable cluster of a map on the rig server (then a second one at
/// the same, now broken, pane) and reads what the real grenade did from the
/// capture - passed at 0.40, passed untouched, bounced, or stopped. The sim
/// treats every EntityBreakable as intact glass that passes at 0.40; this is
/// how vents, boards, pots and doors get checked against that.
///
///   probe --geo data/de_nuke.s2geo [--calib data/calib] [--distance 160] [--speed 600]
///         [--no-changelevel] [--out data/tmp/probe-de_nuke.json]
/// </summary>
public static class ProbeCommand
{
    sealed record Cluster(string Kind, int Triangles, Vector3 Centre, Vector3 Size);
    sealed record Shot(int Index, Cluster Cluster, int Attempt, Vector3 Pos, Vector3 Vel, Vector3 Normal, Vector3 SimRest, string SimEvent);

    static bool Verbose;

    public static int Run(Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(options);
        Verbose = options.ContainsKey("verbose");
        options.TryAdd("attrs", SingleTargetDefaultAttrs);
        var (mesh, _, _, _) = LoadCommon(options);
        var constants = LoadConstants(options);
        var calibDir = options.GetValueOrDefault("calib", Environment.GetEnvironmentVariable("SMOKESOLVER_CALIB_DIR") ?? "data/calib");
        var distance = float.Parse(options.GetValueOrDefault("distance", "160"), CultureInfo.InvariantCulture);
        var speed = float.Parse(options.GetValueOrDefault("speed", "600"), CultureInfo.InvariantCulture);
        var outPath = options.GetValueOrDefault("out", $"data/tmp/probe-{mesh.MapName}.json");
        var (meshMin, meshMax) = mesh.ComputeBounds();
        var collider = new TriangleCollider(mesh, meshMin, meshMax, mesh.GrenadeSolidFilter());

        var clusters = Clusters(mesh);
        Console.WriteLine($"{mesh.MapName}: {clusters.Count} breakable/door cluster(s)");
        var shots = new List<Shot>();
        foreach (var c in clusters)
        {
            if (Aim(collider, c, distance, speed, constants) is not { } aim)
            {
                Console.WriteLine($"  {c.Kind,-15} centre ({c.Centre.X:F0},{c.Centre.Y:F0},{c.Centre.Z:F0}) size ({c.Size.X:F0},{c.Size.Y:F0},{c.Size.Z:F0}) - no clear approach, skipped");
                continue;
            }
            var (pos, vel, normal) = aim;
            var trace = new List<string>();
            var sim = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants, trace);
            var simEvent = trace.FirstOrDefault(l => l.Contains("glass")) is { } g ? "glass pass" : trace.FirstOrDefault(l => l.Contains("contact")) is { } b ? "bounce" : "none";
            for (var attempt = 1; attempt <= 2; attempt++)
            {
                // The second shot is offset by a hair so its capture is separable.
                var p2 = pos + normal * (0.25f * (attempt - 1));
                shots.Add(new Shot(shots.Count, c, attempt, p2, vel, normal, sim.RestPoint, simEvent));
            }
            Console.WriteLine($"  {c.Kind,-15} tris={c.Triangles,4} centre ({c.Centre.X:F0},{c.Centre.Y:F0},{c.Centre.Z:F0}) size ({c.Size.X:F0},{c.Size.Y:F0},{c.Size.Z:F0}) from ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) sim: {simEvent}");
        }
        if (shots.Count == 0)
        {
            Console.Error.WriteLine("nothing to probe");
            return 1;
        }

        if (options.ContainsKey("dry-run"))
        {
            return 0;
        }
        if (!options.ContainsKey("no-changelevel") && !BatchValidateCommand.ChangeLevel(mesh.MapName, calibDir))
        {
            Console.Error.WriteLine("rig server did not consume the changelevel request - is it running with the plugin loaded?");
            return 1;
        }

        var requestPath = Path.Combine(calibDir, "request.json");
        var tailer = new CaptureTailer(Path.Combine(calibDir, "captures.jsonl"));
        tailer.InitializeAtEnd();
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] firing {shots.Count} probe throws, one at a time ...");
        foreach (var s in shots)
        {
            RequestFile.WriteAtomic(requestPath, JsonSerializer.Serialize(new
            {
                throws = new[]
                {
                    new
                    {
                        pos = new[] { s.Pos.X, s.Pos.Y, s.Pos.Z },
                        vel = new[] { s.Vel.X, s.Vel.Y, s.Vel.Z },
                        predict = new[] { s.SimRest.X, s.SimRest.Y, s.SimRest.Z },
                        note = $"probe {s.Cluster.Kind} #{s.Index} attempt {s.Attempt}",
                    },
                },
            }));
            var waited = 0;
            while (File.Exists(requestPath) && waited < 15000)
            {
                Thread.Sleep(50);
                waited += 50;
            }
            if (File.Exists(requestPath))
            {
                Console.Error.WriteLine("  throw not consumed after 15s");
                File.Delete(requestPath);
                return 1;
            }
            // The first grenade must have broken (or not) the pane before the
            // second arrives; a smoke lives ~20 s but the pass happens in the
            // first half second.
            Thread.Sleep(2500);
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] waiting for captures ...");
        var matches = new Dictionary<int, JsonElement>();
        var idleMs = 0;
        while (matches.Count < shots.Count && idleMs < 30000)
        {
            Thread.Sleep(2000);
            idleMs += 2000;
            foreach (var line in tailer.ReadNewLines())
            {
                JsonElement c;
                try
                {
                    c = JsonSerializer.Deserialize<JsonElement>(line);
                }
                catch (JsonException)
                {
                    continue;
                }
                if (!c.TryGetProperty("start", out var startEl) || !c.TryGetProperty("velocity", out var velEl))
                {
                    continue;
                }
                var st = Vec(startEl);
                var v = Vec(velEl);
                foreach (var s in shots)
                {
                    if (!matches.ContainsKey(s.Index) && Vector3.Distance(st, s.Pos) < 0.1f && Vector3.Distance(v, s.Vel) < 0.5f)
                    {
                        matches[s.Index] = c;
                        idleMs = 0;
                        break;
                    }
                }
            }
        }
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] matched {matches.Count}/{shots.Count}");

        var results = new List<object>();
        Console.WriteLine();
        Console.WriteLine("  kind             centre                 attempt  sim         real          speed in -> out   rest error");
        foreach (var s in shots)
        {
            if (!matches.TryGetValue(s.Index, out var cap))
            {
                Console.WriteLine($"  {s.Cluster.Kind,-15} ({s.Cluster.Centre.X:F0},{s.Cluster.Centre.Y:F0},{s.Cluster.Centre.Z:F0})  {s.Attempt}        {s.SimEvent,-10}  (no capture)");
                continue;
            }
            var samples = cap.GetProperty("samples").EnumerateArray().Select(e => e.EnumerateArray().Select(x => x.GetSingle()).ToArray()).Where(a => a.Length >= 7).ToArray();
            var (real, speedIn, speedOut) = Classify(samples, s);
            var restReal = cap.TryGetProperty("rest", out var restEl) ? Vec(restEl) : Sample(samples[^1]);
            var restErr = Vector3.Distance(restReal, s.SimRest);
            Console.WriteLine($"  {s.Cluster.Kind,-15} ({s.Cluster.Centre.X:F0},{s.Cluster.Centre.Y:F0},{s.Cluster.Centre.Z:F0})  {s.Attempt}        {s.SimEvent,-10}  {real,-12}  {speedIn,5:F0} -> {speedOut,-5:F0}      {restErr,6:F1}u");
            results.Add(new
            {
                kind = s.Cluster.Kind, triangles = s.Cluster.Triangles, attempt = s.Attempt,
                centre = new[] { s.Cluster.Centre.X, s.Cluster.Centre.Y, s.Cluster.Centre.Z },
                size = new[] { s.Cluster.Size.X, s.Cluster.Size.Y, s.Cluster.Size.Z },
                pos = new[] { s.Pos.X, s.Pos.Y, s.Pos.Z }, vel = new[] { s.Vel.X, s.Vel.Y, s.Vel.Z },
                sim = s.SimEvent, real, speedIn, speedOut, restErr,
                simRest = new[] { s.SimRest.X, s.SimRest.Y, s.SimRest.Z }, realRest = new[] { restReal.X, restReal.Y, restReal.Z },
            });
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, JsonSerializer.Serialize(new { map = mesh.MapName, build = mesh.GameBuildId, serverBuild = ValidateCommand.RigServerBuild(), timestamp = DateTime.Now.ToString("o"), speed, distance, results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    // Breakable and door triangles grouped by 96u cell, cells merged within
    // 160u: one entry per pane, vent, door or prop.
    static List<Cluster> Clusters(CollisionMesh mesh)
    {
        var cells = new Dictionary<(string, int, int, int), (int N, Vector3 Sum, Vector3 Lo, Vector3 Hi)>();
        var tris = mesh.Indices.Length / 3;
        for (var t = 0; t < tris; t++)
        {
            var kind = mesh.AttributeNames[mesh.TriangleAttributes[t]];
            if (kind is not ("EntityBreakable" or "EntityDoor"))
            {
                continue;
            }
            var a = V(mesh, mesh.Indices[t * 3]);
            var b = V(mesh, mesh.Indices[t * 3 + 1]);
            var c = V(mesh, mesh.Indices[t * 3 + 2]);
            var centre = (a + b + c) / 3f;
            var key = (kind, (int)MathF.Round(centre.X / 96f), (int)MathF.Round(centre.Y / 96f), (int)MathF.Round(centre.Z / 96f));
            var cur = cells.TryGetValue(key, out var existing) ? existing : (N: 0, Sum: Vector3.Zero, Lo: new Vector3(float.MaxValue), Hi: new Vector3(float.MinValue));
            cells[key] = (cur.N + 1, cur.Sum + centre, Vector3.Min(cur.Lo, Vector3.Min(a, Vector3.Min(b, c))), Vector3.Max(cur.Hi, Vector3.Max(a, Vector3.Max(b, c))));
        }
        var merged = new List<(string Kind, int N, Vector3 Sum, Vector3 Lo, Vector3 Hi)>();
        foreach (var (key, cell) in cells.OrderBy(kv => kv.Key.Item2).ThenBy(kv => kv.Key.Item3).ThenBy(kv => kv.Key.Item4))
        {
            var centre = cell.Sum / cell.N;
            var i = merged.FindIndex(m => m.Kind == key.Item1 && Vector3.Distance(m.Sum / m.N, centre) < 160f);
            if (i >= 0)
            {
                var m = merged[i];
                merged[i] = (m.Kind, m.N + cell.N, m.Sum + cell.Sum, Vector3.Min(m.Lo, cell.Lo), Vector3.Max(m.Hi, cell.Hi));
            }
            else
            {
                merged.Add((key.Item1, cell.N, cell.Sum, cell.Lo, cell.Hi));
            }
        }
        return merged.Select(m => new Cluster(m.Kind, m.N, m.Sum / m.N, m.Hi - m.Lo)).OrderBy(c => c.Kind).ThenByDescending(c => c.Triangles).ToList();
    }

    static Vector3 V(CollisionMesh mesh, int index) => new(mesh.Vertices[index * 3], mesh.Vertices[index * 3 + 1], mesh.Vertices[index * 3 + 2]);

    // A launch point in clear air on one side of the cluster, aimed at its
    // centre with the drop over the flight compensated, whose first contact
    // is the cluster itself.
    static (Vector3 Pos, Vector3 Vel, Vector3 Normal)? Aim(TriangleCollider collider, Cluster c, float distance, float speed, ThrowConstants k)
    {
        var axes = new List<Vector3>();
        var thin = c.Size.X <= c.Size.Y && c.Size.X <= c.Size.Z ? Vector3.UnitX : c.Size.Y <= c.Size.Z ? Vector3.UnitY : Vector3.UnitZ;
        if (thin == Vector3.UnitZ || MathF.Min(c.Size.X, c.Size.Y) > 30f)
        {
            // A flat pane in the floor or a bulky prop: approach horizontally.
            axes.AddRange([Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY]);
        }
        else
        {
            axes.AddRange([thin, -thin]);
        }
        foreach (var d in new[] { distance, distance * 0.6f, distance * 1.6f })
        {
            foreach (var n in axes)
            {
                var pos = c.Centre + n * d;
                if (collider.BoxIntersects(pos, new Vector3(4f)))
                {
                    if (Verbose)
                    {
                        Console.WriteLine($"      from ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) d={d:F0}: launch point inside solid");
                    }
                    continue;
                }
                var flight = d / speed;
                var drop = 0.5f * GrenadeTrajectory.BaseGravity * k.GravityScale * flight * flight;
                var target = c.Centre + new Vector3(0f, 0f, drop);
                var vel = Vector3.Normalize(target - pos) * speed;
                var sweep = collider.FirstHitHullIndexed(pos, c.Centre - n * 2f, new Vector3(GrenadeTrajectory.GrenadeRadius));
                // Good enough when the first thing in the way is the cluster
                // itself or something within 40u of it (a frame, a grate in
                // front of the vent): the capture then tells what the game
                // does at that spot, which is the point.
                if (sweep is not { } hit || (!IsCluster(collider, hit.Triangle, c) && Vector3.Distance(Vector3.Lerp(pos, c.Centre - n * 2f, hit.T), c.Centre) > 40f))
                {
                    if (Verbose)
                    {
                        var what = sweep is { } h ? $"first hit {collider.Face(h.Triangle).Attribute} at T={h.T:F2} n=({h.Normal.X:F2},{h.Normal.Y:F2},{h.Normal.Z:F2})" : "no hit at all";
                        Console.WriteLine($"      from ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}) along ({n.X:F0},{n.Y:F0},{n.Z:F0}) d={d:F0}: {what}");
                    }
                    continue;
                }
                return (pos, vel, n);
            }
        }
        return null;
    }

    static bool IsCluster(TriangleCollider collider, int triangle, Cluster c)
    {
        var f = collider.Face(triangle);
        return f.Attribute == c.Kind && Vector3.Distance((f.A + f.B + f.C) / 3f, c.Centre) < MathF.Max(c.Size.Length(), 64f);
    }

    // What the real grenade did at the cluster: horizontal speed just before
    // the crossing against just after, and whether its heading reversed.
    static (string Real, float SpeedIn, float SpeedOut) Classify(float[][] samples, Shot s)
    {
        if (samples.Length < 6)
        {
            return ("no-samples", 0f, 0f);
        }
        // The crossing tick: closest approach to the cluster plane along the approach normal.
        var best = 0;
        var bestD = float.MaxValue;
        for (var i = 0; i < samples.Length; i++)
        {
            var d = MathF.Abs(Vector3.Dot(Sample(samples[i]) - s.Cluster.Centre, s.Normal));
            if (d < bestD)
            {
                bestD = d;
                best = i;
            }
        }
        var i0 = Math.Max(0, best - 2);
        var i1 = Math.Min(samples.Length - 1, best + 3);
        var vin = new Vector3(samples[i0][4], samples[i0][5], samples[i0][6]);
        var vout = new Vector3(samples[i1][4], samples[i1][5], samples[i1][6]);
        var speedIn = vin.Length();
        var speedOut = vout.Length();
        var along = Vector3.Dot(Vector3.Normalize(s.Vel), Vector3.Normalize(vout.Length() > 1e-3f ? vout : s.Vel));
        var ratio = speedIn > 1f ? speedOut / speedIn : 0f;
        var real = along < 0f ? "BOUNCE" : ratio > 0.85f ? "PASS-FULL" : ratio > 0.25f ? $"PASS-{ratio:F2}" : "STOPPED";
        return (real, speedIn, speedOut);
    }

    static Vector3 Sample(float[] s) => new(s[1], s[2], s[3]);

    static Vector3 Vec(JsonElement e)
    {
        var a = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        return new Vector3(a[0], a[1], a[2]);
    }
}
