using System.Globalization;
using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;

using static SmokeSolver.Cli.CliParsing;
using static SmokeSolver.Cli.MeshSetup;
namespace SmokeSolver.Cli;

/// <summary>
/// Replays the misses of a validation report tick by tick against the real
/// flight the rig captured and names the surface where the two split: which
/// triangle (and attribute) the sim bounced off, what normal the real
/// velocity change implies, and how far apart the two paths were at each
/// bounce. The report already classifies a miss (DRIFT, PHANTOM-BOUNCE, ...);
/// this is the next question, "off what?", answered without opening the game.
///
///   diverge --geo data/de_dust2.s2geo --report data/validation/de_dust2-....json [--min-err 8] [--index 12]
/// </summary>
public static class DivergeCommand
{
    const float DivergenceDistance = 8f;

    public static int Run(Dictionary<string, string> options)
    {
        options = new Dictionary<string, string>(options);
        options.TryAdd("attrs", SingleTargetDefaultAttrs);
        var (mesh, _, _, _) = LoadCommon(options);
        var constants = LoadConstants(options);
        var minErr = float.Parse(options.GetValueOrDefault("min-err", "8"), CultureInfo.InvariantCulture);
        var only = options.TryGetValue("index", out var indexRaw) ? int.Parse(indexRaw, CultureInfo.InvariantCulture) : (int?)null;

        // --summary: every report for this map, no per-throw output; instead
        // the triangles the sim bounced off while the real grenade did not
        // (phantom contacts, up to the split), ranked by how many misses they
        // appear in. The surfaces the game does not collide with, by count.
        var summary = options.ContainsKey("summary");
        TraceEnabled = options.ContainsKey("trace");
        var reportFiles = options.TryGetValue("report", out var one)
            ? [one]
            : Directory.EnumerateFiles(options.GetValueOrDefault("reports", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", "validation")), $"{mesh.MapName}-*.json").OrderBy(f => f).ToList();

        var captures = LoadCaptures(options.GetValueOrDefault("captures", Path.Combine(Path.GetDirectoryName(Path.GetFullPath(Require(options, "geo"))) ?? ".", "calib")));
        var (meshMin, meshMax) = mesh.ComputeBounds();
        var collider = BuildGrenadeCollider(mesh, meshMin, meshMax);

        var replayed = 0;
        var total = 0;
        var phantoms = new Dictionary<int, (int Misses, float ErrSum, Vector3 Where)>();
        var phantomBuilds = new Dictionary<int, Dictionary<string, int>>();
        var classes = new Dictionary<string, (int Count, float ErrSum)>();
        var onlyServerBuild = options.GetValueOrDefault("server-build", "");
        foreach (var file in reportFiles)
        {
            using var report = JsonDocument.Parse(File.ReadAllText(file));
            var build = report.RootElement.TryGetProperty("build", out var b) ? b.ToString() : "?";
            if (onlyServerBuild.Length > 0 && (!report.RootElement.TryGetProperty("serverBuild", out var sb) || sb.ToString() != onlyServerBuild))
            {
                continue;
            }
            var rows = report.RootElement.GetProperty("results").EnumerateArray()
                .Where(r => r.TryGetProperty("Detonated", out var det) && det.GetBoolean() && r.TryGetProperty("Pos", out _))
                .Where(r => only is { } i ? r.GetProperty("Index").GetInt32() == i : r.GetProperty("ErrPredicted").GetSingle() > minErr)
                .ToList();
            total += rows.Count;
            foreach (var row in rows)
            {
                var pos = Vec(row.GetProperty("Pos"));
                var vel = Vec(row.GetProperty("Vel"));
                if (!captures.TryGetValue(Key(pos), out var capture))
                {
                    if (!summary)
                    {
                        Console.WriteLine($"[{row.GetProperty("Index").GetInt32()}] no tick capture for launch ({pos.X:F0},{pos.Y:F0},{pos.Z:F0}); skipped");
                    }
                    continue;
                }
                var phantomHits = Replay(collider, constants, row, pos, vel, capture, quiet: summary);
                // The report's error is from grading time; a throw the current
                // physics and mesh already get right is no longer a miss.
                if (summary && only is null && LastError <= minErr)
                {
                    continue;
                }
                replayed++;
                var cls = classes.GetValueOrDefault(LastClass);
                classes[LastClass] = (cls.Count + 1, cls.ErrSum + LastError);
                if (options.ContainsKey("list"))
                {
                    Console.WriteLine($"  {LastClass,-8} {LastError,6:F0}u (was {row.GetProperty("ErrPredicted").GetSingle():F0}u)  {Path.GetFileNameWithoutExtension(file)} [{row.GetProperty("Index").GetInt32()}] {row.GetProperty("Type").GetString()}");
                }
                foreach (var (triangle, where) in phantomHits)
                {
                    var entry = phantoms.GetValueOrDefault(triangle);
                    phantoms[triangle] = (entry.Misses + 1, entry.ErrSum + LastError, where);
                    var builds = phantomBuilds.GetValueOrDefault(triangle) ?? (phantomBuilds[triangle] = []);
                    builds[build] = builds.GetValueOrDefault(build) + 1;
                }
            }
        }
        Console.WriteLine($"replayed {replayed} still-missing of {total} reported misses over {minErr:F0}u from {reportFiles.Count} report(s)");
        if (summary)
        {
            Console.WriteLine("mechanisms: " + string.Join("  ", classes.OrderByDescending(kv => kv.Value.Count).Select(kv => $"{kv.Key} {kv.Value.Count} (mean {kv.Value.ErrSum / kv.Value.Count:F0}u)")));
            Console.WriteLine("phantom contacts: triangles the sim bounced off before the split while the real grenade did not, by misses involved");
            foreach (var (triangle, entry) in phantoms.OrderByDescending(kv => kv.Value.Misses).Take(int.Parse(options.GetValueOrDefault("top", "25"), CultureInfo.InvariantCulture)))
            {
                var face = collider.Face(triangle);
                var n = Vector3.Normalize(Vector3.Cross(face.B - face.A, face.C - face.A));
                var size = MathF.Max(MathF.Max(Vector3.Distance(face.A, face.B), Vector3.Distance(face.B, face.C)), Vector3.Distance(face.C, face.A));
                Console.WriteLine($"  {entry.Misses,3} misses  tri {triangle,8} [{face.Attribute}#{face.AttributeIndex}] n({n.X:F2},{n.Y:F2},{n.Z:F2}) {size,4:F0}u  at ({entry.Where.X:F0},{entry.Where.Y:F0},{entry.Where.Z:F0})  builds {string.Join(" ", phantomBuilds[triangle].Select(kv => $"{kv.Key}x{kv.Value}"))}");
            }
        }
        return 0;
    }

    /// <summary>Which of the four ways a miss happens this one was: see Classify.</summary>
    public static string LastClass { get; private set; } = "";

    /// <summary>The replayed rest error of the last throw, under the current physics and mesh.</summary>
    public static float LastError { get; private set; }
    static bool TraceEnabled;

    static List<(int Triangle, Vector3 Where)> Replay(TriangleCollider collider, ThrowConstants constants, JsonElement row, Vector3 pos, Vector3 vel, float[][] samples, bool quiet = false)
    {
        var phantomTriangles = new List<(int, Vector3)>();
        var output = quiet ? TextWriter.Null : Console.Out;
        var simTicks = new List<(Vector3 Position, Vector3 Velocity)>();
        var simBounces = new List<BounceRecord>();
        // --trace prints the sim's own contact log next to the pairing: the
        // pairing shows bounces, the log also shows slides and rest checks.
        var simTrace = TraceEnabled ? new List<string>() : null;
        var result = GrenadeTrajectory.SimulateExactRaw(collider, pos, vel, constants, trace: simTrace, tickTrace: simTicks, bounceTrace: simBounces);
        if (simTrace is not null)
        {
            foreach (var line in simTrace)
            {
                output.WriteLine($"    trace {line}");
            }
        }
        var realBounces = RealBounces(samples, vel);
        var realRest = Vec(row.GetProperty("RealRest"));
        LastError = Vector3.Distance(result.RestPoint, realRest);

        var click = row.GetProperty("Strength").GetSingle() switch { >= 0.99f => "left", <= 0.01f => "right", _ => "mid" };
        output.WriteLine();
        output.WriteLine($"[{row.GetProperty("Index").GetInt32()}] {row.GetProperty("Type").GetString()} {click} err {row.GetProperty("ErrPredicted").GetSingle():F0}u {row.GetProperty("DivergenceClass").GetString()} stab {row.GetProperty("Stability").GetSingle():P0}  feet ({Vec(row.GetProperty("Feet")).X:F0},{Vec(row.GetProperty("Feet")).Y:F0},{Vec(row.GetProperty("Feet")).Z:F0}) yaw {row.GetProperty("Yaw").GetSingle():F1} pitch {row.GetProperty("Pitch").GetSingle():F1}");
        output.WriteLine($"  sim rest ({result.RestPoint.X:F0},{result.RestPoint.Y:F0},{result.RestPoint.Z:F0}) {simBounces.Count}b  real rest ({realRest.X:F0},{realRest.Y:F0},{realRest.Z:F0}) {realBounces.Count}b");

        var divergence = -1;
        for (var i = 0; i < Math.Min(simTicks.Count, samples.Count()); i++)
        {
            if (Vector3.Distance(simTicks[i].Position, Sample(samples[i])) > DivergenceDistance)
            {
                divergence = i;
                break;
            }
        }

        LastClass = Classify(collider, constants, simBounces, realBounces, divergence);

        // Pair every sim bounce with the nearest real one (by tick): the pair
        // whose ticks or contacts disagree is where the geometry differs.
        output.WriteLine("  bounces (sim | real):");
        var used = new HashSet<int>();
        foreach (var b in simBounces)
        {
            var face = collider.Face(b.Triangle);
            var faceNormal = Vector3.Normalize(Vector3.Cross(face.B - face.A, face.C - face.A));
            var size = MathF.Max(MathF.Max(Vector3.Distance(face.A, face.B), Vector3.Distance(face.B, face.C)), Vector3.Distance(face.C, face.A));
            var line = $"    sim  t{b.Tick,4} ({b.Contact.X,6:F0},{b.Contact.Y,6:F0},{b.Contact.Z,6:F0}) n({b.Normal.X:F2},{b.Normal.Y:F2},{b.Normal.Z:F2}) {SpeedWord(b)} [{face.Attribute}#{face.AttributeIndex}] tri {size:F0}u n({faceNormal.X:F2},{faceNormal.Y:F2},{faceNormal.Z:F2}) A({face.A.X:F0},{face.A.Y:F0},{face.A.Z:F0})";
            var match = realBounces
                .Select((r, idx) => (r, idx))
                .Where(x => !used.Contains(x.idx) && Math.Abs(x.r.Tick - b.Tick) <= 6)
                .OrderBy(x => Math.Abs(x.r.Tick - b.Tick))
                .Select(x => (x.r, x.idx, found: true))
                .FirstOrDefault();
            if (match.found)
            {
                used.Add(match.idx);
                var r = match.r;
                var gap = Vector3.Distance(r.Position, b.Contact);
                line += $" | real t{r.Tick,4} ({r.Position.X,6:F0},{r.Position.Y,6:F0},{r.Position.Z,6:F0}) n({r.Normal.X:F2},{r.Normal.Y:F2},{r.Normal.Z:F2}) gap {gap:F1}u";
                // Which surface would our own bounce model need to turn the
                // real incoming velocity into the real outgoing one? If that
                // normal differs from the mesh triangle's, the mesh is tilted
                // wrong there; if no normal fits, the bounce model is.
                var (fit, residual) = FitNormal(r.Before, r.After, constants);
                line += $"\n         v sim {V(b.VelocityBefore)}->{V(b.VelocityAfter)}  real {V(r.Before)}->{V(r.After)}  fit n({fit.X:F2},{fit.Y:F2},{fit.Z:F2}) residual {residual:F0}u/s";
            }
            else
            {
                line += " | real   -- no bounce within 6 ticks (PHANTOM)";
                if (divergence < 0 || b.Tick <= divergence + 6)
                {
                    phantomTriangles.Add((b.Triangle, b.Contact));
                }
            }
            if (divergence >= 0 && b.Tick >= divergence)
            {
                line += "   <- after split";
            }
            output.WriteLine(line);
        }
        foreach (var (r, idx) in realBounces.Select((r, idx) => (r, idx)).Where(x => !used.Contains(x.idx)))
        {
            output.WriteLine($"    sim    --                                                          | real t{r.Tick,4} ({r.Position.X,6:F0},{r.Position.Y,6:F0},{r.Position.Z,6:F0}) n({r.Normal.X:F2},{r.Normal.Y:F2},{r.Normal.Z:F2}) (MISSED by sim){(divergence >= 0 && r.Tick >= divergence ? "   <- after split" : "")}");
        }

        if (divergence < 0)
        {
            output.WriteLine($"  paths never split by more than {DivergenceDistance}u over {Math.Min(simTicks.Count, samples.Count())} ticks; the rest differs (settle/roll)");
            return phantomTriangles;
        }
        var s = simTicks[divergence];
        var r0 = Sample(samples[divergence]);
        output.WriteLine($"  split at tick {divergence}: sim ({s.Position.X:F0},{s.Position.Y:F0},{s.Position.Z:F0}) v({s.Velocity.X:F0},{s.Velocity.Y:F0},{s.Velocity.Z:F0})  real ({r0.X:F0},{r0.Y:F0},{r0.Z:F0}) v({samples[divergence][4]:F0},{samples[divergence][5]:F0},{samples[divergence][6]:F0})");
        // The surface responsible is the last bounce before the split on
        // either side; print where the two paths were at that moment too.
        var lastSim = simBounces.LastOrDefault(b => b.Tick <= divergence);
        var lastReal = realBounces.LastOrDefault(b => b.Tick <= divergence);
        if (lastSim.Tick > 0 || lastReal.Tick > 0)
        {
            var at = Math.Max(0, Math.Min(Math.Max(lastSim.Tick, lastReal.Tick) - 1, Math.Min(simTicks.Count, samples.Count()) - 1));
            output.WriteLine($"  one tick before the last pre-split bounce (t{at}): sim/real {Vector3.Distance(simTicks[at].Position, Sample(samples[at])):F1}u apart");
        }
            return phantomTriangles;
    }

    /// <summary>
    /// The mechanism behind a miss, from the bounces around the split:
    /// settle (paths never split; the rest differs), phantom (the sim bounced
    /// off something the game did not), missed (the game bounced off
    /// something we lack), normal (both bounced together but the real rebound
    /// implies a surface tilted more than 8 degrees from ours), rebound (same
    /// surface, different outcome) or drift (split with no bounce nearby).
    /// </summary>
    static string Classify(TriangleCollider collider, ThrowConstants constants, List<BounceRecord> sim, List<(int Tick, Vector3 Position, Vector3 Normal, Vector3 Before, Vector3 After)> real, int divergence)
    {
        if (divergence < 0)
        {
            return "settle";
        }
        var simNear = sim.Where(b => b.Tick <= divergence + 2 && b.Tick >= divergence - 12).OrderByDescending(b => b.Tick).FirstOrDefault();
        var realNear = real.Where(r => r.Tick <= divergence + 2 && r.Tick >= divergence - 12).OrderByDescending(r => r.Tick).FirstOrDefault();
        var hasSim = simNear.Tick > 0 || (sim.Count > 0 && sim[0].Tick == 0 && divergence <= 14);
        var hasReal = realNear.Tick > 0;
        if (hasSim && hasReal && Math.Abs(simNear.Tick - realNear.Tick) <= 6)
        {
            var (fit, _) = FitNormal(realNear.Before, realNear.After, constants);
            var angle = MathF.Acos(Math.Clamp(Vector3.Dot(fit, simNear.Normal), -1f, 1f)) * 180f / MathF.PI;
            return angle > 8f ? "normal" : "rebound";
        }
        if (hasSim && !hasReal)
        {
            return "phantom";
        }
        if (hasReal && !hasSim)
        {
            return "missed";
        }
        return "drift";
    }

    static string SpeedWord(BounceRecord b) => $"{b.VelocityBefore.Length():F0}->{b.VelocityAfter.Length():F0}";

    static string V(Vector3 v) => $"({v.X:F0},{v.Y:F0},{v.Z:F0})";

    /// <summary>
    /// Brute-force the unit normal (1 degree lattice over the upper and lower
    /// hemispheres) that makes the sim's bounce model map the real incoming
    /// velocity closest to the real outgoing one.
    /// </summary>
    static (Vector3 Normal, float Residual) FitNormal(Vector3 before, Vector3 after, ThrowConstants k)
    {
        var best = Vector3.UnitZ;
        var bestErr = float.MaxValue;
        for (var el = -89; el <= 90; el++)
        {
            var e = el * MathF.PI / 180f;
            for (var az = 0; az < 360; az++)
            {
                var a = az * MathF.PI / 180f;
                var n = new Vector3(MathF.Cos(e) * MathF.Cos(a), MathF.Cos(e) * MathF.Sin(a), MathF.Sin(e));
                if (Vector3.Dot(before, n) >= 0f)
                {
                    continue;
                }
                var err = Vector3.Distance(GrenadeTrajectory.Bounce(before, n, k), after);
                if (err < bestErr)
                {
                    (best, bestErr) = (n, err);
                }
            }
        }
        return (best, bestErr);
    }

    /// <summary>
    /// Real bounces are velocity discontinuities in the capture; the impulse
    /// direction (velocity change minus the gravity step) is the contact normal.
    /// </summary>
    static List<(int Tick, Vector3 Position, Vector3 Normal, Vector3 Before, Vector3 After)> RealBounces(float[][] samples, Vector3 launch)
    {
        var list = new List<(int, Vector3, Vector3, Vector3, Vector3)>();
        for (var i = 0; i < samples.Length; i++)
        {
            var v = new Vector3(samples[i][4], samples[i][5], samples[i][6]);
            var prev = i > 0 ? new Vector3(samples[i - 1][4], samples[i - 1][5], samples[i - 1][6]) : launch;
            var dv = v - prev;
            if (MathF.Abs(dv.X) > 0.5f || MathF.Abs(dv.Y) > 0.5f || MathF.Abs(dv.Z + 5f) > 0.5f)
            {
                var impulse = dv + new Vector3(0, 0, 5f);
                var n = impulse.Length() > 1e-3f ? Vector3.Normalize(impulse) : Vector3.Zero;
                // The capture's velocity is post-gravity for its tick; the
                // pre-bounce velocity is the previous sample with one gravity
                // step applied, matching what the sim feeds its bounce.
                list.Add((i, Sample(samples[i]), n, prev - new Vector3(0, 0, 5f), v));
            }
            if (MathF.Abs(v.X) < 1f && MathF.Abs(v.Y) < 1f && MathF.Abs(v.Z) < 1f)
            {
                break;
            }
        }
        return list;
    }

    static Dictionary<(int, int, int), float[][]> LoadCaptures(string calibDir)
    {
        var files = File.Exists(calibDir)
            ? [calibDir]
            : Directory.EnumerateFiles(calibDir, "captures*.jsonl").OrderBy(f => f).ToList();
        var map = new Dictionary<(int, int, int), float[][]>();
        foreach (var file in files)
        {
            foreach (var line in File.ReadLines(file))
            {
                if (line.Length == 0)
                {
                    continue;
                }
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("samples", out var samplesEl) || !doc.RootElement.TryGetProperty("start", out var startEl))
                {
                    continue;
                }
                var samples = samplesEl.EnumerateArray()
                    .Select(s => s.EnumerateArray().Select(e => e.GetSingle()).ToArray())
                    .Where(s => s.Length >= 7)
                    .ToArray();
                map[Key(Vec(startEl))] = samples;
            }
        }
        Console.WriteLine($"{map.Count} tick captures loaded from {files.Count} file(s)");
        return map;
    }

    // Launch positions are float-exact in both the report and the capture;
    // a 0.1u key absorbs the JSON round trip.
    static (int, int, int) Key(Vector3 v) => ((int)MathF.Round(v.X * 10), (int)MathF.Round(v.Y * 10), (int)MathF.Round(v.Z * 10));

    static Vector3 Sample(float[] s) => new(s[1], s[2], s[3]);

    static Vector3 Vec(JsonElement e)
    {
        var a = e.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        return new Vector3(a[0], a[1], a[2]);
    }
}
