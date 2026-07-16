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

public static class ThrowCommand
{
    public static int Run(Dictionary<string, string> options)
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
        var type = ParseThrowType(options.GetValueOrDefault("type", "stand"));

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

        // --vel x,y,z replays a recorded launch exactly: the plugin captures the
        // engine's own release position and velocity, so feeding those straight
        // into the integrator compares our physics against a real throw without
        // any DeriveInitial guesswork about pitch/strength/eye height.
        if (options.TryGetValue("vel", out var velRaw))
        {
            var vel = ParseVec(velRaw);
            var replay = GrenadeTrajectory.SimulateExactRaw(
                BuildGrenadeCollider(mesh, min, max), eye, vel, constants);
            Console.WriteLine($"replay from ({eye.X:F0},{eye.Y:F0},{eye.Z:F0}) vel ({vel.X:F0},{vel.Y:F0},{vel.Z:F0})");
            Console.WriteLine($"rest (exact): ({replay.RestPoint.X:F1},{replay.RestPoint.Y:F1},{replay.RestPoint.Z:F1})  bounces {replay.Bounces}  flight {replay.FlightTime:F2}s  lost {replay.Lost}");
            return 0;
        }

        var result = GrenadeTrajectory.Simulate(grid, new ThrowSpec(eye, yaw, pitch, type, strength), constants);
        var traceLines = options.ContainsKey("trace") ? new List<string>() : null;
        var exact = GrenadeTrajectory.SimulateExact(BuildGrenadeCollider(mesh, min, max), new ThrowSpec(eye, yaw, pitch, type, strength), constants, traceLines);
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
}
