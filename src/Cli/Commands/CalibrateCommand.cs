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

public static class CalibrateCommand
{
    public static int Run(Dictionary<string, string> options)
    {
        var (mesh, _, _, _) = LoadCommon(options);
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
                new ThrowSpec(eye, yaw, pitch, ParseThrowType(type), strength),
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
        var collider = BuildGrenadeCollider(mesh, min, max);

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
}
