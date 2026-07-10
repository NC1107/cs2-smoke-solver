using System.Numerics;
using System.Text.Json;
using SmokeSolver.Sim;

namespace SmokeSolver.Sim.Tests;

/// <summary>
/// Replays real de_dust2 engine captures through the exact simulator and checks
/// the predicted rest position. The calibrated median error is ~1u; the 48u gate
/// catches gross physics regressions without flaking on the known slope cases.
/// </summary>
public class GoldenReplayTests
{
    const float RestTolerance = 48f;
    const float MinHorizontalDistance = 500f;
    const int MaxLinesToScan = 200;
    const int MaxReplays = 5;

    static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SmokeSolver.sln")))
            {
                return dir.FullName;
            }
        }
        return null;
    }

    [Fact]
    public void RealDust2CapturesReplayWithinTolerance()
    {
        // xunit 2.x has no dynamic skip; returning keeps the suite green on
        // checkouts without the extracted map or the capture telemetry.
        var root = FindRepoRoot();
        if (root == null)
        {
            return;
        }
        var geoPath = Path.Combine(root, "data", "de_dust2.s2geo");
        var capturesPath = Path.Combine(root, "data", "calib", "captures-20260710-081912.jsonl");
        if (!File.Exists(geoPath) || !File.Exists(capturesPath))
        {
            return;
        }

        var mesh = CollisionMesh.Load(geoPath);
        var filter = mesh.GrenadeSolidFilter();
        var replayed = 0;

        // The captures file is hundreds of MB; stream only the first lines and
        // keep real flights (long horizontal travel), not drops at the feet.
        using var reader = new StreamReader(capturesPath);
        for (var line = 0; line < MaxLinesToScan && replayed < MaxReplays; line++)
        {
            var text = reader.ReadLine();
            if (text == null)
            {
                break;
            }
            using var record = JsonDocument.Parse(text);
            var capture = record.RootElement;
            if (!capture.GetProperty("detonated").GetBoolean())
            {
                continue;
            }
            var start = ReadVector(capture.GetProperty("start"));
            var velocity = ReadVector(capture.GetProperty("velocity"));
            var rest = ReadVector(capture.GetProperty("rest"));
            if (new Vector2(rest.X - start.X, rest.Y - start.Y).Length() <= MinHorizontalDistance)
            {
                continue;
            }

            var margin = new Vector3(600f);
            var collider = new TriangleCollider(
                mesh,
                Vector3.Min(start, rest) - margin,
                Vector3.Max(start, rest) + margin,
                filter);
            var result = GrenadeTrajectory.SimulateExactRaw(collider, start, velocity);

            Assert.False(result.Lost, $"capture line {line + 1}: replay lost the grenade (start {start}, recorded rest {rest})");
            var error = Vector3.Distance(result.RestPoint, rest);
            Assert.True(error <= RestTolerance,
                $"capture line {line + 1}: predicted rest {result.RestPoint} is {error:F1}u from recorded {rest}");
            replayed++;
        }
        Assert.True(replayed > 0, $"no qualifying capture in the first {MaxLinesToScan} lines");
    }

    static Vector3 ReadVector(JsonElement element) =>
        new(element[0].GetSingle(), element[1].GetSingle(), element[2].GetSingle());
}
